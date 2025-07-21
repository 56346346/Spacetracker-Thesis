using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Concurrent;

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
                    solibriClient.CheckModelAsync(SpaceTrackerClass.SolibriModelUUID, SpaceTrackerClass.SolibriRulesetId)
                                 .GetAwaiter()
                                 .GetResult();
                    solibriClient.WaitForCheckCompletionAsync(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2))
                                 .GetAwaiter()
                                 .GetResult();
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