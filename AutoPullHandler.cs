using Autodesk.Revit.UI;
using Autodesk.Revit.DB;

namespace SpaceTracker;

public class AutoPullHandler : IExternalEventHandler
{
    public void Execute(UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document;
        if (doc != null && !doc.IsReadOnly)
        {
            new GraphPuller().PullRemoteChanges(doc, SessionManager.CurrentUserId).GetAwaiter().GetResult();
        }
    }

    public string GetName() => "AutoPullHandler";
}