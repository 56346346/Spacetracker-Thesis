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

            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            var cmdMgr = CommandManager.Instance;
            var connector = cmdMgr.Neo4jConnector;
            // Abfrage: ChangeLog-Einträge seit dem letzten Sync dieses Nutzers (ausgenommen eigene)
            string lastSyncStr = cmdMgr.LastSyncTime.ToString("o");
            string sessionId = cmdMgr.SessionId;
            var queryParams = new { lastSync = lastSyncStr, session = sessionId };
            string cypher = "MATCH (c:ChangeLog) " +
                            "WHERE c.timestamp > $lastSync AND c.sessionId <> $session " +
                            "RETURN c.elementId AS id, c.type AS type, c.user AS user, c.timestamp AS ts " +
                            "ORDER BY c.timestamp";
            List<IRecord> changeRecords;
            try
            {
                changeRecords = await connector.RunReadQueryAsync(cypher, queryParams).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Pull-Fehler", $"Fehler beim Abrufen der Änderungen: {ex.Message}");
                return Result.Failed;
            }

            if (changeRecords.Count == 0)
            {

                cmdMgr.LastSyncTime = DateTime.UtcNow;
                cmdMgr.PersistSyncTime();
                try
                {
                    await connector.UpdateSessionLastSyncAsync(sessionId, cmdMgr.LastSyncTime).ConfigureAwait(false);
                    await connector.CleanupObsoleteChangeLogsAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Pull] Fehler beim Session-Update: {ex.Message}");
                }
                TaskDialog.Show("Pull", "Der Neo4j-Graph ist bereits auf dem neuesten Stand (keine neuen Änderungen).");
                SpaceTrackerClass.SetStatusIndicator(SpaceTrackerClass.StatusColor.Green);
                return Result.Succeeded;
            }

            // Mapping der empfangenen Änderungen
            var remoteChanges = new List<(long id, string type, string user)>();
            foreach (var rec in changeRecords)
            {
                long id = rec["id"].As<long>();
                string type = rec["type"].As<string>();
                string user = rec["user"].As<string>();
                remoteChanges.Add((id, type, user));
            }

            // Listen für Meldungen
            var notCreatedIds = new List<long>();    // Remote eingefügte Elemente, die lokal nicht erstellt werden konnten
            var conflictIds = new List<long>();      // Änderungen, die mit lokalem Stand konfligieren
            var appliedCount = 0;

            // Starte Revit-Transaktion zur Übernahme der Änderungen
            using (Transaction tx = new Transaction(doc, "Pull Changes"))
            {
                tx.Start();
                try
                {
                    // 1. Für jeden entfernten Delta-Patch die lokale Daten aktualisieren
                    var affectedIds = new HashSet<long>();  // alle betroffenen IDs (für evtl. Local-Queue-Bereinigung)
                    foreach (var (elemId, changeType, user) in remoteChanges)
                    {
                        ElementId revitId = new ElementId(elemId);
                        Element localElem = doc.GetElement(revitId);
                        affectedIds.Add(elemId);
                        if (changeType == "Delete")
                        {
                            // Entfernte Löschung durchführen (Element im lokalen Modell löschen, falls vorhanden)
                            if (localElem != null)
                            {
                                doc.Delete(revitId);
                                appliedCount++;
                            }
                            // Falls Element lokal bereits nicht mehr vorhanden, kein Konflikt – es war schon gelöscht
                        }
                        else if (changeType == "Insert")
                        {
                            // Neues Element wurde remote eingefügt
                            if (localElem == null)
                            {
                                // Automatische Neuerstellung im lokalen Modell nicht implementiert
                                notCreatedIds.Add(elemId);
                                Debug.WriteLine($"[Pull] Element neu im Graph (ID {elemId}), muss manuell im Revit-Modell hinzugefügt werden.");
                                // Hinweis: Hier könnte man versuchen, bestimmte Kategorien (Räume usw.) automatisch anzulegen.
                            }
                            else
                            {
                                // Element mit gleicher ID existiert bereits lokal (kein Handlungsbedarf)
                            }
                        }
                        else if (changeType == "Modify")
                        {
                            // Eigenschaftenänderung remote
                            if (localElem != null)
                            {
                                bool applied = false;
                                // Je nach Element-Typ relevante Properties aktualisieren
                                if (localElem is Room room)
                                {
                                    // Neuen Namen aus Neo4j lesen
                                    string newName = "";
                                    try
                                    {
                                        var recs = await connector.RunReadQueryAsync(
                                           "MATCH (r:Room {ElementId: $id}) RETURN r.Name AS name",
                                            new { id = elemId }).ConfigureAwait(false);
                                    }
                                    catch { /* ignore errors reading name */ }
                                    if (!string.IsNullOrEmpty(newName))
                                    {
                                        try
                                        {
                                            room.Name = newName;
                                            applied = true;
                                            Debug.WriteLine($"[Pull] Raum {elemId} Name -> {newName}");
                                        }
                                        catch (Exception ex)
                                        {
                                            Debug.WriteLine($"[Pull] Raum-Name für ID {elemId} konnte nicht gesetzt werden: {ex.Message}");
                                        }
                                    }
                                }
                                else if (localElem is Level level)
                                {
                                    string newName = "";
                                    try
                                    {
                                        var recs = await connector.RunReadQueryAsync("MATCH (l:Level {ElementId: $id}) RETURN l.Name AS name", new { id = elemId }).ConfigureAwait(false);
                                    }
                                    catch { }
                                    if (!string.IsNullOrEmpty(newName) && newName != level.Name)
                                    {
                                        try
                                        {
                                            level.Name = newName;
                                            applied = true;
                                            Debug.WriteLine($"[Pull] Level {elemId} Name -> {newName}");
                                        }
                                        catch (Exception ex)
                                        {
                                            Debug.WriteLine($"[Pull] Level-Name für ID {elemId} nicht geändert: {ex.Message}");
                                        }
                                    }
                                }
                                else if (localElem is Wall wall)
                                {
                                    // Wand: ggf. Typname geändert
                                    string newName = "";
                                    try
                                    {
                                        var recs = await connector.RunReadQueryAsync("MATCH (w:Wall {ElementId: $id}) RETURN coalesce(w.Name, w.Type) AS newName", new { id = elemId }).ConfigureAwait(false);
                                    }
                                    catch { }
                                    if (!string.IsNullOrEmpty(newName))
                                    {
                                        WallType wType = wall.WallType;
                                        if (wType.Name != newName)
                                        {
                                            try
                                            {
                                                wType.Name = newName;
                                                applied = true;
                                                Debug.WriteLine($"[Pull] WallType von Wand {elemId} umbenannt -> {newName}");
                                            }
                                            catch (Exception ex)
                                            {
                                                Debug.WriteLine($"[Pull] Wand-Typname für ID {elemId} konnte nicht gesetzt werden: {ex.Message}");
                                            }
                                        }
                                    }
                                }
                                else if (localElem is FamilyInstance fi && fi.Category.Id.Value == (int)BuiltInCategory.OST_Doors)
                                {
                                    // Tür: Türnummer (Mark) aktualisieren
                                    string newMark = "";
                                    try
                                    {
                                        var recs = await connector.RunReadQueryAsync("MATCH (d:Door {ElementId: $id}) RETURN d.Name AS mark", new { id = elemId }).ConfigureAwait(false);
                                    }
                                    catch { }
                                    if (!string.IsNullOrEmpty(newMark))
                                    {
                                        var markParam = fi.get_Parameter(BuiltInParameter.DOOR_NUMBER);
                                        if (markParam != null && !markParam.IsReadOnly && markParam.AsString() != newMark)
                                        {
                                            try
                                            {
                                                markParam.Set(newMark);
                                                applied = true;
                                                Debug.WriteLine($"[Pull] Tür {elemId} Nummer -> {newMark}");
                                            }
                                            catch (Exception ex)
                                            {
                                                Debug.WriteLine($"[Pull] Türnummer für ID {elemId} nicht geändert: {ex.Message}");
                                            }
                                        }
                                    }
                                }
                                // (Weitere Elementtypen können analog behandelt werden)

                                if (applied) appliedCount++;
                            }
                            else
                            {
                                // Element existiert lokal nicht (z.B. lokal gelöscht, aber remote geändert) -> Konflikt
                                conflictIds.Add(elemId);
                                Debug.WriteLine($"[Pull] Konflikt: Element {elemId} wurde extern geändert, ist lokal aber nicht vorhanden.");
                            }
                        }
                    }

                    // 2. Lokale nicht-übertragene Änderungen bereinigen, falls durch Pull obsolet (z.B. entferntes Element)
                    foreach (long id in affectedIds)
                    {
                        // Entferne alle in Queue verbliebenen lokalen Befehle, die dieses Element betreffen (verhindert Inkonsistenzen beim nächsten Push)
                        var tempList = new List<string>();
                        while (cmdMgr.cypherCommands.TryDequeue(out string queuedCmd))
                        {
                            if (queuedCmd.Contains($"ElementId: {id}") || queuedCmd.Contains($"ElementId = {id}"))
                            {
                                // verwerfen
                                Debug.WriteLine($"[Pull] Entferne lokalen Patch für Element {id} aus Queue (überschrieben durch Pull).");
                            }
                            else
                            {
                                tempList.Add(queuedCmd);
                            }
                        }
                        // Restliche unveränderte wieder einreihen
                        foreach (var cmd in tempList)
                            cmdMgr.cypherCommands.Enqueue(cmd);
                    }

                    // 3. Transaktion abschließen
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    TaskDialog.Show("Pull-Fehler", $"Fehler beim Anwenden der Änderungen:\n{ex.Message}");
                    return Result.Failed;
                }
            }

            // LastSyncTime auf Zeitpunkt des letzten übernommenen Logs setzen
            string latestTs = changeRecords.Last()["ts"].As<string>();
            // letzter Datensatz
            try
            {
                cmdMgr.LastSyncTime = DateTime.Parse(latestTs);
            }
            catch { cmdMgr.LastSyncTime = DateTime.UtcNow; }
            // Synchronisationszeitpunkt persistieren
            cmdMgr.PersistSyncTime();
            try
            {
                await connector.UpdateSessionLastSyncAsync(sessionId, cmdMgr.LastSyncTime).ConfigureAwait(false);
                await connector.CleanupObsoleteChangeLogsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pull] Fehler beim Session-Update: {ex.Message}");
            }

            // Ergebnis auswerten und Benutzer informieren
            if (notCreatedIds.Count == 0 && conflictIds.Count == 0)
            {
                // Vollständig erfolgreich
                TaskDialog.Show("Pull", $"Es wurden {appliedCount} Änderungen von anderen Nutzern erfolgreich ins Modell übernommen.");
                SpaceTrackerClass.SetStatusIndicator(SpaceTrackerClass.StatusColor.Green);
            }
            else
            {
                // Es gab Probleme/Inkonsistenzen
                string info = "Einige Änderungen konnten nicht vollständig synchronisiert werden:\n";
                if (notCreatedIds.Count > 0)
                {
                    info += $" - Neu erstellte Elemente nicht im lokalen Modell vorhanden (manuelle Erstellung nötig): IDs {string.Join(", ", notCreatedIds)}\n";
                }
                if (conflictIds.Count > 0)
                {
                    info += $" - Konflikt: Remote-Änderungen an gelöschten Elementen (IDs {string.Join(", ", conflictIds)}) wurden nicht übernommen.\n";
                }
                info += "\nBitte prüfen Sie diese Inkonsistenzen manuell.";
                TaskDialog.Show("Pull - Inkonsistenzen", info);
                SpaceTrackerClass.SetStatusIndicator(SpaceTrackerClass.StatusColor.Red);
            }
            return Result.Succeeded;
        }
    }
}