using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Runtime.Versioning;

namespace SpaceTracker
{
    [SupportedOSPlatform("windows")]
    public class PullEventHandler : IExternalEventHandler
    {
        private Document _doc;
        private readonly ExternalEvent _event;

        public PullEventHandler()
        {
            _event = ExternalEvent.Create(this);
        }

        public void RequestPull(Document doc)
        {
            _doc = doc;
            if (!_event.IsPending)
                _event.Raise();
        }

        public string GetName() => "AutoPull";

        public void Execute(UIApplication app)
        {
            if (_doc == null)
                return;
            try
            {
                PullCommand.PullChanges(_doc);
            }
            catch (Exception ex)
            {
                Logger.LogCrash("AutoPull", ex);
            }
            finally
            {
                _doc = null;
            }
        }
    }
}