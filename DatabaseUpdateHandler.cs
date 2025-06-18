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
                                     // für XDocument
using Autodesk.Revit.DB.Architecture;// für Room
using SpaceTracker.Utilities;


using SpaceTracker;

namespace SpaceTracker
{
    public class DatabaseUpdateHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<ChangeData> _changeQueue = new ConcurrentQueue<ChangeData>();
     
        private readonly SpaceExtractor _extractor;

        private readonly SolibriApiClient _solibriClient = new SolibriApiClient(SpaceTrackerClass.SolibriApiPort);


        private ExternalEvent _externalEvent;


        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return;
            List<ChangeData> processedChanges = new List<ChangeData>();

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

                // 3. Solibri REST API-Aufrufe
                string modelId = SpaceTrackerClass.SolibriModelUUID;
                if (string.IsNullOrEmpty(SpaceTrackerClass.SolibriRulesetId))
                    SpaceTrackerClass.SolibriRulesetId = _solibriClient
                        .ImportRulesetAsync("C:/Users/Public/Solibri/SOLIBRI/Regelsaetze/RegelnThesis/DeltaRuleset.cset")
                        .GetAwaiter().GetResult();
                _solibriClient.PartialUpdateAsync(modelId, ifcPath).GetAwaiter().GetResult();
                _solibriClient.CheckModelAsync(modelId, SpaceTrackerClass.SolibriRulesetId).GetAwaiter().GetResult();
             

                string bcfZip = _solibriClient.ExportBcfAsync(modelId, Path.GetTempPath()).GetAwaiter().GetResult();





                // 4. BCF parsen und Issues zurück nach Neo4j
                ProcessBcfAndWriteToNeo4j(bcfZip);
            }

            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Delta-Prüfung", ex);
            }
        }




        public string GetName() => "SpaceTracker Real-Time Sync";

        public void EnqueueChange(ChangeData data)
        {
            _changeQueue.Enqueue(data);
            RaiseEvent();
        }

        private void ProcessBcfAndWriteToNeo4j(string bcfZipPath)
        {
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

                foreach (var guid in components)
                {
                    string cy = $@"
                MERGE (e {{ ifcGuid: '{guid}' }})
                MERGE (i:Issue {{ title: '{title}', description: '{desc}' }})
                MERGE (e)-[:HAS_ISSUE]->(i)";
                    CommandManager.Instance.cypherCommands.Enqueue(cy);
                }
            }
        }


        public void Initialize()
        {
            _externalEvent = ExternalEvent.Create(this);
        }

        public DatabaseUpdateHandler(SpaceExtractor extractor)
        {
            
            _extractor = extractor;
            _externalEvent = ExternalEvent.Create(this);
        }


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
        public List<Element> ModifiedElements { get; set; } = new List<Element>();
    }

}