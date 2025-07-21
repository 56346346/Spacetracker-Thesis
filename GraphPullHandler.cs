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
        private readonly GraphPullEvent _pullEvent = new();

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
                    _pullEvent.Raise(doc);

                }
            }

            if (!_queue.IsEmpty && ExternalEvent != null)
                ExternalEvent.Raise();
        }

        public string GetName() => "GraphPullHandler";
    }
}
