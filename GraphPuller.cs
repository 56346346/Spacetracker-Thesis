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