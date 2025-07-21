using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace SpaceTracker
{
    /// <summary>
    /// Executes the actual graph pull on the Revit API thread.
    /// </summary>
    public class GraphPullEvent : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Document> _queue = new();
        private readonly ExternalEvent _event;
        private readonly GraphPuller _puller = new();

        public GraphPullEvent()
        {
            _event = ExternalEvent.Create(this);
        }

        /// <summary>
        /// Schedules a pull for the given document.
        /// </summary>
        public void Raise(Document doc)
        {
            _queue.Enqueue(doc);
            if (!_event.IsPending)
                _event.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var doc))
            {
                if (doc == null || doc.IsReadOnly || doc.IsModifiable)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Graph Pull", "Dokument nicht bereit f\u00fcr Pull.");
                    continue;
                }

                try
                {
                    _puller.PullRemoteChanges(doc, CommandManager.Instance.SessionId)
                           .GetAwaiter()
                           .GetResult();

                    var solibriClient = new SolibriApiClient(SpaceTrackerClass.SolibriApiPort);

                    Task.Run(async () =>
                    {
                        Logger.LogToFile("Starte Solibri Check (Graph Pull)", "solibri.log");
                        try
                        {
                            var results = await solibriClient
                                .RunRulesetCheckAsync(SpaceTrackerClass.SolibriModelUUID)
                                .ConfigureAwait(false);

                            var status = SpaceTrackerClass.StatusColor.Green;
                            foreach (var clash in results)
                            {
                                var sev = clash.Severity?.Trim().ToUpperInvariant();
                                if (sev == "ROT" || sev == "RED" || sev == "ERROR" ||
                                    sev == "HIGH" || sev == "CRITICAL")
                                {
                                    status = SpaceTrackerClass.StatusColor.Red;
                                    break;
                                }
                                if (sev == "GELB" || sev == "YELLOW" || sev == "WARNING" ||
                                    sev == "MEDIUM")
                                {
                                    if (status != SpaceTrackerClass.StatusColor.Red)
                                        status = SpaceTrackerClass.StatusColor.Yellow;
                                }
                            }
                            SpaceTrackerClass.SetStatusIndicator(status);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogCrash("Solibri Modellprüfung (GraphPull)", ex);
                        }

                        Logger.LogToFile("Solibri Check (Graph Pull) abgeschlossen", "solibri.log");
                    });
                }
                catch (Exception ex)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("GraphPull Fehler", ex.Message);
                }
            }
        }

        public string GetName() => "GraphPullEvent";
    }
}