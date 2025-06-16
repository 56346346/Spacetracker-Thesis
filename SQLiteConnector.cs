using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Autodesk.Revit.DB;
using SQLitePCL;
using System.IO;
using System.Linq;
using SpaceTracker;

namespace SpaceTracker
{
    public class SQLiteConnector : IDisposable
    {

        private readonly string ConnectionString;

        private bool _isBlocked = false;
        private DateTime _blockedUntil = DateTime.MinValue;


        public SQLiteConnector()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dbDirectory = Path.Combine(appDataPath, "SpaceTracker");
            Directory.CreateDirectory(dbDirectory); // Ordner erstellen

            ConnectionString = $"Data Source={Path.Combine(dbDirectory, "Datenbank.db")}";
            Batteries.Init();
            InitializeDatabase();


            Task.Run(async () =>
    {
        while (true)
        {
            if (_blockedUntil < DateTime.Now) _isBlocked = false;
            await Task.Delay(5000);
        }
    });
        }

        private void InitializeDatabase()
        {
            try
            {
                using var connection = new SqliteConnection(ConnectionString);

                {
                    connection.Open();

                    var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Level (
                    ElementId INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Room (
                    ElementId INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL,
                    LevelId INTEGER,
                    FOREIGN KEY(LevelId) REFERENCES Level(ElementId)
                );

                CREATE TABLE IF NOT EXISTS Wall (
                    ElementId INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL,
                    LevelId INTEGER,
                    Type TEXT NOT NULL, 
                    FOREIGN KEY(LevelId) REFERENCES Level(ElementId)
                );

                CREATE TABLE IF NOT EXISTS Door (
                    ElementId INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL,
                    WallId INTEGER,
                    FOREIGN KEY(WallId) REFERENCES Wall(ElementId)
                );

                CREATE TABLE IF NOT EXISTS contains (
                    LevelId INTEGER,
                    ElementId INTEGER,
                    PRIMARY KEY(LevelId, ElementId),
                    FOREIGN KEY(LevelId) REFERENCES Level(ElementId)
                );

                CREATE TABLE IF NOT EXISTS bounds (
                    WallId INTEGER,
                    RoomId INTEGER,
                    PRIMARY KEY(WallId, RoomId),
                    FOREIGN KEY(WallId) REFERENCES Wall(ElementId),
                    FOREIGN KEY(RoomId) REFERENCES Room(ElementId)
                );

                CREATE TABLE IF NOT EXISTS Changes (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ElementId TEXT NOT NULL,
                    ChangeType TEXT NOT NULL,
                    Timestamp DATETIME NOT NULL,
                    SessionId TEXT
                );";

                    cmd.ExecuteNonQuery();
                }
            }

            catch (Exception ex)
            {
                Debug.WriteLine($"[SQLite-Error] Initialization: {ex.Message}");
                throw;
            }
        }


        public void RunSQLQuery(string query)
        {
            if (_isBlocked) return;

            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = query;
                command.ExecuteNonQuery();
            }
        }

        public async Task RunSQLQueryAsync(string query)
        {
            if (_isBlocked) return;
             using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
             using var command = connection.CreateCommand();
            command.CommandText = query;
            await command.ExecuteNonQueryAsync();
        }

        public async Task LogChangesAsync(
            List<Element> added,
            List<ElementId> deleted,
            List<Element> modified)
        {

            if (_isBlocked) return;

            try
            {
                var addedIds = added.Select(e => e.Id).ToList();
                var modifiedIds = modified.Select(e => e.Id).ToList();
                using (var connection = new SqliteConnection(ConnectionString))
                {
                    await connection.OpenAsync();

                    using var transaction = connection.BeginTransaction();
                    var timestamp = DateTime.UtcNow.ToString("o");

                    await ProcessChanges(connection, addedIds, "Added", timestamp);
                    await ProcessChanges(connection, deleted, "Deleted", timestamp);
                    await ProcessChanges(connection, modifiedIds, "Modified", timestamp);

                    transaction.Commit();
                }
                Debug.WriteLine($"[SQLite] Successfully logged {added.Count + deleted.Count + modified.Count} changes");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // Tabellenfehler
            {
                Debug.WriteLine($"[SQLite] Tabelle fehlt: {ex.Message}");
                InitializeDatabase(); // Re-Initialisierung
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SQLite] Kritischer Fehler: {ex.Message}");
                _isBlocked = true;
            }
        }


        private async Task ProcessChanges(
                    SqliteConnection connection,
                    IEnumerable<ElementId> elements,
                    string changeType,
                    string timestamp)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO Changes (ElementId, ChangeType, Timestamp)
                VALUES ($elementId, $changeType, $timestamp)";

            var paramElementId = command.CreateParameter();
            paramElementId.ParameterName = "$elementId";
            command.Parameters.Add(paramElementId);

            var paramChangeType = command.CreateParameter();
            paramChangeType.ParameterName = "$changeType";
            paramChangeType.Value = changeType;
            command.Parameters.Add(paramChangeType);

            var paramTimestamp = command.CreateParameter();
            paramTimestamp.ParameterName = "$timestamp";
            paramTimestamp.Value = timestamp;
            command.Parameters.Add(paramTimestamp);

            foreach (var id in elements)
            {
                paramElementId.Value = id.ToString();
                await command.ExecuteNonQueryAsync();
            }
        }







        public void Dispose()
        {

            GC.SuppressFinalize(this);
        }
    }
}
