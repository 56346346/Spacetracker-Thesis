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
                if (doc == null)
                    continue;

                    ExternalEvent.Create(
                         new GraphPullEventHandler(_puller, doc, CommandManager.Instance.SessionId),
                         "GraphPull")
                         .Raise();
            }
        }

        public string GetName() => "GraphPullEvent";
    }
}