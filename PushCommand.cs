using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.IO;
using System.Threading.Tasks;
using SpaceTracker;
using System.Linq;
using System.Collections.Concurrent;

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

            var commands = cmdMgr.cypherCommands.ToList();
            cmdMgr.cypherCommands = new ConcurrentQueue<string>();

            if (commands.Count == 0)
            {
                TaskDialog.Show("Push", "Keine Änderungen zum Übertragen vorhanden.");
                return Result.Succeeded;
            }


            string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SpaceTracker",
                    "neo4j_cypher.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, commands);

            try
            {
                // 3) Sync-Push (kein Task.Run mehr!)
                connector.PushChangesAsync(commands, sessionId)
                         .GetAwaiter().GetResult();

                // 4) Unmittelbar danach: Aufräumen alter ChangeLogs
                connector.CleanupObsoleteChangeLogsAsync()
                         .GetAwaiter().GetResult();

                TaskDialog.Show("Push", "Änderungen erfolgreich an Neo4j übertragen.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Push-Fehler",
                    $"Export nach Neo4j fehlgeschlagen: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
