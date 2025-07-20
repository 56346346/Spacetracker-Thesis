using Autodesk.Revit.UI;

namespace SpaceTracker
{
    public class AutoPullHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc != null && !doc.IsReadOnly && !doc.IsModifiable)
            {
                // Reiner Neo4j-Pull
                new GraphPuller().PullRemoteChanges(doc, CommandManager.Instance.SessionId);
            }
        }

        public string GetName() => "AutoPullHandler";
    }
}