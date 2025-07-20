using Autodesk.Revit.UI;
using System;

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
                  // Trigger Solibri consistency check after pull
                var solibriClient = new SolibriApiClient(SpaceTrackerClass.SolibriApiPort);
                solibriClient.CheckModelAsync(SpaceTrackerClass.SolibriModelUUID, SpaceTrackerClass.SolibriRulesetId)
                             .GetAwaiter().GetResult();
                solibriClient.WaitForCheckCompletionAsync(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2))
                             .GetAwaiter().GetResult();
            }
        }
        public string GetName() => "GraphPullHandler";
    }
}