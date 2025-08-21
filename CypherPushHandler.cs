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
                    // SKIP SOLIBRI VALIDATION for initial session to prevent UI hanging
                    // Initial sessions with large models can cause IFC export to hang
                    var isInitialSession = CommandManager.Instance.IsInitialSession;
                    
                    if (!isInitialSession)
                    {
                        Logger.LogToFile("SOLIBRI VALIDATION: Starting for subsequent session", "solibri.log");
                        // BACKGROUND SOLIBRI VALIDATION: Run Solibri validation in background to prevent UI blocking
                        _ = Task.Run(async () =>
                        {
                            Logger.LogToFile("SOLIBRI VALIDATION: Starting background validation", "solibri.log");
                            await SpaceTrackerClass.SolibriLock.WaitAsync();
                            try
                            {
                                var errs = await SolibriRulesetValidator.Validate(doc);
                                var sev = errs.Count == 0 ? Severity.Info : errs.Max(e => e.Severity);
                                
                                Logger.LogToFile($"SOLIBRI VALIDATION: Completed with {errs.Count} errors, severity: {sev}", "solibri.log");
                                
                                // Note: UI update would need to be done via ExternalEvent for thread safety
                                // For now, just log the result
                            }
                            catch (Exception ex)
                            {
                                Logger.LogCrash("Background Solibri Validation", ex);
                            }
                            finally
                            {
                                SpaceTrackerClass.SolibriLock.Release();
                            }
                        });
                    
                        // BACKGROUND SOLIBRI API CHECK: Run second Solibri check in background 
                        _ = Task.Run(async () =>
                        {
                            Logger.LogToFile("SOLIBRI API CHECK: Starting background Solibri API check", "solibri.log");
                            try
                            {
                                var solibriClient = new SolibriApiClient(SpaceTrackerClass.SolibriApiPort);
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
                                Logger.LogToFile($"SOLIBRI API CHECK: Completed with status {status}", "solibri.log");
                            }
                            catch (Exception ex)
                            {
                                Logger.LogCrash("Background Solibri API Check", ex);
                            }
                        });
                    }
                    else
                    {
                        Logger.LogToFile("SOLIBRI VALIDATION: Skipped for initial session to prevent UI hanging", "solibri.log");
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