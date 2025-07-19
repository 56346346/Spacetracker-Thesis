using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace SpaceTracker
{
    [SupportedOSPlatform("windows")]
    public class PullEventHandler : IExternalEventHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Document> _queue = new();
        private readonly ExternalEvent _event;

        public PullEventHandler()
        {
            _event = ExternalEvent.Create(this);
        }

        public void RequestPull(Document doc)
        {
            _queue.Enqueue(doc);
            if (!_event.IsPending)
                _event.Raise();
        }

        public string GetName() => "AutoPull";

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var doc))
            {
                try
                {
 PullCommand.RunPull(doc, showDialog: false);
                    Logger.LogToFile("PullEventHandler: PullCommand executed", "sync.log");
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("AutoPullCommand", ex);
                }

                try
                {
                    new GraphPuller().PullRemoteChanges(doc, SessionManager.CurrentUserId).GetAwaiter().GetResult();
                    Logger.LogToFile("PullEventHandler: GraphPuller executed", "sync.log");
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("AutoPullGraphPuller", ex);
                                    }
            }
              if (!_queue.IsEmpty)
                _event.Raise();
        }
    }
}