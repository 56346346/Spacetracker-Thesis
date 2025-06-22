using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.Collections.Generic;
using Neo4j.Driver;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;

namespace SpaceTracker
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ConsistencyCheckCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var cmdMgr = CommandManager.Instance;
            var connector = cmdMgr.Neo4jConnector;
            var parameters = new { session = cmdMgr.SessionId };
            string query = "MATCH (c:ChangeLog) " +
                           "WHERE c.sessionId <> $session AND c.acknowledged = false " +
                           "RETURN c.elementId AS id, c.type AS type, c.sessionId AS session";
            List<IRecord> records;
            try
            {
                records = Task.Run(() => connector.RunReadQueryAsync(query, parameters)).Result;
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Consistency Check", $"Fehler bei der Abfrage: {ex.Message}");
                return Result.Failed;
            }

            // Ermitteln der lokalen Pending-Änderungen
            var localPendingIds = new HashSet<long>();
            var localChangeType = new Dictionary<long, string>();
            foreach (var cmd in cmdMgr.cypherCommands.ToArray())  // Safely snapshot the queue
            {
                // Extrahiere ElementId aus dem Befehl
                long id = -1;
                int idx = cmd.IndexOf("ElementId");
                if (idx >= 0)
                {
                    // Zahl nach "ElementId"
                    string sub = cmd.Substring(idx);
                    // Entferne bis Ziffernfolge
                    sub = new string(sub.SkipWhile(ch => !char.IsDigit(ch) && ch != '-').ToArray());
                    string num = new string(sub.TakeWhile(ch => char.IsDigit(ch) || ch == '-').ToArray());
                    long.TryParse(num, out id);
                }
                if (id < 0) continue;
                localPendingIds.Add(id);
                // Änderungstyp bestimmen
                string lType;
                if (cmd.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) >= 0)
                    lType = "Delete";
                else if (cmd.IndexOf("MERGE", StringComparison.OrdinalIgnoreCase) >= 0 && cmd.IndexOf("MATCH", StringComparison.OrdinalIgnoreCase) == -1)
                    lType = "Insert";
                else
                    lType = "Modify";
                localChangeType[id] = lType;
            }

            bool conflict = false;
            bool remoteChanges = records.Count > 0;
            var conflictDetails = new List<string>();

            // Prüfe auf Konflikte mit lokalen Änderungen
            foreach (var rec in records)
            {
                long id = rec["id"].As<long>();
                string rType = rec["type"].As<string>();
                if (localPendingIds.Contains(id))
                {
                    string lType = localChangeType.ContainsKey(id) ? localChangeType[id] : "Modify";
                    // Beide Nutzer haben id geändert
                    if (!(rType == "Delete" && lType == "Delete"))
                    {
                        // Konflikt, wenn nicht beide das Element gelöscht haben
                        conflict = true;
                        // Beschreibung hinzufügen
                        string rDesc = (rType == "Delete") ? "gelöscht" : (rType == "Insert" ? "eingefügt" : "geändert");
                        string lDesc = (lType == "Delete") ? "gelöscht" : (lType == "Insert" ? "eingefügt" : "geändert");
                        conflictDetails.Add($"Element {id}: extern {rDesc}, lokal {lDesc}");
                    }
                }
            }

            // Ampel-Status setzen und Meldung ausgeben
            if (conflict)
            {
                // Konfliktsituation (Rot)
                SpaceTrackerClass.SetStatusIndicator(SpaceTrackerClass.StatusColor.Red);
                string detailText = conflictDetails.Count > 0
                    ? string.Join("\n", conflictDetails)
                    : "Siehe Änderungsprotokoll für Details.";
                Autodesk.Revit.UI.TaskDialog.Show("Consistency Check",
                    $"*** Konflikt erkannt! ***\n" +
                    $"Einige Elemente wurden sowohl lokal als auch von einem anderen Nutzer geändert.\n" +
                    $"{detailText}\n\nBitte Konflikte manuell lösen.");
            }
            else if (remoteChanges)
            {
                // Externe Änderungen vorhanden, aber kein direkter Konflikt (Gelb)
                SpaceTrackerClass.SetStatusIndicator(SpaceTrackerClass.StatusColor.Yellow);
                int count = records.Count;
                Autodesk.Revit.UI.TaskDialog.Show("Consistency Check",
                    $"Es liegen {count} neue Änderungen von anderen Nutzern vor.\n" +
                    $"Keine direkten Konflikte mit lokalen Änderungen erkannt.\n" +
                    $"Sie können einen Pull durchführen, um diese zu übernehmen.");
            }
            else
            {
                // Keine externen Änderungen -> konsistent (Grün)
                SpaceTrackerClass.SetStatusIndicator(SpaceTrackerClass.StatusColor.Green);
                // Hinweis, falls lokale Änderungen noch nicht gepusht
                string note = localPendingIds.Count > 0
                    ? "\n(Hinweis: Es gibt ungesicherte lokale Änderungen, bitte Push ausführen.)"
                    : "";
                Autodesk.Revit.UI.TaskDialog.Show("Consistency Check", "Das lokale Modell ist konsistent mit dem Neo4j-Graph." + note);

                if (localPendingIds.Count == 0)
                {
                    cmdMgr.LastSyncTime = DateTime.Now;
                    cmdMgr.PersistSyncTime();
                    try
                    {
                        Task.Run(() => connector.UpdateSessionLastSyncAsync(cmdMgr.SessionId, cmdMgr.LastSyncTime)).Wait();
                        Task.Run(() => connector.CleanupObsoleteChangeLogsAsync()).Wait();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ConsistencyCheck] Cleanup failed: {ex.Message}");
                    }
                }
            }
            return Result.Succeeded;
        }
    }
}
