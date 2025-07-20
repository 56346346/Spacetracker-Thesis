using Autodesk.Revit.UI;

namespace SpaceTracker
{
    public class GraphPullHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc != null && !doc.IsReadOnly && !doc.IsModifiable)
            {
                new GraphPuller().PullRemoteChanges(doc, CommandManager.Instance.SessionId);
            }
        }
        public string GetName() => "GraphPullHandler";
    }
}