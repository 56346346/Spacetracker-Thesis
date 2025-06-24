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

    public GraphPuller(Neo4jConnector connector)
    {
        _connector = connector;
        _event = ExternalEvent.Create(this);
    }

    public void RequestPull(Document doc, string currentUserId)
    {
        _doc = doc;
        _userId = currentUserId;
        if (!_event.IsPending)
            _event.Raise();
    }

    public string GetName() => "GraphPuller";

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

    public async Task PullRemoteChanges(Document doc, string currentUserId)
    {
        const string cypher = @"MATCH (u:User)-[:CREATED]->(lc:LogChanges)-[:TARGET]->(e)
WHERE u.id <> $uid AND NOT EXISTS( (:User {id:$uid})-[:RECEIVED]->(lc) )
RETURN lc, e ORDER BY lc.timestamp";
        var records = await _connector.RunReadQueryAsync(cypher, new { uid = currentUserId }).ConfigureAwait(false);
        foreach (var rec in records)
        {
            var node = rec["e"].As<INode>();
            var log = rec["lc"].As<INode>();
            using (var tx = new Transaction(doc, "Pull Remote Change"))
            {
                tx.Start();
                RevitElementBuilder.BuildFromNodes(doc, node);
                 var dict = node.Properties.ToDictionary(k => k.Key, k => (object)k.Value);
                RevitElementBuilder.BuildFromNode(doc, dict);
                tx.Commit();
            }
            long logId = long.TryParse(log.ElementId, out var id) ? id : log.Id;
            string relCypher =
                $"MATCH (u:User {{id:'{EscapeString(currentUserId)}'}}), (lc:LogChanges) WHERE id(lc) = {logId} MERGE (u)-[:RECEIVED]->(lc)";
            await _connector.RunCypherQuery(relCypher).ConfigureAwait(false);
        }
    }

    private static string EscapeString(string input)
    {
        return string.IsNullOrEmpty(input) ? string.Empty : input.Replace("\\", string.Empty).Replace("'", "''").Replace("\"", "'");
    }
}