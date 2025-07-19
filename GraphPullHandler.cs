using Autodesk.Revit.UI;

namespace SpaceTracker
{
    public class GraphPullHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc != null && !doc.IsReadOnly)
            {
                new GraphPuller().PullRemoteChanges(doc, SessionManager.CurrentUserId);
            }
        }
        public string GetName() => "GraphPullHandler";
    }
}