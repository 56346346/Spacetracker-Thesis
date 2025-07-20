using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.IO.Compression;
using System.Xml.Linq;
using System.IO;                     // für Path
using Autodesk.Revit.DB.Architecture;// für Room
using SpaceTracker;

namespace SpaceTracker
{
    public class DatabaseUpdateHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<ChangeData> _changeQueue = new ConcurrentQueue<ChangeData>();

        private readonly SpaceExtractor _extractor;

        private readonly SolibriApiClient _solibriClient = new SolibriApiClient(SpaceTrackerClass.SolibriApiPort);

        private static readonly object _lockObj = new object();
        private static bool _solibriBusy;

        private ExternalEvent _externalEvent;
        private readonly CypherPushHandler _pushHandler = new CypherPushHandler();
        private ExternalEvent _pushEvent;

        // Wird aufgerufen, wenn Änderungen verarbeitet werden sollen.
        // Erstellt IFC-Teilexporte und triggert anschließend die Solibri-Prüfung.    
        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return;
            List<ChangeData> processedChanges = new List<ChangeData>();
            Logger.LogToFile("DatabaseUpdateHandler Execute start", "concurrency.log");
            try
            {
                // 1. Prozessiere alle wartenden Changes
                while (_changeQueue.TryDequeue(out var change))
                {
                    processedChanges.Add(change);
                    _extractor.UpdateGraph(doc,
                                           change.AddedElements,
                                           change.DeletedElementIds,
                                           change.ModifiedElements);
                }

            }
            catch (Exception ex)
            {
                // Absichern gegen jeden Fehler im Handler
                Logger.LogCrash("ExternalEvent", ex);
            }


            try
            {
                // 1. Betroffene + Kontext-ElementIds sammeln
                var deltaIds = processedChanges
                    .SelectMany(c => c.AddedElements.Select(e => e.Id)
                        .Concat(c.ModifiedElements.Select(e => e.Id)))
                    .Distinct()
                    .ToList();

                var removedGuids = processedChanges
               .SelectMany(c => c.DeletedUids)
               .Distinct()
               .ToList();


                // Beispiel: für Räume angrenzende Wände ergänzen
                foreach (var change in processedChanges)
                {
                    foreach (var room in change.AddedElements.OfType<Room>()
                        .Concat(change.ModifiedElements.OfType<Room>()))
                    {
                        var boundaries = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                        foreach (var segList in boundaries)
                            foreach (var seg in segList)
                                if (!deltaIds.Contains(seg.ElementId))
                                    deltaIds.Add(seg.ElementId);
                    }
                }

                // 2. IFC-Subset exportieren
                string ifcPath = _extractor.ExportIfcSubset(app.ActiveUIDocument.Document, deltaIds);
                var guidMap = _extractor.MapIfcGuidsToRevitIds(ifcPath, deltaIds);

                // 3. Solibri REST API-Aufrufe asynchron verarbeiten
                _ = Task.Run(async () =>
              {
                  Logger.LogToFile("Solibri async task start", "concurrency.log");
                  bool runSolibri = false;

                  try
                  {
                      lock (_lockObj)
                      {
                          if (_solibriBusy)
                          {
                              Logger.LogToFile("Solibri-Prüfung ausgelassen, da bereits aktiv.");
                              return;
                          }
                      }

                      if (!await _solibriClient.PingAsync().ConfigureAwait(false))
                      {
                          Logger.LogToFile("Solibri REST API nicht erreichbar, überspringe Delta-Prüfung");
                          return;
                      }

                      string modelId = SpaceTrackerClass.SolibriModelUUID;
                      if (string.IsNullOrEmpty(SpaceTrackerClass.SolibriRulesetId))
                      {
                          SpaceTrackerClass.SolibriRulesetId = await _solibriClient
                              .ImportRulesetAsync("C:/Users/Public/Solibri/SOLIBRI/Regelsaetze/RegelnThesis/DeltaRuleset.cset")
                              .ConfigureAwait(false);
                      }

                      lock (_lockObj)
                      {
                          if (_solibriBusy)
                          {
                              Logger.LogToFile("Solibri-Prüfung ausgelassen, da bereits aktiv.");
                              return;
                          }
                          _solibriBusy = true;
                          runSolibri = true;
                      }

                      modelId = await _solibriClient.PartialUpdateAsync(modelId, ifcPath).ConfigureAwait(false);
                      SpaceTrackerClass.SolibriModelUUID = modelId;
                      if (removedGuids.Count > 0)
                          await _solibriClient.DeleteComponentsAsync(modelId, removedGuids).ConfigureAwait(false);
                      await _solibriClient.CheckModelAsync(modelId, SpaceTrackerClass.SolibriRulesetId).ConfigureAwait(false);
                      bool done = await _solibriClient.WaitForCheckCompletionAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                      if (!done)
                      {
                          Logger.LogToFile("Solibri Prüfung hat das Zeitlimit überschritten", "solibri.log");
                          return;
                      }
                      var bcfDir = Path.Combine(Path.GetTempPath(), CommandManager.Instance.SessionId);
                      string bcfZip = await _solibriClient.ExportBcfAsync(bcfDir).ConfigureAwait(false);
                      Debug.WriteLine($"[DatabaseUpdateHandler] BCF results stored at {bcfZip}");
                      var severity = ProcessBcfAndWriteToNeo4j(bcfZip, guidMap);
                      switch (severity)
                      {
                          case IssueSeverity.Error:
                              SpaceTrackerClass.SetStatusIndicator(SpaceTrackerClass.StatusColor.Red);
                              Debug.WriteLine("[DatabaseUpdateHandler] Solibri issues detected: error");

                              break;
                          case IssueSeverity.Warning:
                              SpaceTrackerClass.SetStatusIndicator(SpaceTrackerClass.StatusColor.Yellow);
                              Debug.WriteLine("[DatabaseUpdateHandler] Solibri issues detected: warning");

                              break;
                          default:
                              SpaceTrackerClass.SetStatusIndicator(SpaceTrackerClass.StatusColor.Green);
                              Debug.WriteLine("[DatabaseUpdateHandler] No Solibri issues detected");

                              break;
                      }
                  }
                  catch (Exception ex)
                  {
                      Logger.LogCrash("Solibri Delta-Prüfung", ex);
                  }
                  finally
                  {
                      if (runSolibri)
                      {
                          lock (_lockObj)
                          {
                              _solibriBusy = false;
                          }
                      }
                      if (_pushEvent != null && !_pushEvent.IsPending)
                          _pushEvent.Raise();
                      Logger.LogToFile("Solibri async task end", "concurrency.log");
                  }
              });
            }

            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Delta-Prüfung", ex);
            }
            Logger.LogToFile("DatabaseUpdateHandler Execute finished", "concurrency.log");
        }



        // Name des ExternalEvents.
        public string GetName() => "SpaceTracker Real-Time Sync";

        // Reicht eine Änderung zur späteren Verarbeitung ein und startet das Event.
        public void EnqueueChange(ChangeData data)
        {
            _changeQueue.Enqueue(data);
            RaiseEvent();
        }

        private enum IssueSeverity { None, Warning, Error }
        // Wertet eine BCF-Datei aus, schreibt gefundene Issues nach Neo4j und
        // gibt die schwerste aufgetretene Stufe zurück.
        private static IssueSeverity ProcessBcfAndWriteToNeo4j(string bcfZipPath, Dictionary<string, ElementId> guidMap)
        {
            IssueSeverity worst = IssueSeverity.None;
            var severityMap = new Dictionary<ElementId, string>();
            var session = SessionManager.OpenSessions.Values.FirstOrDefault();
            var doc = session?.Document;

            using var archive = ZipFile.OpenRead(bcfZipPath);
            foreach (var entry in archive.Entries.Where(e => e.Name.Equals("markup.bcf", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = entry.Open();
                var xdoc = XDocument.Load(stream);

                var components = xdoc.Descendants("Component")
                    .Select(x => (string)x.Attribute("IfcGuid"))
                    .Where(g => !string.IsNullOrEmpty(g))
                    .ToList();

                var title = xdoc.Descendants("Title").FirstOrDefault()?.Value ?? "Issue";
                var desc = xdoc.Descendants("Description").FirstOrDefault()?.Value ?? "";
                string sevText = xdoc.Descendants("Severity").FirstOrDefault()?.Value
                              ?? xdoc.Descendants("Priority").FirstOrDefault()?.Value;

                IssueSeverity sev = IssueSeverity.None;
                if (!string.IsNullOrEmpty(sevText))
                {
                    if (int.TryParse(sevText, out int sevNum))
                    {
                        if (sevNum >= 80) sev = IssueSeverity.Error;
                        else if (sevNum >= 40) sev = IssueSeverity.Warning;
                    }
                    else
                    {
                        if (sevText.Equals("high", StringComparison.OrdinalIgnoreCase) ||
                            sevText.Equals("critical", StringComparison.OrdinalIgnoreCase) ||
                            sevText.Equals("error", StringComparison.OrdinalIgnoreCase))
                            sev = IssueSeverity.Error;
                        else if (sevText.Equals("medium", StringComparison.OrdinalIgnoreCase) ||
                                 sevText.Equals("warning", StringComparison.OrdinalIgnoreCase) ||
                                 sevText.Equals("moderate", StringComparison.OrdinalIgnoreCase))
                            sev = IssueSeverity.Warning;
                    }
                }
                if (sev > worst)
                    worst = sev;
                foreach (var guid in components)
                {
                      guidMap.TryGetValue(guid, out var revitId);
                    string idPart = revitId != ElementId.InvalidElementId ? $", elementId: {revitId.Value}" : string.Empty;
                    string cy = $@"
                MERGE (e {{ ifcGuid: '{guid}'{idPart} }})
                MERGE (i:Issue {{ title: '{title}', description: '{desc}' }})
                MERGE (e)-[:HAS_ISSUE]->(i)";
                    CommandManager.Instance.cypherCommands.Enqueue(cy);

                    if (doc != null)
                    {
                        var elem = doc.GetElement(guid);
                        if (elem != null)
                        {
                            string color = sev == IssueSeverity.Error ? "RED" :
                                sev == IssueSeverity.Warning ? "YELLOW" : "GREEN";
                            if (severityMap.TryGetValue(elem.Id, out string existing))
                            {
                                if (existing == "YELLOW" && color == "RED")
                                    severityMap[elem.Id] = color;
                            }
                            else if (color != "GREEN")
                            {
                                severityMap[elem.Id] = color;
                            }
                        }
                    }
                }
            }

            if (severityMap.Count > 0)
                SpaceTrackerClass.MarkElementsBySeverity(severityMap);
            return worst;
        }

        // Wertet eine BCF-Datei aus, schreibt gefundene Issues nach Neo4j und
        // gibt die schwerste aufgetretene Stufe zurück.
        public void Initialize()
        {
            _externalEvent = ExternalEvent.Create(this);
            _pushEvent = ExternalEvent.Create(_pushHandler);
        }

        /// <summary>
        /// Exposes the push event so other classes can trigger an immediate
        /// transfer of queued Cypher commands to Neo4j.
        /// </summary>
        // Stößt einen sofortigen Push aller gesammelten Cypher-Befehle an.

        public void TriggerPush()
        {
            if (_pushEvent != null && !_pushEvent.IsPending)
            {
                _pushEvent.Raise();
            }
        }

        // Konstruktor, benötigt einen SpaceExtractor zur Graph-Aktualisierung.

        public DatabaseUpdateHandler(SpaceExtractor extractor)
        {

            _extractor = extractor;
            _externalEvent = ExternalEvent.Create(this);
            _pushEvent = ExternalEvent.Create(_pushHandler);
        }

        // Hebt das ExternalEvent an, falls es nicht bereits aussteht
        private void RaiseEvent()
        {
            if (_externalEvent != null && !_externalEvent.IsPending)
            {
                _externalEvent.Raise();
            }


        }



    }

    public class ChangeData
    {
        public List<Element> AddedElements { get; set; } = new List<Element>();
        public List<ElementId> DeletedElementIds { get; set; } = new List<ElementId>();
        public List<string> DeletedUids { get; set; } = new List<string>();

        public List<Element> ModifiedElements { get; set; } = new List<Element>();
    }

}