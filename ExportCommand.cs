using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.IO;
using System.Threading.Tasks;
using SpaceTracker;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;

namespace SpaceTracker
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportCommand : IExternalCommand
    {

        // Exportiert das komplette Modell nach Neo4j und legt die erzeugten
        // Cypher-Befehle optional im Benutzerprofil ab.
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Warn user if they try to push during pull
            if (CommandManager.Instance.IsPullInProgress)
            {
                Logger.LogToFile("PUSH BLOCKED: Manual push attempted during pull operation", "sync.log");
                Autodesk.Revit.UI.TaskDialog.Show("SpaceTracker", 
                    "Push-Operation nicht möglich während Pull-Vorgang.\n" +
                    "Bitte warten Sie bis der Pull abgeschlossen ist.");
                return Result.Cancelled;
            }

            UIApplication uiApp = commandData.Application;
            // UIDocument und Document daraus:
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            if (uiDoc == null)
            {
                message = "Kein aktives Revit-Dokument gefunden.";
                return Result.Failed;
            }
            var cmdMgr = CommandManager.Instance;
            var connector = cmdMgr.Neo4jConnector;
            string sessionId = cmdMgr.SessionId;


            Document doc = uiDoc.Document;

            // 1) Gesamtes Modell exportieren
            var extractor = new SpaceExtractor(cmdMgr);
            cmdMgr.cypherCommands = new ConcurrentQueue<string>();
            extractor.CreateInitialGraph(doc);
            var commands = cmdMgr.cypherCommands.Distinct().ToList();
            cmdMgr.cypherCommands = new ConcurrentQueue<string>();

            if (commands.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Push", "Keine Änderungen zum Übertragen vorhanden.");
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
                Autodesk.Revit.UI.TaskDialog.Show("Exportfehler", "Keine Cypher-Befehle gefunden.");
                return Result.Failed;
            }

            try
            {
                connector.PushChangesAsync(commands, sessionId, doc).GetAwaiter().GetResult();
                connector.CleanupObsoleteChangeLogsAsync().GetAwaiter().GetResult();

                var errs = SolibriRulesetValidator.Validate(doc).GetAwaiter().GetResult();
                var sev = errs.Count == 0 ? Severity.Info : errs.Max(e => e.Severity);
                SpaceTrackerClass.UpdateConsistencyCheckerButton(sev);

                Autodesk.Revit.UI.TaskDialog.Show("Push", "Änderungen erfolgreich an Neo4j übertragen.");
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Push-Fehler",
                    $"Export nach Neo4j fehlgeschlagen: {ex.Message}");
                Logger.LogCrash("ExportCommand", ex);
            }


            return Result.Succeeded;
        }
    }
}
