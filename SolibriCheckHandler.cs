using System;
using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SpaceTracker
{
    public class SolibriCheckHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<(Document Doc, ElementId Id)> _queue = new();
        private readonly ExternalEvent _event;

        public SolibriCheckHandler()
        {
            _event = ExternalEvent.Create(this);
        }

        public void ScheduleCheck(Document doc, ElementId id)
        {
            _queue.Enqueue((doc, id));
            if (!_event.IsPending)
                _event.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var item))
            {
                try
                {
                    SolibriChecker.CheckElementAsync(item.Id, item.Doc).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("SolibriCheck", ex);
                }
            }
        }

        public string GetName() => "Solibri Element Check";
    }
}