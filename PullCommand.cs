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



namespace SpaceTracker
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PullCommand : IExternalCommand
    {
        private Document _doc;
        private Neo4jConnector _connector;
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return ExecuteAsync(commandData, message, elements).GetAwaiter().GetResult();
        }
        
          private async Task ApplyCachedInserts(Document doc)
        {
            int created = 0;
            foreach (var change in ChangeCacheHelper.ReadChanges())
            {
                if (string.IsNullOrWhiteSpace(change.cypher))
                    continue;
                if (!change.cypher.Contains("MERGE", StringComparison.OrdinalIgnoreCase) ||
                    change.cypher.Contains("DELETE", StringComparison.OrdinalIgnoreCase))
                    continue;

                long id = change.elementId;
                if (doc.GetElement(new ElementId(id)) != null)
                    continue;

                string label = null;
                var match = Regex.Match(change.cypher, @"MERGE\s*\(\w+:([A-Za-z0-9_]+)");
                if (match.Success)
                    label = match.Groups[1].Value;

                var createdElem = await CreateElementFromNeo4j(id, label).ConfigureAwait(false);
                if (createdElem != null)
                    created++;
            }

            if (created > 0)
            {
                ChangeCacheHelper.ClearCache();
            }
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

            _doc = doc;
            _connector = connector;

            await ApplyCachedInserts(doc).ConfigureAwait(false);

            // Abfrage: ChangeLog-Einträge seit dem letzten Sync dieses Nutzers (ausgenommen eigene)
            string lastSyncStr = cmdMgr.LastSyncTime.ToString("o");
            string sessionId = cmdMgr.SessionId;
            var queryParams = new { lastSync = lastSyncStr, session = sessionId };
            string cypher = "MATCH (c:ChangeLog) " +
                            "WHERE c.timestamp > $lastSync AND c.sessionId <> $session " +
                            "RETURN c.elementId AS id, c.type AS type, c.user AS user, c.timestamp AS ts, c.cachePath AS cache " +
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

                cmdMgr.LastSyncTime = DateTime.Now;
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
            var remoteChanges = new List<(long id, string type, string user, string cache)>();
            foreach (var rec in changeRecords)
            {
                long id = rec["id"].As<long>();
                string type = rec["type"].As<string>();
                string user = rec["user"].As<string>();
                string cachePath = rec["cache"]?.As<string>();
                remoteChanges.Add((id, type, user, cachePath));
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
                    foreach (var (elemId, changeType, user, cache) in remoteChanges)
                    {
                        ElementId revitId = new ElementId(elemId);
                        Element localElem = doc.GetElement(revitId);
                        affectedIds.Add(elemId);
                        if (!string.IsNullOrEmpty(cache) && File.Exists(cache))
                        {
                            try
                            {
                                var _ = File.ReadAllText(cache);
                            }
                            catch { }
                        }
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
                                Element created = await CreateElementFromNeo4j(elemId, null);
                                if (created != null)
                                {
                                    appliedCount++;
                                }
                                else
                                {
                                    notCreatedIds.Add(elemId);
                                    Debug.WriteLine($"[Pull] Element neu im Graph (ID {elemId}), muss manuell im Revit-Modell hinzugefügt werden.");
                                }
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
                                    var recs = await connector.RunReadQueryAsync(
                                          "MATCH (r:Room {ElementId: $id}) RETURN r.Name AS name",
                                           new { id = elemId }).ConfigureAwait(false);
                                    string newName = recs.FirstOrDefault()?["name"]?.As<string>() ?? "";

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
                                    var recs = await connector.RunReadQueryAsync("MATCH (l:Level {ElementId: $id}) RETURN l.Name AS name", new { id = elemId }).ConfigureAwait(false);
                                    string newName = recs.FirstOrDefault()?["name"]?.As<string>() ?? "";
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
                                    var recs = await connector.RunReadQueryAsync("MATCH (w:Wall {ElementId: $id}) RETURN coalesce(w.Name, w.Type) AS newName", new { id = elemId }).ConfigureAwait(false);
                                    string newName = recs.FirstOrDefault()?["newName"]?.As<string>() ?? "";
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
                                    var recs = await connector.RunReadQueryAsync("MATCH (d:Door {ElementId: $id}) RETURN d.Name AS mark", new { id = elemId }).ConfigureAwait(false);
                                    string newMark = recs.FirstOrDefault()?[
                                        "mark"]?.As<string>() ?? "";
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
                        var pattern = $"ElementId\\s*[:=]\\s*{id}\b";
                        var regex = new System.Text.RegularExpressions.Regex(pattern);
                        while (cmdMgr.cypherCommands.TryDequeue(out string queuedCmd))
                        {
                            if (regex.IsMatch(queuedCmd))
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
            catch { cmdMgr.LastSyncTime = DateTime.Now; }
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



 internal async Task<Element> CreateElementFromNeo4j(long elementId, string elementType)        {
            if (_doc == null || _connector == null)
                return null;

            try
            {
                // Elementtyp ermitteln, falls nicht übergeben
                if (string.IsNullOrEmpty(elementType))
                {
                    var tRec = (await _connector.RunReadQueryAsync(
                        "MATCH (e {ElementId: $id}) RETURN head(labels(e)) AS type",
                        new { id = elementId }).ConfigureAwait(false)).FirstOrDefault();
                    elementType = tRec?["type"]?.As<string>();
                }

                switch (elementType)
                {
                    case "Level":
                        var lvlData = await _connector.RunReadQueryAsync(
                            "MATCH (l:Level {ElementId: $id}) RETURN l.Name AS name, coalesce(l.Elevation, 0) AS elev",
                            new { id = elementId }).ConfigureAwait(false);
                        var lvlRec = lvlData.FirstOrDefault();
                        if (lvlRec != null)
                        {
                            string lvlName = lvlRec["name"].As<string>();
                            double elev = 0;
                            try { elev = lvlRec["elev"].As<double>(); } catch { }
                            Level newLevel = Level.Create(_doc, elev);
                            newLevel.Name = lvlName;
                            return newLevel;
                        }
                        break;
                    case "Room":
                        var roomData = await _connector.RunReadQueryAsync(
                            "MATCH (l:Level)-[:CONTAINS]->(r:Room {ElementId: $id}) RETURN r.Name AS name, l.ElementId AS levelId",
                            new { id = elementId }).ConfigureAwait(false);
                        var roomRec = roomData.FirstOrDefault();
                        if (roomRec != null)
                        {
                            string rName = roomRec["name"].As<string>();
                            long levelId = roomRec["levelId"].As<long>();
                            Level lvl = _doc.GetElement(new ElementId(levelId)) as Level;
                            if (lvl != null)
                            {
                                UV loc = new UV(0, 0);
                                Room newRoom = _doc.Create.NewRoom(lvl, loc);
                                newRoom.Name = rName;
                                return newRoom;
                            }
                        }
                        break;
                    case "Wall":
                        var wallData = await _connector.RunReadQueryAsync(
                            "MATCH (w:Wall {ElementId:$id}) RETURN w.Type AS type, w.Level AS level",
                            new { id = elementId }).ConfigureAwait(false);
                        var wRec = wallData.FirstOrDefault();
                        if (wRec != null)
                        {
                            var lvl = _doc.GetElement(new ElementId((long)wRec["level"].As<long>())) as Level;
                            if (lvl != null)
                            {
                                WallType wType = new FilteredElementCollector(_doc)
                                    .OfClass(typeof(WallType))
                                    .Cast<WallType>()
                                    .FirstOrDefault(t => t.Name == wRec["type"].As<string>());
                                if (wType != null)
                                {
                                    return Wall.Create(_doc, Line.CreateBound(XYZ.Zero, XYZ.BasisX), wType.Id, lvl.Id, 3, 0, false, false);
                                }
                            }
                        }
                        break;
                    case "Door":
                        var doorData = await _connector.RunReadQueryAsync(
                            "MATCH (d:Door {ElementId:$id}) RETURN d.Type AS type, d.Level AS level",
                            new { id = elementId }).ConfigureAwait(false);
                        var dRec = doorData.FirstOrDefault();
                        if (dRec != null)
                        {
                            var lvl = _doc.GetElement(new ElementId((long)dRec["level"].As<long>())) as Level;
                            if (lvl != null)
                            {
                                FamilySymbol doorType = new FilteredElementCollector(_doc)
                                    .OfCategory(BuiltInCategory.OST_Doors)
                                    .OfClass(typeof(FamilySymbol))
                                    .Cast<FamilySymbol>()
                                    .FirstOrDefault(t => t.Name == dRec["type"].As<string>());
                                if (doorType != null && !doorType.IsActive) doorType.Activate();
                                if (doorType != null)
                                {
                                    XYZ loc = XYZ.Zero;
                                    return _doc.Create.NewFamilyInstance(loc, doorType, lvl, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                                }
                            }
                        }
                        break;
                    case "Stair":
                        var stairData = await _connector.RunReadQueryAsync(
                            "MATCH (b:Level)-[:CONNECTS_TO]->(s:Stair {ElementId:$id})-[:CONNECTS_TO]->(t:Level) RETURN b.ElementId AS base, t.ElementId AS top",
                            new { id = elementId }).ConfigureAwait(false);
                        var sRec = stairData.FirstOrDefault();
                        if (sRec != null)
                        {
                            var baseLvl = _doc.GetElement(new ElementId((long)sRec["base"].As<long>())) as Level;
                            var topLvl = _doc.GetElement(new ElementId((long)sRec["top"].As<long>())) as Level;
                            if (baseLvl != null && topLvl != null)
                            {
                                try
                                {
                                    // Select the first available stairs type for creation
                                    var stairType = new FilteredElementCollector(_doc)
                                        .OfClass(typeof(Autodesk.Revit.DB.Architecture.StairsType))
                                        .Cast<Autodesk.Revit.DB.Architecture.StairsType>()
                                        .FirstOrDefault();

                                    if (stairType != null)
                                    {

                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[Pull] Fehler beim Erstellen der Treppe {elementId}: {ex.Message}");
                                }
                            }
                        }
                        break;

                    default:
                        Debug.WriteLine($"[Pull] CreateElementFromNeo4j: unbekannter Typ {elementType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pull] Fehler beim Erstellen des Elements {elementId}: {ex.Message}");
            }

            return null;
        }
    }
}