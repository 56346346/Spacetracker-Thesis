using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;

namespace SpaceTracker
{
    public class MarkSeverityHandler : IExternalEventHandler
    {
        public Dictionary<ElementId, string> SeverityMap { get; set; } = new();

        public void Execute(UIApplication app)
        {
            if (SeverityMap != null && SeverityMap.Count > 0)
                SpaceTrackerClass.MarkElementsBySeverity(SeverityMap);
            SeverityMap = new();
        }

        public string GetName() => "MarkSeverityHandler";
    }
}