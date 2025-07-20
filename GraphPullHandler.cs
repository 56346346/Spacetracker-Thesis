using Autodesk.Revit.UI;
using System;
using Autodesk.Revit.DB;
using System.Collections.Concurrent;


namespace SpaceTracker
{
    public class GraphPullHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Document> _queue = new();
        internal ExternalEvent? ExternalEvent { get; set; }

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
                if (doc != null && !doc.IsReadOnly && !doc.IsModifiable)
                {
                    new GraphPuller().PullRemoteChanges(doc, CommandManager.Instance.SessionId).GetAwaiter().GetResult();

                    var solibriClient = new SolibriApiClient(SpaceTrackerClass.SolibriApiPort);
                    solibriClient.CheckModelAsync(SpaceTrackerClass.SolibriModelUUID, SpaceTrackerClass.SolibriRulesetId)
                                 .GetAwaiter().GetResult();
                    solibriClient.WaitForCheckCompletionAsync(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2))
                                 .GetAwaiter().GetResult();
                }
            }

            if (!_queue.IsEmpty && ExternalEvent != null)
                ExternalEvent.Raise();
        }

        public string GetName() => "GraphPullHandler";
    }
}
