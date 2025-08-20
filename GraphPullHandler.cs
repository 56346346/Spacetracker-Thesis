using Autodesk.Revit.UI;
using System;
using Autodesk.Revit.DB;
using System.Collections.Concurrent;


namespace SpaceTracker
{
    public class GraphPullHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Document> _queue = new();
        internal ExternalEvent ExternalEvent { get; set; }
        private readonly GraphPuller _puller = new();

        public void RequestPull(Document doc)
        {
            _queue.Enqueue(doc);
            if (ExternalEvent != null && !ExternalEvent.IsPending)
                ExternalEvent.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var doc))
            {
                if (doc != null)
                {
                    Handle(doc, CommandManager.Instance.SessionId);
                }
            }

            if (!_queue.IsEmpty && ExternalEvent != null)
                ExternalEvent.Raise();
        }

        public void Handle(Document doc, string sessionId)
        {
            using (var tx = new Transaction(doc, "SpaceTracker Pull"))
            {
                tx.Start();
                try
                {
                    // Delegation an bestehenden Puller (keine neue Klasse!)
                    _puller.ApplyPendingWallChanges(doc, sessionId);

                    tx.Commit();
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    Autodesk.Revit.UI.TaskDialog.Show("SpaceTracker Pull", $"Fehler beim Pull: {ex.Message}");
                    Logger.LogCrash("GraphPullHandler.Handle", ex);
                    throw;
                }
            }
        }

        public string GetName() => "GraphPullHandler";
    }
}
