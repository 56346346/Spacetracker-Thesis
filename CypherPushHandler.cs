using System;
using Autodesk.Revit.UI;
using System.Linq;

#nullable enable



namespace SpaceTracker
{
    public class CypherPushHandler : IExternalEventHandler
    {
        public async void Execute(UIApplication app)
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
                    
                    var solibriClient = new SolibriApiClient(SpaceTrackerClass.SolibriApiPort);
                    _ = Task.Run(async () =>
                    {
                        Logger.LogToFile("Starte Solibri Check (Push)", "solibri.log");
                        try
                        {
                            var results = await solibriClient
                                .RunRulesetCheckAsync(SpaceTrackerClass.SolibriModelUUID)
                                .ConfigureAwait(false);

                            var status = SpaceTrackerClass.StatusColor.Green;
                            foreach (var clash in results)
                            {
                                var sev2 = clash.Severity?.Trim().ToUpperInvariant();
                                if (sev2 == "ROT" || sev2 == "RED" || sev2 == "ERROR" ||
                                    sev2 == "HIGH" || sev2 == "CRITICAL")
                                {
                                    status = SpaceTrackerClass.StatusColor.Red;
                                    break;
                                }
                                if (sev2 == "GELB" || sev2 == "YELLOW" || sev2 == "WARNING" ||
                                    sev2 == "MEDIUM")
                                {
                                    if (status != SpaceTrackerClass.StatusColor.Red)
                                        status = SpaceTrackerClass.StatusColor.Yellow;
                                }
                            }
                            SpaceTrackerClass.SetStatusIndicator(status);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogCrash("Solibri Modellprüfung (Push)", ex);
                        }

                        Logger.LogToFile("Solibri Check (Push) abgeschlossen", "solibri.log");
                    });
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