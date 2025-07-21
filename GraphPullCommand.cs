using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Runtime.Versioning;

namespace SpaceTracker
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    [SupportedOSPlatform("windows")]
    public class GraphPullCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null)
                return Result.Failed;

            var puller = SpaceTrackerClass.GraphPullerInstance ?? new GraphPuller();
            puller.PullRemoteChanges(doc, CommandManager.Instance.SessionId)
                  .GetAwaiter().GetResult();
            return Result.Succeeded;
        }
    }
}