using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.IO;
using System.Threading.Tasks;
using SpaceTracker;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SpaceTracker
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PushCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            var cmdMgr = CommandManager.Instance;
            var connector = cmdMgr.Neo4jConnector;
            string sessionId = cmdMgr.SessionId;
              UIApplication uiApp = commandData.Application;
            Document doc = uiApp.ActiveUIDocument?.Document;

            var commands = cmdMgr.cypherCommands.ToList();
            cmdMgr.cypherCommands = new ConcurrentQueue<string>();

            var changes = new List<(string Command, string Path)>();
            foreach (var cmd in commands)
            {
                string cachePath = ChangeCacheHelper.WriteChange(cmd);
                changes.Add((cmd, cachePath));
            }


            if (commands.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Push", "Keine Änderungen zum Übertragen vorhanden.");
                return Result.Succeeded;
            }


            string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SpaceTracker",
                    "neo4j_cypher.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, commands);

            _ = Task.Run(async () =>
            {
            
            try
                {
 await connector.PushChangesAsync(changes, sessionId, Environment.UserName, doc).ConfigureAwait(false);                         .ConfigureAwait(false);
                 
                    await connector.CleanupObsoleteChangeLogsAsync().ConfigureAwait(false);
                 
                    // Nach dem erfolgreichen Push Validierung starten und Ampel setzen
                    if (doc != null)
                    {
                        var errs = SolibriRulesetValidator.Validate(doc);
                        var sev = errs.Count == 0 ? Severity.Info : errs.Max(e => e.Severity);
                        SpaceTrackerClass.UpdateConsistencyCheckerButton(sev);
                    }

                    Autodesk.Revit.UI.TaskDialog.Show("Push", "Änderungen erfolgreich an Neo4j übertragen.");
                }
                catch (Exception ex)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Push-Fehler",
                        $"Export nach Neo4j fehlgeschlagen: {ex.Message}");
                }
            });
            return Result.Succeeded;
            }
        }
    }

