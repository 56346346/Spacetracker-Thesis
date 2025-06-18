using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading.Tasks;
using Autodesk.Revit.DB.Events;
using Neo4j.Driver;
using System.Collections.Concurrent;
using System.Threading;
using SpaceTracker;

namespace SpaceTracker
{
    public class CommandManager
    {
        public ConcurrentQueue<string> cypherCommands = new ConcurrentQueue<string>();
        public ConcurrentQueue<string> sqlCommands = new ConcurrentQueue<string>();
        private Neo4jConnector _neo4jConnector;
        public Neo4jConnector Neo4jConnector => _neo4jConnector;
        private SQLiteConnector _sqlConnector;

        public SQLiteConnector SqlConnector => _sqlConnector;

        private readonly SemaphoreSlim _cypherLock = new(1, 1);


        private static CommandManager _instance;

        private static readonly object _lock = new object();

        public string SessionId { get; private set; }

        public DateTime LastSyncTime { get; set; } = DateTime.MinValue;

         private readonly CommandManager cmdManager;





        private CommandManager(Neo4jConnector neo4jConnector, SQLiteConnector sqlconnector)
        {
            _neo4jConnector = neo4jConnector;
            _sqlConnector = sqlconnector;
            SessionId = GenerateSessionId();
            LastSyncTime = LoadLastSyncTime();

            cypherCommands = new ConcurrentQueue<string>();
            sqlCommands = new ConcurrentQueue<string>();


        }

        public void EnqueueSql(string sql)
        {
            // sqlCommands.Enqueue(sql);
            //_ = SqlCommandUpdateAsync();
            //
            //
            //
        }


        private async Task SqlCommandUpdateAsync()
        {
            while (sqlCommands.TryDequeue(out var cmd))
            {
                try
                {
                    await _sqlConnector.RunSQLQueryAsync(cmd).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SQLite Error] {ex}");
                    Logger.LogCrash("SQLite Error", ex);
                }
            }
        }

        public static void Initialize(Neo4jConnector neo4jConnector, SQLiteConnector sqlConnector)
        {
            lock (_lock)
            {
                if (_instance != null)
                    throw new InvalidOperationException("CommandManager bereits initialisiert");

                _instance = new CommandManager(neo4jConnector, sqlConnector);

            }
        }

        // Öffentliche Instanz-Eigenschaft
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
        private async void SqlCommandUpdate(object sender, NotifyCollectionChangedEventArgs e)
        {
            while (sqlCommands.TryDequeue(out string sql))
            {
                try
                {
                    await Task.Run(() => _sqlConnector.RunSQLQueryAsync(sql));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SQLite Error] {ex.Message}");
                }
            }
        }

        // Öffentliche Instanz-Eigenschaft
        public void Dispose()
        {
            _neo4jConnector?.Dispose();
            _sqlConnector?.Dispose();
        }


        public async Task ProcessCypherQueueAsync()
        {
            await _cypherLock.WaitAsync();
            try
            {
                while (cypherCommands.TryDequeue(out string cyCommand))
                {
                    await _neo4jConnector.RunCypherQuery(cyCommand).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Neo4j-Error] {ex.Message}");
            }
            finally { _cypherLock.Release(); }
        }

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
        
         private string GenerateSessionId()
        {
            string user = Environment.UserName;
            string processId = Process.GetCurrentProcess().Id.ToString();
            return $"{user}_{processId}_{Guid.NewGuid().ToString().Substring(0, 8)}";
        }

        private DateTime LoadLastSyncTime()
        {
            try
            {
                var dir = System.IO.Path.Combine(
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

            return DateTime.UtcNow;
        }
    }
}