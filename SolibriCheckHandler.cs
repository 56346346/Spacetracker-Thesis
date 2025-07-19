using System;
using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.IO;

namespace SpaceTracker
{
    public class SolibriCheckHandler : IExternalEventHandler
    {
         private static readonly string logPath = Path.Combine("log", "SolibriCheckHandler.log");
        static SolibriCheckHandler()
        {
            MethodLogger.InitializeLog(nameof(SolibriCheckHandler));
        }

        private static void LogMethodCall(string methodName, Dictionary<string, object?> parameters)
        {
            MethodLogger.Log(nameof(SolibriCheckHandler), methodName, parameters);
        }
        private readonly ConcurrentQueue<(Document Doc, ElementId Id)> _queue = new();
        private readonly ExternalEvent _event;

        public SolibriCheckHandler()
        {
            _event = ExternalEvent.Create(this);
        }

        public void ScheduleCheck(Document doc, ElementId id)
        {
             LogMethodCall(nameof(ScheduleCheck), new()
            {
                ["doc"] = doc?.Title,
                ["id"] = id.Value
            });
            _queue.Enqueue((doc, id));
            if (!_event.IsPending)
                _event.Raise();
        }

        public void Execute(UIApplication app)
        {
                        LogMethodCall(nameof(Execute), new() { ["app"] = app?.ToString() ?? "null" });

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