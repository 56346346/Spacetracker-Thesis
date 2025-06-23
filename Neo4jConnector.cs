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


namespace SpaceTracker
{
    public class Neo4jConnector : IDisposable
    {
        private readonly IDriver _driver;
        private ConcurrentQueue<string> cypherCommands = new ConcurrentQueue<string>();


        private readonly string _cypherFilePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "SpaceTracker",
    "neo4j_cypher.txt"
);
        /// <summary>
        /// public constructor
        /// </summary>
        public Neo4jConnector(string uri = "bolt://localhost:7687",
                        string user = "neo4j",
                        string password = "password")
        {
            _driver = GraphDatabase.Driver(
                            uri,
                            AuthTokens.Basic(user, password),
                            o => o.WithConnectionTimeout(TimeSpan.FromSeconds(15))
                                  .WithMaxConnectionPoolSize(50)
                        );
        }

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

        /// <summary>
        /// Überträgt eine Liste von Cypher-Befehlen als Atomar-Transaktion an Neo4j 
        /// und protokolliert jede Änderung im ChangeLog (mit Benutzer & Timestamp).
        /// </summary>
    public async Task PushChangesAsync(IEnumerable<(string Command, string CachePath)> changes, string sessionId, string userName, Autodesk.Revit.DB.Document currentDocument = null)        {
            // 1) Asynchrone Neo4j-Session öffnen
            var session = _driver.AsyncSession();

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
                    if (cmd.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) >= 0)
                        changeType = "Delete";
                    else if (cmd.IndexOf("MERGE", StringComparison.OrdinalIgnoreCase) >= 0)
                        changeType = "Insert";
                    else
                        changeType = "Modify";

                    // 4.3) ElementId extrahieren (oder -1, wenn nicht gefunden)
                    long elementId = -1;
                    var match = idRegex.Match(cmd);
                    if (match.Success)
                        long.TryParse(match.Groups[1].Value, out elementId);

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
           

                // Nach Abschluss der Transaktion den aktuellen Revit-Status validieren
                if (currentDocument != null)
                {
                    var errs = SolibriRulesetValidator.Validate(currentDocument);
                    var sev = errs.Count == 0 ? Severity.Info : errs.Max(e => e.Severity);
                    SpaceTrackerClass.UpdateConsistencyCheckerButton(sev);
                }            }
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

        public async Task<bool> AreAllUsersConsistentAsync()
        {
            var session = _driver.AsyncSession();
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
        public async Task<List<SessionStatus>> GetSessionStatusesAsync()
        {
            var result = new List<SessionStatus>();
            var session = _driver.AsyncSession();
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

        public async Task DeleteAllSessionsAndLogsAsync()
        {
            var session = _driver.AsyncSession();
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


        public async Task<List<IRecord>> GetPendingChangeLogsAsync(string currentSession)
        {
            string query = @"MATCH (c:ChangeLog)
WHERE c.sessionId <> $session AND c.acknowledged = false
RETURN c.sessionId AS sessionId, c.elementId AS elementId, c.type AS type, c.timestamp AS ts
ORDER BY c.timestamp";
            return await RunReadQueryAsync(query, new { session = currentSession }).ConfigureAwait(false);
        }

        public async Task AcknowledgeAllAsync(string currentSession)
        {
            var session = _driver.AsyncSession();
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

        public async Task AcknowledgeSelectedAsync(string currentSession, IEnumerable<long> elementIds)
        {
            var session = _driver.AsyncSession();
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

        public async Task CleanupObsoleteChangeLogsAsync()
        {
            var session = _driver.AsyncSession();
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

        public async Task RemoveStaleSessionsAsync(TimeSpan maxAge)
        {
            var session = _driver.AsyncSession();
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

        public async Task UpdateSessionLastSyncAsync(string sessionId, DateTime syncTime)
        {
            var session = _driver.AsyncSession();
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





        public void Dispose()
        {
            _driver?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}




