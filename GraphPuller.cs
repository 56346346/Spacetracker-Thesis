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
            RevitElementBuilder.BuildFromNode(doc, w.ToDictionary());
        foreach (var d in doors)
            RevitElementBuilder.BuildFromNode(doc, d.ToDictionary());
        foreach (var p in pipes)
            RevitElementBuilder.BuildFromNode(doc, p.ToDictionary());
        foreach (var ps in provisionalSpaces)
            RevitElementBuilder.BuildFromNode(doc, ps.ToDictionary());

        tx.Commit();

        cmdMgr.LastSyncTime = System.DateTime.UtcNow;
        cmdMgr.PersistSyncTime();
        await _connector.UpdateSessionLastSyncAsync(cmdMgr.SessionId, cmdMgr.LastSyncTime).ConfigureAwait(false);
    }
    // Kleine Hilfsfunktion für Cypher-Sicherheit.

    private static string EscapeString(string input)
    {
        return string.IsNullOrEmpty(input) ? string.Empty : input.Replace("\\", string.Empty).Replace("'", "''").Replace("\"", "'");
    }
}