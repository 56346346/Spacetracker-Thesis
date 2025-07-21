using System;
using Autodesk.Revit.UI;
using System.Linq;

#nullable enable



namespace SpaceTracker
{
    public class CypherPushHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                await CommandManager.Instance.ProcessCypherQueueAsync(doc);


                if (doc != null)
                {
                     await SpaceTrackerClass.SolibriLock.WaitAsync();
                    try
                    {
                        var errs = await SolibriRulesetValidator.Validate(doc);
                        var sev = errs.Count == 0 ? Severity.Info : errs.Max(e => e.Severity);
                        SpaceTrackerClass.UpdateConsistencyCheckerButton(sev);
                    }
                    finally
                    {
                        SpaceTrackerClass.SolibriLock.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Neo4j Push", ex);
            }
        }

        public string GetName() => "Process Cypher Queue";
    }
}