using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Neo4j.Driver;
using System.Diagnostics;
using System.Linq;
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
                    ? await session.RunAsync(query)
                    : await session.RunAsync(query, parameters);
                return await resultCursor.ToListAsync();
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
        public async Task PushChangesAsync(IEnumerable<string> cypherCommands, string userId)
        {
            // 1) Asynchrone Neo4j-Session öffnen
            var session = _driver.AsyncSession();

            try
            {
                // 2) Transaction starten
                var tx = await session.BeginTransactionAsync();

                // ─────────────────────────────────────────────────────────
                // ► SESSION-KNOTEN ERSTELLEN / UPDATEN (MERGE ganz am Anfang)
                // ─────────────────────────────────────────────────────────
                var initTime = DateTime.UtcNow.ToString("o");
                await tx.RunAsync(
                    @"MERGE (s:Session { id: $session })
              SET s.lastSync = datetime($time)",
                    new
                    {
                        session = userId,
                        time = initTime
                    }
                );

                // 3) Regex zum Extrahieren der ElementId aus dem Cypher-String
                var idRegex = new Regex(@"ElementId\D+(\d+)");

                // 4) Alle Commands durchlaufen
                foreach (string cmd in cypherCommands)
                {
                    // 4.1) Änderungsbefehl ausführen – jetzt mit $session-Parameter
                    await tx.RunAsync(cmd, new { session = userId });

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

                    // 4.4) Audit-Log-Eintrag erzeugen
                    //     Hier kein zweites MERGE auf Session nötig, da oben bereits erfolgt
                    var logTime = DateTime.UtcNow.ToString("o");
                    await tx.RunAsync(
                        @"CREATE (cl:ChangeLog {
                    sessionId: $session,
                    timestamp: datetime($time),
                    type:      $type,
                    elementId: $eid
                  })",
                        new
                        {
                            session = userId,
                            time = logTime,
                            type = changeType,
                            eid = elementId
                        }
                    );
                }

                // 5) Transaction committen
                await tx.CommitAsync();
                Debug.WriteLine($"[Neo4j] PushChanges: {cypherCommands.Count()} Änderungen übertragen und protokolliert.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Neo4j Push Fehler] {ex.Message}");
                // 6) Bei Fehler: Rollback
                try
                {
                    await session.CloseAsync(); // Session sauber schließen
                }
                catch { /* ignore */ }
                throw;
            }
            finally
            {
                // 7) Session schließen
                await session.CloseAsync();
            }
        }


        public async Task RunCypherQuery(string query)
        {
            await using var session = _driver.AsyncSession();

            try
            {
                var result = await session.RunAsync(query);
                await result.ConsumeAsync();
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
                await session.ExecuteWriteAsync(async tx =>
                {
                    // 1) ältesten Zeitstempel aller Sessions ermitteln
                    var res = await tx.RunAsync(
                        "MATCH (s:Session) RETURN min(s.lastSync) AS cutoff");
                    var record = await res.SingleAsync();
                    var cutoff = record["cutoff"].As<ZonedDateTime>();

                    // 2) ChangeLogs löschen, die älter sind
                    await tx.RunAsync(
                        @"MATCH (cl:ChangeLog)
                  WHERE cl.timestamp < $cutoff
                  DELETE cl",
                        new { cutoff = cutoff.ToString() });
                });
            }
            finally
            {
                await session.CloseAsync();
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

                var commands = await Task.Run(() => File.ReadAllLines(_cypherFilePath));

                await using var session = _driver.AsyncSession();
                foreach (var cmd in commands)
                {
                    if (!string.IsNullOrWhiteSpace(cmd))
                    {
                        try
                        {
                            var result = await session.RunAsync(cmd);
                            await result.ConsumeAsync();
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




