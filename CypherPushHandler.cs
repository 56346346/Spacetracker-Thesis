using System;
using Autodesk.Revit.UI;

namespace SpaceTracker
{
    public class CypherPushHandler : IExternalEventHandler
    {
        public async void Execute(UIApplication app)
        {
            try
            {
                await CommandManager.Instance.ProcessCypherQueueAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Neo4j Push", ex);
            }
        }

        public string GetName() => "Process Cypher Queue";
    }
}