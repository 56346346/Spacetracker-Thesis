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
    public class ExportCommand : IExternalCommand
    {
        public Result Execute(
    ExternalCommandData commandData,
    ref string message,
    ElementSet elements)
        {
            var cmdMgr = CommandManager.Instance;
            var connector = cmdMgr.Neo4jConnector;
            string sessionId = cmdMgr.SessionId;

            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // 1) Gesamtes Modell exportieren
            var extractor = new SpaceExtractor(cmdMgr);
            cmdMgr.cypherCommands = new ConcurrentQueue<string>();
            extractor.CreateInitialGraph(doc);
            var commands = cmdMgr.cypherCommands.Distinct().ToList();
            cmdMgr.cypherCommands = new ConcurrentQueue<string>();

            if (commands.Count == 0)
            {
                TaskDialog.Show("Push", "Keine Änderungen zum Übertragen vorhanden.");
                return Result.Succeeded;
            }

            // 2) (Optional) Persistiere Befehle
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SpaceTracker",
                "neo4j_cypher.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, commands);

            if (!File.Exists(path))
            {
                TaskDialog.Show("Exportfehler", "Keine Cypher-Befehle gefunden.");
                return Result.Failed;
            }

             _ = Task.Run(async () =>
             {
             try
            {
                await connector.PushChangesAsync(commands, sessionId, Environment.UserName)
                     .ConfigureAwait(false);
                await connector.CleanupObsoleteChangeLogsAsync().ConfigureAwait(false);
                TaskDialog.Show("Push", "Änderungen erfolgreich an Neo4j übertragen.");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Push-Fehler",
                    $"Export nach Neo4j fehlgeschlagen: {ex.Message}");
            }
            });
            return Result.Succeeded;
        }
    }
}
