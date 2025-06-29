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
 CommandManager.Instance.ProcessCypherQueueAsync(doc).GetAwaiter().GetResult();
                // Nach dem Push sofort die Solibri-Regeln validieren und die Ampel anpassen

                if (doc != null)
                {
                    var errs = SolibriRulesetValidator.Validate(doc);
                    var sev = errs.Count == 0 ? Severity.Info : errs.Max(e => e.Severity);
                    SpaceTrackerClass.UpdateConsistencyCheckerButton(sev);
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