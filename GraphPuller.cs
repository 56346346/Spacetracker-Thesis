using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using System.Linq;
using Autodesk.Revit.UI;
using Neo4j.Driver;
using System.IO;
using static System.Environment;




namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public class GraphPuller
{
    private bool _pullInProgress = false;

    private static readonly string _logDir =
       Path.Combine(GetFolderPath(Environment.SpecialFolder.ApplicationData),
       "SpaceTracker", "log");
    private static readonly string _logPath =
       Path.Combine(_logDir, nameof(GraphPuller) + ".log");
    private static readonly object _logLock = new object();

    static GraphPuller()
    {
        if (!Directory.Exists(_logDir))
            Directory.CreateDirectory(_logDir);
        MethodLogger.InitializeLog(nameof(GraphPuller));
    }

    private static void LogMethodCall(string methodName, Dictionary<string, object> parameters)
    {
        MethodLogger.Log(nameof(GraphPuller), methodName, parameters);
    }
    private readonly Neo4jConnector _connector;
    public DateTime LastPulledAt { get; private set; } = DateTime.MinValue;
    // Erzeugt den Puller mit optionalem Connector

    public GraphPuller(Neo4jConnector connector = null)
    {
        _connector = connector ?? CommandManager.Instance.Neo4jConnector;
    }

    // Applies pending wall changes from ChangeLog entries for this session
    public void ApplyPendingWallChanges(Document doc, string sessionId)
    {
        // 1) Changes laden
        var changes = _connector.RunReadQueryAsync(
            @"MATCH (c:ChangeLog {ack:false})-[:CHANGED]->(w:Wall)
              WHERE c.targetSessionId = $sessionId
              RETURN id(c) AS changeId, c.op AS op, c.elementId AS elementId, w AS wall
              ORDER BY c.ts ASC",
            new { sessionId }).GetAwaiter().GetResult();

        Logger.LogToFile($"Found {changes.Count} pending wall changes for session {sessionId}", "sync.log");

        // 2) Für jedes Change anwenden
        foreach (var rec in changes)
        {
            try
            {
                var changeId = rec["changeId"].As<long>();
                var op = rec["op"].As<string>();
                var elementId = rec["elementId"].As<int>();

                switch (op)
                {
                    case "Create":
                    case "Modify":
                        var wall = rec["wall"];
                        UpsertWallFromGraph(doc, wall.As<INode>());
                        break;

                    case "Delete":
                        DeleteWallByRemoteElementId(doc, elementId);
                        break;
                }

                // 3) Acknowledge (pro ChangeLog-Node)
                _connector.RunWriteQueryAsync(
                    "MATCH (c) WHERE id(c)=$id SET c.ack=true, c.ackBy=$sid, c.ackTs=datetime()",
                    new { id = changeId, sid = sessionId }).GetAwaiter().GetResult();

                Logger.LogToFile($"Applied and acknowledged change {changeId} ({op}) for element {elementId}", "sync.log");
            }
            catch (Exception ex)
            {
                Logger.LogCrash($"Error applying wall change", ex);
                // Continue with next change
            }
        }
    }

    private void UpsertWallFromGraph(Document doc, INode wallNode)
    {
        var w = wallNode.Properties;

        // --- 1) Werte aus Neo4j ---
        int remoteElementId = (int)(long)w["ElementId"]; // Neo4j gibt int oft als long zurück
        bool flipped = w.ContainsKey("flipped") && (bool)w["flipped"];
        bool structural = w.ContainsKey("structural") && (bool)w["structural"];

        double x1 = (double)w["x1"], y1 = (double)w["y1"], z1 = (double)w["z1"];
        double x2 = (double)w["x2"], y2 = (double)w["y2"], z2 = (double)w["z2"];

        int locCode = w.ContainsKey("location_line") ? (int)(long)w["location_line"] : 1; // Default: CoreCenterline
        double baseOffset_m = w.ContainsKey("base_offset_m") ? (double)w["base_offset_m"] : 0.0;

        string baseLevelUid = (string)w["baseLevelUid"];
        bool hasTop = w.ContainsKey("topLevelUid") && !string.IsNullOrEmpty((string)w["topLevelUid"]);
        string topLevelUid = hasTop ? (string)w["topLevelUid"] : null;
        double topOffset_m = hasTop && w.ContainsKey("top_offset_m") ? (double)w["top_offset_m"] : 0.0;

        bool hasUncHeight = w.ContainsKey("unconnected_height_m");
        double uncHeight_m = hasUncHeight ? (double)w["unconnected_height_m"] : 3.0; // default 3m

        string typeName = (string)w["typeName"];
        string familyName = w.ContainsKey("familyName") ? (string)w["familyName"] : "Basiswand";
        double thickness_m = w.ContainsKey("thickness_m") ? (double)w["thickness_m"] : 0.2;

        // --- 2) Revit-Objekte auflösen ---
        var baseLevel = doc.GetElement(baseLevelUid) as Level
                     ?? throw new InvalidOperationException($"Base-Level nicht gefunden: {baseLevelUid}");

        Level topLevel = null;
        if (hasTop)
        {
            topLevel = doc.GetElement(topLevelUid) as Level
                    ?? throw new InvalidOperationException($"Top-Level nicht gefunden: {topLevelUid}");
        }

        var wallType = FindWallType(doc, typeName, familyName, thickness_m)
                    ?? FindFallbackWallType(doc, familyName, thickness_m)
                    ?? new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault()
                    ?? throw new InvalidOperationException("Kein WallType im Dokument gefunden.");

        // --- 3) Geometrie aufbauen (Meter → Fuß) ---
        XYZ p1 = ToFeetXYZ(x1, y1, z1);
        XYZ p2 = ToFeetXYZ(x2, y2, z2);
        if (p1.IsAlmostEqualTo(p2)) throw new InvalidOperationException("Degenerierte Wand (P1==P2).");

        var line = Line.CreateBound(p1, p2);

        double baseOffset_ft = ToFeet(baseOffset_m);
        double height_ft = hasTop
            ? Math.Max(0.0, (topLevel.Elevation - baseLevel.Elevation) + ToFeet(topOffset_m))
            : ToFeet(uncHeight_m);

        // --- 4) Lokal bereits vorhanden? (Id-Brücke via Comments) ---
        var local = FindLocalWallByRemoteId(doc, remoteElementId);

        Wall wall;
        if (local == null)
        {
            wall = Wall.Create(doc, line, wallType.Id, baseLevel.Id, height_ft, baseOffset_ft, flipped, structural);
            MarkWallWithRemoteId(wall, remoteElementId);
            Logger.LogToFile($"Created wall for remote ElementId {remoteElementId}, local Id {wall.Id.Value}", "sync.log");
        }
        else
        {
            wall = local;
            // Kurve/Typ/Parameter aktualisieren
            var lc = (LocationCurve)wall.Location;
            lc.Curve = line;
            if (wall.WallType.Id != wallType.Id) wall.WallType = wallType;
            SetBaseAndHeight(doc, wall, baseLevel, topLevel, height_ft, baseOffset_ft);
            if (wall.Flipped != flipped) wall.Flip();
            Logger.LogToFile($"Updated existing wall for remote ElementId {remoteElementId}, local Id {wall.Id.Value}", "sync.log");
        }

        // --- 5) Location Line & Room Bounding ---
        SetWallLocationLine(wall, locCode);
        SetRoomBoundingIfNeeded(wall, w.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
    }

    private Wall FindLocalWallByRemoteId(Document doc, int remoteId)
    {
        var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>();
        foreach (var w in walls)
        {
            var p = w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            var s = p?.AsString();
            if (!string.IsNullOrEmpty(s) && s.Contains($"SpaceTracker:ElementId={remoteId}"))
                return w;
        }
        return null;
    }

    private void MarkWallWithRemoteId(Wall wall, int remoteId)
    {
        var p = wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (p != null && !p.IsReadOnly)
        {
            var current = p.AsString();
            var tag = $"SpaceTracker:ElementId={remoteId}";
            p.Set(string.IsNullOrEmpty(current) ? tag : $"{current}; {tag}");
        }
    }

    private void SetBaseAndHeight(Document doc, Wall wall, Level baseLevel, Level topLevel, double height_ft, double baseOffset_ft)
    {
        // Basis-Level
        var pBaseLvl = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
        if (pBaseLvl != null && !pBaseLvl.IsReadOnly) pBaseLvl.Set(baseLevel.Id);

        // Base-Offset
        var pBaseOff = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
        if (pBaseOff != null && !pBaseOff.IsReadOnly) pBaseOff.Set(baseOffset_ft);

        if (topLevel != null)
        {
            // Top-Constraint = Level
            var pTopType = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
            if (pTopType != null && !pTopType.IsReadOnly) pTopType.Set(topLevel.Id);
        }
        else
        {
            // Unconnected Height
            var pTopType = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
            if (pTopType != null && !pTopType.IsReadOnly) pTopType.Set(ElementId.InvalidElementId);
            
            var pHeight = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (pHeight != null && !pHeight.IsReadOnly) pHeight.Set(height_ft);
        }
    }

    private void SetWallLocationLine(Wall wall, int code)
    {
        var map = new Dictionary<int, WallLocationLine>
        {
            [0] = WallLocationLine.WallCenterline,
            [1] = WallLocationLine.CoreCenterline,
            [2] = WallLocationLine.FinishFaceExterior,
            [3] = WallLocationLine.FinishFaceInterior,
            [4] = WallLocationLine.CoreExterior,
            [5] = WallLocationLine.CoreInterior
        };
        var val = map.ContainsKey(code) ? map[code] : WallLocationLine.CoreCenterline;

        var p = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
        if (p != null && !p.IsReadOnly) p.Set((int)val);
    }

    private void SetRoomBoundingIfNeeded(Wall wall, IDictionary<string, object> wprops)
    {
        if (wprops.ContainsKey("roomBounding"))
        {
            bool rb = (bool)wprops["roomBounding"];
            var p = wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
            if (p != null && !p.IsReadOnly) p.Set(rb ? 1 : 0);
        }
    }

    private WallType FindWallType(Document doc, string typeName, string familyName, double thickness_m)
    {
        var types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>();
        foreach (var t in types)
        {
            if (!string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(familyName) && !t.FamilyName.Contains(familyName, StringComparison.OrdinalIgnoreCase)) continue;
            return t;
        }
        return null;
    }

    private WallType FindFallbackWallType(Document doc, string familyName, double thickness_m)
    {
        var types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
        if (!string.IsNullOrEmpty(familyName))
            types = types.Where(t => t.FamilyName.Contains(familyName, StringComparison.OrdinalIgnoreCase)).ToList();

        // Nächstliegende Dicke
        var target = ToFeet(thickness_m);
        WallType best = null;
        double bestDelta = double.MaxValue;
        foreach (var t in types)
        {
            var cs = t.GetCompoundStructure();
            if (cs == null) continue;
            var width = cs.GetWidth(); // Gesamtstärke in Fuß
            var delta = Math.Abs(width - target);
            if (delta < bestDelta) { bestDelta = delta; best = t; }
        }
        return best;
    }

    private void DeleteWallByRemoteElementId(Document doc, int remoteId)
    {
        var wall = FindLocalWallByRemoteId(doc, remoteId);
        if (wall != null) 
        {
            doc.Delete(wall.Id);
            Logger.LogToFile($"Deleted wall for remote ElementId {remoteId}", "sync.log");
        }
    }

    // Konvertierer (using existing UnitConversion patterns)
    private static XYZ ToFeetXYZ(double xm, double ym, double zm) =>
        new XYZ(ToFeet(xm), ToFeet(ym), ToFeet(zm));

    private static double ToFeet(double meters) =>
        UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
    // Synchronises all changes since the last pull by querying modified nodes
    // directly instead of relying on change log relationships.
    public async Task PullRemoteChanges(Document doc, string currentUserId)
    {
        if (_pullInProgress)
            return; // Verhindert erneutes Reentry beim Hängen

        _pullInProgress = true;
        try
        {
            LogMethodCall(nameof(PullRemoteChanges), new()
            {
                ["doc"] = doc?.Title,
                ["currentUserId"] = currentUserId
            });
            var cmdMgr = CommandManager.Instance;
            var lastSync = cmdMgr.LastSyncTime;
            Logger.LogToFile("Lade aktualisierte Wände seit " + lastSync, "sync.log");

            var wallsTask = _connector.GetUpdatedWallsAsync(lastSync);
            Logger.LogToFile("Lade aktualisierte Türen seit " + lastSync, "sync.log");

            var doorsTask = _connector.GetUpdatedDoorsAsync(lastSync);
            Logger.LogToFile("Lade aktualisierte Rohre seit " + lastSync, "sync.log");

            var pipesTask = _connector.GetUpdatedPipesAsync(lastSync);
            Logger.LogToFile("Lade aktualisierte ProvisionalSpaces seit " + lastSync, "sync.log");

            var provisionalSpacesTask = _connector.GetUpdatedProvisionalSpacesAsync(lastSync);

            await Task.WhenAll(wallsTask, doorsTask, pipesTask, provisionalSpacesTask);

            var walls = await wallsTask;
            var doors = await doorsTask;
            var pipes = await pipesTask;
            var provisionalSpaces = await provisionalSpacesTask;


            Logger.LogToFile(
                $"Gefundene Änderungen: {walls.Count} Wände, {doors.Count} Türen, {pipes.Count} Rohre, {provisionalSpaces.Count} ProvisionalSpaces",
                "sync.log");
            // Rely solely on the information provided by Neo4j.  Types are
            // validated while creating the elements and missing types will be
            // looked up by name where possible.

            provisionalSpaces = provisionalSpaces
                .Where(ps => ParameterUtils.IsProvisionalSpace(ps.ToDictionary()))
                .ToList();

            string pullInfo = $"Pulled {walls.Count} walls, {doors.Count} doors, {pipes.Count} pipes, {provisionalSpaces.Count} provisional spaces";
            Debug.WriteLine(pullInfo);
            Logger.LogToFile($"GraphPuller.PullRemoteChanges: {pullInfo}", "sync.log");

            if (doc.IsReadOnly || doc.IsModifiable)
            {
                Logger.LogToFile("PullRemoteChanges skipped: document not ready for transaction", "sync.log");
                return;
            }

            using var tx = new Transaction(doc, "Auto Sync");
            tx.Start();

            foreach (var w in walls)
            {
                try
                {
                    Debug.WriteLine($"Build wall {w.ElementId}");
                    RevitElementBuilder.BuildFromNode(doc, w.ToDictionary());
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("Fehler beim Bauen von Wall", ex);
                }
            }
            foreach (var d in doors)
            {
                try
                {
                    Debug.WriteLine($"Build door {d.ElementId}");
                    RevitElementBuilder.BuildFromNode(doc, d.ToDictionary());
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("Fehler beim Bauen von Door", ex);
                }
            }
            foreach (var p in pipes)
            {
                try
                {
                    Debug.WriteLine($"Build pipe {p.ElementId}");
                    RevitElementBuilder.BuildFromNode(doc, p.ToDictionary());
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("Fehler beim Bauen von Pipe", ex);
                }
            }
            foreach (var ps in provisionalSpaces)
            {
                try
                {
                    Debug.WriteLine($"Build provisional space {ps.Guid}");
                    RevitElementBuilder.BuildFromNode(doc, ps.ToDictionary());
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("Fehler beim Bauen von ProvisionalSpace", ex);
                }
            }
            doc.Regenerate();
            tx.Commit();

            cmdMgr.LastSyncTime = System.DateTime.UtcNow;
            cmdMgr.LastPulledAt = cmdMgr.LastSyncTime;
            LastPulledAt = cmdMgr.LastPulledAt;
            cmdMgr.PersistSyncTime();
            Logger.LogToFile("Pull erfolgreich bis " + cmdMgr.LastSyncTime, "sync.log");
            await _connector.UpdateSessionLastSyncAsync(cmdMgr.SessionId, cmdMgr.LastSyncTime);
            // Prevent endless pull loops by acknowledging remote changelogs
            await _connector.AcknowledgeAllAsync(cmdMgr.SessionId);
            var key = doc.PathName ?? doc.Title;
            if (SessionManager.OpenSessions.TryGetValue(key, out var session))
                session.LastSyncTime = cmdMgr.LastSyncTime;
        }
        finally
        {
            _pullInProgress = false;
        }
    }

}