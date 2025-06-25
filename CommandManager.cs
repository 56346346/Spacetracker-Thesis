using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading.Tasks;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB;
using Neo4j.Driver;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using SpaceTracker;
using System.Text.RegularExpressions;

namespace SpaceTracker
{
    public class CommandManager
    {
        public ConcurrentQueue<string> cypherCommands = new ConcurrentQueue<string>();

        private Neo4jConnector _neo4jConnector;
        public Neo4jConnector Neo4jConnector => _neo4jConnector;




        private readonly SemaphoreSlim _cypherLock = new(1, 1);


        private static CommandManager _instance;

        private static readonly object _lock = new object();

        public string SessionId { get; private set; }

        public DateTime LastSyncTime { get; set; } = DateTime.MinValue;
        public List<LogChange> LogChanges { get; } = new();
        public List<LogChangeAcknowledged> LogChangesAcknowledged { get; } = new();
        public int ExpectedSessionCount { get; set; } = 1;

        // Privater Konstruktor; erzeugt eine neue Instanz und initialisiert
        // Session-ID sowie den Zeitstempel der letzten Synchronisation.
        private CommandManager(Neo4jConnector neo4jConnector)
        {
            _neo4jConnector = neo4jConnector;

            SessionId = GenerateSessionId();
            LastSyncTime = LoadLastSyncTime();

            cypherCommands = new ConcurrentQueue<string>();
        }

        // Muss einmalig aufgerufen werden um die Singleton-Instanz anzulegen.
        public static void Initialize(Neo4jConnector neo4jConnector)
        {
            lock (_lock)
            {
                if (_instance != null)
                    throw new InvalidOperationException("CommandManager bereits initialisiert");

                _instance = new CommandManager(neo4jConnector);

            }
        }

        // Öffentliche Instanz-Eigenschaft
        // Liefert die zuvor initialisierte Singleton-Instanz.
        public static CommandManager Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null)
                        throw new InvalidOperationException("CommandManager nicht initialisiert. Zuerst Initialize() aufrufen!");
                    return _instance;
                }
            }
        }

        // Öffentliche Instanz-Eigenschaft
        // Gibt die Ressourcen des Neo4jConnectors frei.

        public void Dispose()
        {
            _neo4jConnector?.Dispose();
        }

        // Überträgt alle gesammelten Cypher-Befehle an Neo4j und aktualisiert
        // anschließend den Sync-Zeitstempel. Optional wird der aktuelle
        // Revit-Status geprüft.
        public async Task ProcessCypherQueueAsync(Document currentDoc = null)
        {
            await _cypherLock.WaitAsync();
            try
            {
                if (cypherCommands.IsEmpty)
                    return;

                var changes = new List<(string Command, string Path)>();
                var ids = new HashSet<long>();
                var idRegex = new Regex(@"ElementId\D+(\d+)");
                while (cypherCommands.TryDequeue(out string cyCommand))
                {
                    string cache = ChangeCacheHelper.WriteChange(cyCommand);
                    changes.Add((cyCommand, cache));
                    var match = idRegex.Match(cyCommand);
                    if (match.Success && long.TryParse(match.Groups[1].Value, out long parsed))
                        ids.Add(parsed);
                }

                await _neo4jConnector.PushChangesAsync(changes, SessionId, Environment.UserName, currentDoc).ConfigureAwait(false);
                LastSyncTime = DateTime.Now;
                PersistSyncTime();
                await _neo4jConnector.UpdateSessionLastSyncAsync(SessionId, LastSyncTime).ConfigureAwait(false);
                await _neo4jConnector.CleanupObsoleteChangeLogsAsync().ConfigureAwait(false);
                if (currentDoc != null)
                {
                    foreach (var id in ids)
                        _ = Task.Run(() => SolibriChecker.CheckElementAsync(new ElementId((int)id), currentDoc));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Neo4j-Error] {ex.Message}");
            }
            finally
            {
                _cypherLock.Release();
            }
        }
        // Schreibt den aktuellen LastSyncTime-Wert in eine Datei im
        // Benutzerprofil.
        public void PersistSyncTime()
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SpaceTracker");
                System.IO.Directory.CreateDirectory(dir);

                var file = System.IO.Path.Combine(dir, $"last_sync_{SessionId}.txt");
                System.IO.File.WriteAllText(file, LastSyncTime.ToString("o"));
            }
            catch (Exception ex)
            {
                Logger.LogCrash("PersistSyncTime", ex);
            }
        }
        // Generiert eine eindeutige Session-ID basierend auf dem Benutzernamen
        private static string GenerateSessionId()
        {
            string user = Environment.UserName;
            string processId = Environment.ProcessId.ToString();
            return $"{user}_{processId}_{Guid.NewGuid().ToString().Substring(0, 8)}";
        }
        // Liest einen zuvor gespeicherten Sync-Zeitstempel aus der Datei oder
        // liefert DateTime.Now wenn keiner vorhanden ist. 
         private DateTime LoadLastSyncTime()
        {
            try
            {
                var dir = System.IO.Path.Combine(
                   Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SpaceTracker");

                var sessionFile = System.IO.Path.Combine(dir, $"last_sync_{SessionId}.txt");
                var globalFile = System.IO.Path.Combine(dir, "last_sync.txt");

                string path = System.IO.File.Exists(sessionFile) ? sessionFile : globalFile;

                if (System.IO.File.Exists(path))
                {
                    var content = System.IO.File.ReadAllText(path);
                    if (DateTime.TryParse(content, out var ts))
                        return ts;
                }
            }
            catch { }

            return DateTime.Now;
        }
        
    }
}