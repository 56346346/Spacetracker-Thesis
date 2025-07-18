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

    private static void LogMethodCall(string methodName, Dictionary<string, object?> parameters)
    {
        MethodLogger.Log(nameof(GraphPuller), methodName, parameters);
    }
    private readonly Neo4jConnector _connector;
    public DateTime LastPulledAt { get; private set; } = DateTime.MinValue;
    // Erzeugt den Puller mit optionalem Connector

    public GraphPuller(Neo4jConnector? connector = null)
    {
        _connector = connector ?? CommandManager.Instance.Neo4jConnector;
    }
    // Synchronises all changes since the last pull by querying modified nodes
    // directly instead of relying on change log relationships.
    public async Task PullRemoteChanges(Document doc, string currentUserId)
    {
        LogMethodCall(nameof(PullRemoteChanges), new()
        {
            ["doc"] = doc?.Title,
            ["currentUserId"] = currentUserId
        });
        var cmdMgr = CommandManager.Instance;

        var walls = await _connector.GetUpdatedWallsAsync(cmdMgr.LastSyncTime).ConfigureAwait(false);
        var doors = await _connector.GetUpdatedDoorsAsync(cmdMgr.LastSyncTime).ConfigureAwait(false);
        var pipes = await _connector.GetUpdatedPipesAsync(cmdMgr.LastSyncTime).ConfigureAwait(false);
        var provisionalSpaces = await _connector.GetUpdatedProvisionalSpacesAsync(cmdMgr.LastSyncTime).ConfigureAwait(false);

        var validDoorTypes = new HashSet<ElementId>(new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Doors)
            .OfClass(typeof(FamilySymbol))
            .Select(fs => fs.Id));
        doors = doors
            .Where(d => validDoorTypes.Contains(new ElementId((int)d.TypeId)))
            .ToList();

        var validPipeTypes = new HashSet<ElementId>(new FilteredElementCollector(doc)
            .OfClass(typeof(PipeType))
            .Select(pt => pt.Id));
        pipes = pipes
            .Where(p => validPipeTypes.Contains(new ElementId((int)p.TypeId)))
            .ToList();

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
            Debug.WriteLine($"Build wall {w.ElementId}");
            RevitElementBuilder.BuildFromNode(doc, w.ToDictionary());
        }
        doc.Regenerate();
        foreach (var d in doors)
        {
            Debug.WriteLine($"Build door {d.ElementId}");
            RevitElementBuilder.BuildFromNode(doc, d.ToDictionary());
        }
        foreach (var p in pipes)
        {
            Debug.WriteLine($"Build pipe {p.ElementId}");
            RevitElementBuilder.BuildFromNode(doc, p.ToDictionary());
        }
        foreach (var ps in provisionalSpaces)
        {
            Debug.WriteLine($"Build provisional space {ps.Guid}");
            RevitElementBuilder.BuildFromNode(doc, ps.ToDictionary());
        }
        tx.Commit();

        cmdMgr.LastSyncTime = System.DateTime.UtcNow;
        cmdMgr.LastPulledAt = cmdMgr.LastSyncTime;
        LastPulledAt = cmdMgr.LastPulledAt;
        cmdMgr.PersistSyncTime();
        await _connector.UpdateSessionLastSyncAsync(cmdMgr.SessionId, cmdMgr.LastSyncTime).ConfigureAwait(false);

        var key = doc.PathName ?? doc.Title;
        if (SessionManager.OpenSessions.TryGetValue(key, out var session))
            session.LastSyncTime = cmdMgr.LastSyncTime;
    }
}