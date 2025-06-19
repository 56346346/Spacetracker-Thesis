using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.Collections.Generic;
using System.IO;
using Neo4j.Driver;  // für IRecord .As<T> Extensions
using System.Threading.Tasks;
using System.Diagnostics;
using Autodesk.Revit.DB.Architecture;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB.Structure;
using System.Threading.Tasks;


namespace SpaceTracker
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PullCommand : IExternalCommand
    {

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return ExecuteAsync(commandData, message, elements).GetAwaiter().GetResult();
        }

        private async Task<Result> ExecuteAsync(ExternalCommandData commandData, string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            if (uiDoc == null)
            {
                message = "Kein aktives Revit-Dokument gefunden.";
                return Result.Failed;
            }
            Document doc = uiDoc.Document;
            var cmdMgr = CommandManager.Instance;
            var connector = cmdMgr.Neo4jConnector;

            string sessionId = cmdMgr.SessionId;
            List<IRecord> records;
            try
            {
                records = await connector.GetPendingChangeLogsAsync(sessionId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Pull-Fehler", $"Fehler beim Abrufen der Änderungen: {ex.Message}");
                return Result.Failed;
            }

            if (records.Count == 0)
            {

                SpaceTrackerClass.SetStatusIndicator(SpaceTrackerClass.StatusColor.Green);
                 TaskDialog.Show("Pull", "Keine neuen Änderungen vorhanden.");
                return Result.Succeeded;
            }
var entries = records.Select(r => new ChangeLogEntry
            {
            
             ElementId = r["elementId"].As<long>(),
                ChangeType = r["type"].As<string>(),
                Timestamp = DateTime.Parse(r["ts"].As<string>()),
                SessionId = r["sessionId"].As<string>()
            }).ToList();

            var dlg = new PullDialog(doc, entries, new PropertySyncService(connector), connector, sessionId);
            dlg.ShowDialog();
            return Result.Succeeded;
        }


    }
}