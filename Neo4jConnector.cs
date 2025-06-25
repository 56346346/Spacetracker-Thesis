using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Neo4j.Driver;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using System.IO;
using SpaceTracker;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


namespace SpaceTracker
{
    public class Neo4jConnector : IDisposable
    {
        private readonly IDriver _driver;
        private readonly Microsoft.Extensions.Logging.ILogger<Neo4jConnector> _logger;

        private ConcurrentQueue<string> cypherCommands = new ConcurrentQueue<string>();


        private readonly string _cypherFilePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "SpaceTracker",
    "neo4j_cypher.txt"
);
        // Stellt die Verbindung zu Neo4j her. Die Zugangsdaten können optional
        // angepasst werden.
        public Neo4jConnector(Microsoft.Extensions.Logging.ILogger<Neo4jConnector> logger, string uri = "bolt://localhost:7687",
                                string user = "neo4j",
                               string password = "password")
        {
            _logger = logger;
            _driver = GraphDatabase.Driver(
                            uri,
                            AuthTokens.Basic(user, password),
                            o => o.WithConnectionTimeout(TimeSpan.FromSeconds(15))
                                  .WithMaxConnectionPoolSize(50)
                        );
        }
        // Führt eine Leseabfrage aus und gibt die Ergebnismenge zurück.

        public async Task<List<IRecord>> RunReadQueryAsync(string query, object parameters = null)
        {
            await using var session = _driver.AsyncSession();
            try
            {
                var resultCursor = parameters == null
                     ? await session.RunAsync(query).ConfigureAwait(false)
                    : await session.RunAsync(query, parameters).ConfigureAwait(false);
                return await resultCursor.ToListAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Neo4j-Fehler] Lese-Query fehlgeschlagen: {ex.Message}");
                throw;
            }
        }

        // Schreibt alle Änderungen in einer Transaktion nach Neo4j und legt für
        // jedes Element einen Log-Eintrag an.
        public async Task PushChangesAsync(IEnumerable<(string Command, string CachePath)> changes, string sessionId, string userName, Autodesk.Revit.DB.Document currentDocument = null)
        {
            // 1) Asynchrone Neo4j-Session öffnen
            await using var session = _driver.AsyncSession();

            try
            {
                // 2) Transaction starten
                var tx = await session.BeginTransactionAsync().ConfigureAwait(false);

                // ─────────────────────────────────────────────────────────
                // ► SESSION-KNOTEN ERSTELLEN / UPDATEN (MERGE ganz am Anfang)
                // ─────────────────────────────────────────────────────────
                var initTime = DateTime.Now.ToString("o");
                await tx.RunAsync(
                  @"MERGE (s:Session { id: $session })
              SET s.lastSync = datetime($time)",
                  new
                  {
                      session = sessionId,
                      time = initTime
                  }
              ).ConfigureAwait(false);

                // 3) Regex zum Extrahieren der ElementId aus dem Cypher-String
                var idRegex = new Regex(@"ElementId\D+(\d+)");

                // 4) Alle Commands durchlaufen
                foreach (var change in changes)
                {
                    string cmd = change.Command;
                    string cachePath = change.CachePath;

                    // 4.1) Änderungsbefehl ausführen – jetzt mit $session-Parameter
                    await tx.RunAsync(cmd, new { session = sessionId }).ConfigureAwait(false);

                    // 4.2) Änderungstyp bestimmen (Insert/Modify/Delete)
                    string changeType;
                    if (cmd.Contains("DELETE", StringComparison.OrdinalIgnoreCase))
                        changeType = "Delete";
                    else if (cmd.Contains("MERGE", StringComparison.OrdinalIgnoreCase))
                        changeType = "Insert";
                    else
                        changeType = "Modify";

                    // 4.3) ElementId extrahieren (oder -1, wenn nicht gefunden)
                    long elementId = -1;
                    var match = idRegex.Match(cmd);
                    if (match.Success && long.TryParse(match.Groups[1].Value, out var parsedId))
                        elementId = parsedId;

                    // 4.4) Audit-Log-Einträge erzeugen. Bei "Insert" nur ein Log
                    //      pro ElementId und Session erlauben
                    var logTime = DateTime.UtcNow.ToString("o");

                    string logQuery;
                    if (changeType == "Insert")
                    {
                        logQuery = @"MATCH (s:Session { id: $session })
MERGE (cl:ChangeLog { sessionId: $session, elementId: $eid, type: $type })
ON CREATE SET cl.user = $user, cl.timestamp = datetime($time), cl.cachePath = $path, cl.acknowledged = false
MERGE (s)-[:HAS_LOG]->(cl)";
                    }
                    else
                    {
                        logQuery = @"MATCH (s:Session { id: $session })
CREATE (cl:ChangeLog {
    sessionId: $session,
    user: $user,
    timestamp: datetime($time),
    type: $type,
    elementId: $eid,
      cachePath: $path,
    acknowledged: false
})
MERGE (s)-[:HAS_LOG]->(cl)";
                    }

                    await tx.RunAsync(logQuery,
                        new
                        {
                            session = sessionId,
                            user = userName,
                            time = logTime,
                            type = changeType,
                            eid = elementId,
                            path = cachePath
                        }).ConfigureAwait(false);

                    // lastModifiedUtc setzen
                    if (elementId >= 0)
                    {
                        await tx.RunAsync(
                            "MATCH (e { ElementId: $id }) SET e.lastModifiedUtc = datetime($time)",
                            new { id = elementId, time = logTime }).ConfigureAwait(false);
                    }
                }

                // 5) Transaction committen
                await tx.CommitAsync().ConfigureAwait(false);
                Debug.WriteLine($"[Neo4j] PushChanges: {changes.Count()} Änderungen übertragen und protokolliert.");

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Neo4j Push Fehler] {ex.Message}");
                // 6) Bei Fehler: Rollback
                try
                {
                    await session.CloseAsync().ConfigureAwait(false); // Session sauber schließen
                }
                catch { /* ignore */ }
                throw;
            }
            finally
            {
                // 7) Session schließen
                await session.CloseAsync().ConfigureAwait(false);
            }
        }
        // Prüft, ob alle aktiven Sessions den gleichen Synchronisationsstand haben.
        public async Task<bool> AreAllUsersConsistentAsync()
        {
            await using var session = _driver.AsyncSession();
            try
            {
                var minRes = await session.RunAsync("MATCH (s:Session) RETURN min(s.lastSync) AS minSync").ConfigureAwait(false);
                var minRec = await minRes.SingleAsync().ConfigureAwait(false);
                if (minRec["minSync"] is null)
                    return true;
                var minSync = minRec["minSync"].As<ZonedDateTime>().ToDateTimeOffset().UtcDateTime;

                var maxRes = await session.RunAsync("MATCH (cl:ChangeLog) RETURN max(cl.timestamp) AS lastChange").ConfigureAwait(false);
                var maxRec = await maxRes.SingleAsync().ConfigureAwait(false);
                if (maxRec["lastChange"] is null)
                    return true;
                var lastChange = maxRec["lastChange"].As<ZonedDateTime>().ToDateTimeOffset().UtcDateTime;

                return lastChange <= minSync;
            }
            finally
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
        }

        // Liefert den Sync-Status aller Sessions zur Prüfung, ob alle gepullt haben
        // Liefert eine Liste aller Sessions inklusive Pull-Status.

        public async Task<List<SessionStatus>> GetSessionStatusesAsync()
        {
            var result = new List<SessionStatus>();
            await using var session = _driver.AsyncSession();
            try
            {
                var lastChangeRes = await session.RunAsync("MATCH (cl:ChangeLog) RETURN max(cl.timestamp) AS lastChange").ConfigureAwait(false);
                var lastChangeRec = await lastChangeRes.SingleAsync().ConfigureAwait(false);
                DateTime lastChange = DateTime.MinValue;
                if (lastChangeRec["lastChange"] != null)
                    lastChange = lastChangeRec["lastChange"].As<ZonedDateTime>().ToDateTimeOffset().UtcDateTime;

                var res = await session.RunAsync("MATCH (s:Session) RETURN s.id AS id, s.lastSync AS lastSync").ConfigureAwait(false);
                await res.ForEachAsync(r =>
                {
                    DateTime sync = DateTime.MinValue;
                    if (r["lastSync"] != null)
                        sync = r["lastSync"].As<ZonedDateTime>().ToDateTimeOffset().UtcDateTime;
                    bool pulled = lastChange <= sync;
                    result.Add(new SessionStatus { Id = r["id"].As<string>(), HasPulledAll = pulled });
                }).ConfigureAwait(false);
            }
            finally
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
            return result;
        }
        // Entfernt sämtliche Session- und ChangeLog-Knoten aus der Datenbank.
        public async Task DeleteAllSessionsAndLogsAsync()
        {
            await using var session = _driver.AsyncSession();
            try
            {
                await session.ExecuteWriteAsync(async tx =>
                {
                    await tx.RunAsync("MATCH (cl:ChangeLog) DELETE cl").ConfigureAwait(false);
                    await tx.RunAsync("MATCH (s:Session) DETACH DELETE s").ConfigureAwait(false);
                });
            }
            finally
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
        }
        // Gibt alle noch nicht bestätigten ChangeLogs anderer Sessions zurück.
        public async Task<List<IRecord>> GetPendingChangeLogsAsync(string currentSession)
        {
            string query = @"MATCH (c:ChangeLog)
WHERE c.sessionId <> $session AND c.acknowledged = false
RETURN c.sessionId AS sessionId, c.elementId AS elementId, c.type AS type, c.timestamp AS ts
ORDER BY c.timestamp";
            return await RunReadQueryAsync(query, new { session = currentSession }).ConfigureAwait(false);
        }
        // Markiert alle fremden ChangeLogs als gelesen.
        public async Task AcknowledgeAllAsync(string currentSession)
        {
            await using var session = _driver.AsyncSession();
            try
            {
                await session.ExecuteWriteAsync(async tx =>
                {
                    await tx.RunAsync(@"MATCH (c:ChangeLog)
WHERE c.sessionId <> $session AND c.acknowledged = false
SET c.acknowledged = true", new { session = currentSession }).ConfigureAwait(false);
                });
            }
            finally
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
        }
        // Bestätigt nur Logs der angegebenen Elemente.

        public async Task AcknowledgeSelectedAsync(string currentSession, IEnumerable<long> elementIds)
        {
            await using var session = _driver.AsyncSession();
            try
            {
                await session.ExecuteWriteAsync(async tx =>
                {
                    foreach (var id in elementIds)
                    {
                        await tx.RunAsync(@"MATCH (c:ChangeLog)
WHERE c.sessionId <> $session AND c.elementId = $id
SET c.acknowledged = true",
                            new { session = currentSession, id }).ConfigureAwait(false);
                    }
                });
            }
            finally
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
        }


        // Führt einen beliebigen Cypher-String aus.

        public async Task RunCypherQuery(string query)
        {
            await using var session = _driver.AsyncSession();

            try
            {
                var result = await session.RunAsync(query).ConfigureAwait(false);
                await result.ConsumeAsync().ConfigureAwait(false);
                Debug.WriteLine($"[Neo4j] Query erfolgreich: {query}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Neo4j-Fehler] {ex.Message}");
                throw;
            }
        }
        // Löscht alte ChangeLogs anhand des kleinsten Session-Zeitstempels.

        public async Task CleanupObsoleteChangeLogsAsync()
        {
            await using var session = _driver.AsyncSession();
            try
            {
                // Alte Sessions bereinigen, damit die Mindestberechnung korrekt bleibt
                await RemoveStaleSessionsAsync(TimeSpan.FromDays(1)).ConfigureAwait(false);

                await session.ExecuteWriteAsync(async tx =>
                {
                    // 1) ältesten Zeitstempel aller Sessions ermitteln
                    var res = await tx.RunAsync(
                        "MATCH (s:Session) RETURN min(s.lastSync) AS cutoff").ConfigureAwait(false);
                    var record = await res.SingleAsync().ConfigureAwait(false);
                    var cutoff = record["cutoff"].As<ZonedDateTime>().ToString();

                    // 2) ChangeLogs löschen, die älter sind
                    await tx.RunAsync(
                        @"MATCH (cl:ChangeLog)
                     WHERE cl.timestamp < datetime($cutoff)
                    DELETE cl",
                        new { cutoff = cutoff.ToString() }).ConfigureAwait(false);
                });
            }
            finally
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
        }
        // Entfernt Session-Knoten die länger nicht synchronisiert wurden.

        public async Task RemoveStaleSessionsAsync(TimeSpan maxAge)
        {
            await using var session = _driver.AsyncSession();
            try
            {
                await session.ExecuteWriteAsync(async tx =>
                {
                    var cutoff = DateTime.UtcNow.Subtract(maxAge).ToString("o");
                    await tx.RunAsync(
                        @"MATCH (s:Session)
                          WHERE s.lastSync < datetime($cutoff)
                          DETACH DELETE s",
                        new { cutoff }).ConfigureAwait(false);
                });
            }
            finally
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
        }
        // Aktualisiert das lastSync-Datum einer Session in Neo4j.
        public async Task UpdateSessionLastSyncAsync(string sessionId, DateTime syncTime)
        {
            await using var session = _driver.AsyncSession();
            try
            {
                await session.ExecuteWriteAsync(async tx =>
                {
                    await tx.RunAsync(
                        @"MERGE (s:Session { id: $session })
                          SET s.lastSync = datetime($time)",
                        new { session = sessionId, time = syncTime.ToString("o") }
                    ).ConfigureAwait(false);
                });
            }
            finally
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
        }
        // Schreibt eine Tür in Neo4j (INSERT/UPDATE).

        public async Task UpsertDoorAsync(Dictionary<string, object> args)
        {
            var setParts = new List<string>();
            foreach (var kvp in args)
            {
                if (kvp.Key is "uid" or "user" or "created" or "modified")
                    continue;
                setParts.Add($"d.{kvp.Key} = ${kvp.Key}");
            }
            setParts.Add("d.createdBy = coalesce(d.createdBy,$user)");
            setParts.Add("d.createdAt = coalesce(d.createdAt,$created)");
            setParts.Add("d.lastModifiedUtc = datetime($modified)");
            string cypher = $"MERGE (d:Door {{uid:$uid}}) SET {string.Join(", ", setParts)} RETURN d";

            await using var session = _driver.AsyncSession();
            await using var tx = await session.BeginTransactionAsync().ConfigureAwait(false);
            await tx.RunAsync(cypher, args).ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
            _logger.LogInformation("Door {Uid} upserted", args["uid"]);
        }


        // Spielt zuvor gespeicherte Cypher-Befehle aus der Datei in die Datenbank ein.
        public async Task ExportToNeo4j()
        {
            try
            {
                if (!File.Exists(_cypherFilePath))
                {
                    Debug.WriteLine("[Neo4j] Cypher-Datei nicht gefunden: " + _cypherFilePath);
                    return;
                }

                var commands = await Task.Run(() => File.ReadAllLines(_cypherFilePath)).ConfigureAwait(false);

                await using var session = _driver.AsyncSession();
                foreach (var cmd in commands)
                {
                    if (!string.IsNullOrWhiteSpace(cmd))
                    {
                        try
                        {
                            var result = await session.RunAsync(cmd).ConfigureAwait(false);
                            await result.ConsumeAsync().ConfigureAwait(false);
                            Debug.WriteLine("[Neo4j] Erfolgreich: " + cmd);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("[Neo4j ERROR] Query fehlgeschlagen: " + ex.Message);
                            // Optional: Fehlerhafte Abfragen speichern
                        }
                    }
                }

                Debug.WriteLine("[Neo4j] Export abgeschlossen");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Neo4j Export Error] " + ex.Message);
                throw;
            }
        }
        // ------------------------------------------------------------------
        // Neue API für Node-basierten Datenaustausch
        // Helfer für generische Abfragen mit Mapping-Funktion.

        public async Task<List<T>> RunQueryAsync<T>(string cypher, object parameters, Func<IRecord, T> map)
        {
            await using var session = _driver.AsyncSession();
            _logger.LogDebug("RunQueryAsync: {Cypher}", cypher);
            var cursor = await session.RunAsync(cypher, parameters).ConfigureAwait(false);
            var list = new List<T>();
            await cursor.ForEachAsync(r => list.Add(map(r))).ConfigureAwait(false);
            return list;
        }
        // Erstellt oder aktualisiert eine Wand in Neo4j.
        public async Task UpsertWallAsync(Dictionary<string, object> args)
        {
            var setParts = new List<string>();
            foreach (var kvp in args)
            {
                if (kvp.Key is "uid" or "user" or "created" or "modified")
                    continue;
                setParts.Add($"w.{kvp.Key} = ${kvp.Key}");
            }
            setParts.Add("w.createdBy = coalesce(w.createdBy,$user)");
            setParts.Add("w.createdAt = coalesce(w.createdAt,$created)");
            setParts.Add("w.lastModifiedUtc = datetime($modified)");
            string cypher = $"MERGE (w:Wall {{uid:$uid}}) SET {string.Join(", ", setParts)} RETURN w";
            await using var session = _driver.AsyncSession();
            await using var tx = await session.BeginTransactionAsync().ConfigureAwait(false);
            await tx.RunAsync(cypher, args).ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
            _logger.LogInformation("Wall {Uid} upserted", args["uid"]);
        }
        // Erstellt oder aktualisiert ein Rohr in Neo4j.

        public async Task UpsertPipeAsync(Dictionary<string, object> args)
        {
            var setParts = new List<string>();
            foreach (var kvp in args)
            {
                if (kvp.Key is "uid" or "user" or "created" or "modified")
                    continue;
                setParts.Add($"p.{kvp.Key} = ${kvp.Key}");
            }
            setParts.Add("p.createdBy = coalesce(p.createdBy,$user)");
            setParts.Add("p.createdAt = coalesce(p.createdAt,$created)");
            setParts.Add("p.lastModifiedUtc = datetime($modified)");
            string cypher = $"MERGE (p:Pipe {{uid:$uid}}) SET {string.Join(", ", setParts)} RETURN p";

            await using var session = _driver.AsyncSession();
            await using var tx = await session.BeginTransactionAsync().ConfigureAwait(false);
            await tx.RunAsync(cypher, args).ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
            _logger.LogInformation("Pipe {Uid} upserted", args["uid"]);
        }
        // Erstellt oder aktualisiert einen ProvisionalSpace-Knoten.

        public async Task UpsertProvisionalSpaceAsync(string guid, Dictionary<string, object> props)
        {
            var setParts = props.Keys
              .Where(k => k != "guid")
              .Select(k => $"p.{k} = ${k}")
              .ToList();
            string cypher = $"MERGE (p:ProvisionalSpace {{guid:$guid}}) SET {string.Join(", ", setParts)}";
            props["guid"] = guid;

            await using var session = _driver.AsyncSession();
            await using var tx = await session.BeginTransactionAsync().ConfigureAwait(false);
            await tx.RunAsync(cypher, props).ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
            _logger.LogInformation("ProvisionalSpace {Guid} upserted", guid);
        }
        // Verbindet einen ProvisionalSpace mit einer Wand.
        public async Task LinkProvisionalSpaceToWallAsync(string guid, long wallId)
        {
            const string cypher = @"MATCH (w:Wall {ElementId:$wid}), (p:ProvisionalSpace {guid:$guid})
MERGE (w)-[:HAS_PROV_SPACE]->(p)";
            await using var session = _driver.AsyncSession();
            await session.RunAsync(cypher, new { wid = wallId, guid }).ConfigureAwait(false);
        }

        // Verknüpft ein Rohr mit einem ProvisionalSpace.

        public async Task LinkPipeToProvisionalSpaceAsync(string pipeUid, string provGuid)
        {
            const string cypher = @"MATCH (p:Pipe {uid:$uid}), (ps:ProvisionalSpace {guid:$guid})
MERGE (p)-[:CONTAINED_IN]->(ps)";
            await using var session = _driver.AsyncSession();
            await session.RunAsync(cypher, new { uid = pipeUid, guid = provGuid }).ConfigureAwait(false);
        }

        // Holt alle seit einem Zeitpunkt geänderten Wände.

        public async Task<List<WallNode>> GetUpdatedWallsAsync(DateTime sinceUtc)
        {
            const string cypher = @"MATCH (w:Wall)
WHERE  w.lastModifiedUtc > datetime($since)
RETURN w";
            _logger.LogDebug("GetUpdatedWalls since {Time}", sinceUtc);
            var list = await RunQueryAsync(cypher, new { since = sinceUtc.ToString("o") }, r =>
            {
                var node = r["w"].As<INode>();
                return new WallNode
                (
                    node.Properties.ContainsKey("uid") ? node.Properties["uid"].As<string>() : string.Empty,
                    node.Properties.TryGetValue("elementId", out var elemId) ? elemId.As<long>() : -1,
                    node.Properties["typeId"].As<long>(),
                    node.Properties.TryGetValue("typeName", out var typeName) ? typeName.As<string>() : string.Empty,
                    node.Properties.TryGetValue("familyName", out var familyName) ? familyName.As<string>() : string.Empty,
                    node.Properties["levelId"].As<long>(),
                    node.Properties["x1"].As<double>(),
                    node.Properties["y1"].As<double>(),
                    node.Properties["z1"].As<double>(),
                    node.Properties["x2"].As<double>(),
                    node.Properties["y2"].As<double>(),
                    node.Properties["z2"].As<double>(),
                    node.Properties["height_mm"].As<double>(),
                    node.Properties["thickness_mm"].As<double>(),
                    node.Properties["structural"].As<bool>(),
                    node.Properties.TryGetValue("flipped", out var flipped) && flipped.As<bool>(),
                    node.Properties.TryGetValue("base_offset_mm", out var baseOffset) ? baseOffset.As<double>() : 0.0,
                    node.Properties.TryGetValue("location_line", out var locLine) ? locLine.As<int>() : (int)WallLocationLine.WallCenterline
                    );
            }).ConfigureAwait(false);
            _logger.LogInformation("Pulled {Count} walls", list.Count);
            return list;
        }

        // Holt alle seit einem Zeitpunkt geänderten Türen.
        public async Task<List<DoorNode>> GetUpdatedDoorsAsync(DateTime sinceUtc)
        {
            const string cypher = @"MATCH (d:Door)
WHERE  d.lastModifiedUtc > datetime($since)
RETURN d";
            _logger.LogDebug("GetUpdatedDoors since {Time}", sinceUtc);
            var list = await RunQueryAsync(cypher, new { since = sinceUtc.ToString("o") }, r =>
            {
                var node = r["d"].As<INode>();
                return new DoorNode
                (
                    node.Properties.ContainsKey("uid") ? node.Properties["uid"].As<string>() : string.Empty,
                    node.Properties.TryGetValue("elementId", out var elemId) ? elemId.As<long>() : -1,
                    node.Properties["typeId"].As<long>(),
                    node.Properties.TryGetValue("familyName", out var famName) ? famName.As<string>() : string.Empty,
                    node.Properties.TryGetValue("symbolName", out var symName) ? symName.As<string>() : string.Empty,
                    node.Properties["levelId"].As<long>(),
                    node.Properties.TryGetValue("hostId", out var hostId) ? hostId.As<long>() : -1,
                    node.Properties.TryGetValue("x", out var x) ? x.As<double>() : 0.0,
                    node.Properties.TryGetValue("y", out var y) ? y.As<double>() : 0.0,
                    node.Properties.TryGetValue("z", out var z) ? z.As<double>() : 0.0,
                    node.Properties.TryGetValue("rotation", out var rot) ? rot.As<double>() : 0.0,
                    node.Properties.TryGetValue("width", out var width) ? width.As<double>() : 0.0,
                    node.Properties.TryGetValue("height", out var height) ? height.As<double>() : 0.0,
                    node.Properties.TryGetValue("thickness", out var thickness) ? thickness.As<double>() : 0.0
                );
            }).ConfigureAwait(false);
            _logger.LogInformation("Pulled {Count} doors", list.Count);
            return list;
        }
        // Setzt LogChanges auf "acknowledged" sobald alle Online-Nutzer sie empfangen haben.
        public async Task<int> AcknowledgeLogChangesAsync(CancellationToken cancellationToken = default)
        {
            const string cypher = @"MATCH (lc:LogChanges)
WITH lc,
     SIZE( (lc)<-[:RECEIVED]-(:User {online:true}) ) AS rcv,
     SIZE( (:User {online:true}) )                  AS all
WHERE rcv = all AND all > 0
CALL {
  WITH lc
  REMOVE lc:LogChanges
  SET   lc:LogChangesAcknowledged,
        lc.tsAcknowledged = datetime()
} IN TRANSACTIONS OF 1000 ROWS
RETURN count(*) AS updated";

            await using var session = _driver.AsyncSession();
            var sw = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var updated = await session.ExecuteWriteAsync(async tx =>
                {
                    var cursor = await tx.RunAsync(cypher).ConfigureAwait(false);
                    var record = await cursor.SingleAsync().ConfigureAwait(false);
                    return record["updated"].As<int>();
                }).ConfigureAwait(false);

                sw.Stop();
                _logger.LogInformation("Acknowledged {Count} LogChanges in {Elapsed}", updated, sw.Elapsed);
                return updated;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to acknowledge LogChanges");
                throw;
            }
            finally
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
        }
        // Speichert einen Status für einen bestimmten LogChange.
        public async Task SetLogChangeStatusAsync(long elementId, string sessionId, string status, string code)
        {
            const string cypher = @"MATCH (cl:ChangeLog { elementId: $id, sessionId: $session }) SET cl.status = $status, cl.errorCode = $code";
            await using var session = _driver.AsyncSession();
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(cypher, new { id = elementId, session = sessionId, status, code }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        // Schließt den Neo4j-Treiber und gibt Ressourcen frei.
        public void Dispose()
        {
            _driver?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}




