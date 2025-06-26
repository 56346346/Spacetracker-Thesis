using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Neo4j.Driver;


namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public class GraphPuller : IExternalEventHandler
{
    private readonly Neo4jConnector _connector;
    private ExternalEvent _event;
    private Document _doc;
    private string _userId;


    // Erzeugt den Puller und registriert ein ExternalEvent

    public GraphPuller(Neo4jConnector connector)
    {
        _connector = connector;
        _event = ExternalEvent.Create(this);
    }
    // Fordert einen Pull an; wird von anderen Klassen aufgerufen.

    public void RequestPull(Document doc, string currentUserId)
    {
        _doc = doc;
        _userId = currentUserId;
        if (!_event.IsPending)
            _event.Raise();
    }
    // Name des Events für Debugzwecke.
    public string GetName() => "GraphPuller";
    // ExternalEvent-Callback, ruft PullRemoteChanges auf.

    public void Execute(UIApplication app)
    {
        if (_doc == null || string.IsNullOrEmpty(_userId))
            return;
        try
        {
            PullRemoteChanges(_doc, _userId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogCrash("GraphPuller", ex);
        }
        finally
        {
            _doc = null;
            _userId = null;
        }
    }
    // Synchronises all changes since the last pull by querying modified nodes
    // directly instead of relying on change log relationships.
    public async Task PullRemoteChanges(Document doc, string currentUserId)
    {
        var cmdMgr = CommandManager.Instance;

        var walls = await _connector.GetUpdatedWallsAsync(cmdMgr.LastSyncTime).ConfigureAwait(false);
        var doors = await _connector.GetUpdatedDoorsAsync(cmdMgr.LastSyncTime).ConfigureAwait(false);
        var pipes = await _connector.GetUpdatedPipesAsync(cmdMgr.LastSyncTime).ConfigureAwait(false);
        var provisionalSpaces = await _connector.GetUpdatedProvisionalSpacesAsync(cmdMgr.LastSyncTime).ConfigureAwait(false);

        using var tx = new Transaction(doc, "Auto Sync");
        tx.Start();

        foreach (var w in walls)
            RevitElementBuilder.BuildFromNode(doc, WallToDict(w));
        foreach (var d in doors)
            RevitElementBuilder.BuildFromNode(doc, DoorToDict(d));
        foreach (var p in pipes)
            RevitElementBuilder.BuildFromNode(doc, PipeToDict(p));
        foreach (var ps in provisionalSpaces)
            RevitElementBuilder.BuildFromNode(doc, ProvSpaceToDict(ps));

        tx.Commit();

        cmdMgr.LastSyncTime = System.DateTime.UtcNow;
        cmdMgr.PersistSyncTime();
        await _connector.UpdateSessionLastSyncAsync(cmdMgr.SessionId, cmdMgr.LastSyncTime).ConfigureAwait(false);
    }

    private static Dictionary<string, object> WallToDict(WallNode w) => new()
    {
        ["rvtClass"] = "Wall",
        ["uid"] = w.Uid,
        ["elementId"] = w.ElementId,
        ["typeId"] = w.TypeId,
        ["typeName"] = w.TypeName,
        ["familyName"] = w.FamilyName,
        ["levelId"] = w.LevelId,
        ["x1"] = w.X1,
        ["y1"] = w.Y1,
        ["z1"] = w.Z1,
        ["x2"] = w.X2,
        ["y2"] = w.Y2,
        ["z2"] = w.Z2,
        ["height_mm"] = w.HeightMm,
        ["thickness_mm"] = w.ThicknessMm,
        ["structural"] = w.Structural,
        ["flipped"] = w.Flipped,
        ["base_offset_mm"] = w.BaseOffsetMm,
        ["location_line"] = w.LocationLine
    };

    private static Dictionary<string, object> DoorToDict(DoorNode d) => new()
    {
        ["rvtClass"] = "Door",
        ["uid"] = d.Uid,
        ["elementId"] = d.ElementId,
        ["typeId"] = d.TypeId,
        ["familyName"] = d.FamilyName,
        ["symbolName"] = d.SymbolName,
        ["levelId"] = d.LevelId,
        ["hostId"] = d.HostId,
        ["hostUid"] = d.HostUid,
        ["x"] = d.X,
        ["y"] = d.Y,
        ["z"] = d.Z,
        ["rotation"] = d.Rotation,
        ["width"] = d.Width,
        ["height"] = d.Height,
        ["thickness"] = d.Thickness
    };

    private static Dictionary<string, object> PipeToDict(PipeNode p) => new()
    {
        ["rvtClass"] = "Pipe",
        ["uid"] = p.Uid,
        ["elementId"] = p.ElementId,
        ["typeId"] = p.TypeId,
        ["systemTypeId"] = p.SystemTypeId,
        ["levelId"] = p.LevelId,
        ["x1"] = p.X1,
        ["y1"] = p.Y1,
        ["z1"] = p.Z1,
        ["x2"] = p.X2,
        ["y2"] = p.Y2,
        ["z2"] = p.Z2,
        ["diameter"] = p.Diameter
    };

    private static Dictionary<string, object> ProvSpaceToDict(ProvisionalSpaceNode ps) => new()
    {
        ["rvtClass"] = "ProvisionalSpace",
        ["guid"] = ps.Guid,
        ["name"] = ps.Name,
        ["familyName"] = ps.FamilyName,
        ["symbolName"] = ps.SymbolName,
        ["width"] = ps.Width,
        ["height"] = ps.Height,
        ["thickness"] = ps.Thickness,
        ["level"] = ps.Level,
        ["levelId"] = ps.LevelId,
        ["x"] = ps.X,
        ["y"] = ps.Y,
        ["z"] = ps.Z,
        ["rotation"] = ps.Rotation,
        ["hostId"] = ps.HostId,
        ["revitId"] = ps.RevitId,
        ["ifcType"] = ps.IfcType,
        ["category"] = ps.Category,
        ["phaseCreated"] = ps.PhaseCreated,
        ["phaseDemolished"] = ps.PhaseDemolished,
        ["bbMinX"] = ps.BbMinX,
        ["bbMinY"] = ps.BbMinY,
        ["bbMinZ"] = ps.BbMinZ,
        ["bbMaxX"] = ps.BbMaxX,
        ["bbMaxY"] = ps.BbMaxY,
        ["bbMaxZ"] = ps.BbMaxZ
    };

    // Kleine Hilfsfunktion für Cypher-Sicherheit.

    private static string EscapeString(string input)
    {
        return string.IsNullOrEmpty(input) ? string.Empty : input.Replace("\\", string.Empty).Replace("'", "''").Replace("\"", "'");
    }
}