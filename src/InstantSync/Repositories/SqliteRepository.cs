

using System.IO;
using Microsoft.Data.Sqlite;

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using InstantSync.Core.Delta;

namespace InstantSync.Core.Repositories
{
    /// <summary>
    /// Persists delta packages to a SQLite database.
    /// </summary>
    public class SqliteRepository : IRepository
    {
        private readonly string _dbPath;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqliteRepository"/> class.
        /// </summary>
        /// <param name="dbPath">Database file path.</param>
        public SqliteRepository(string dbPath)
        {
            _dbPath = dbPath;
        }

        /// <inheritdoc />
        public async Task SaveAsync(DeltaPackage pkg, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

  using var conn = new SqliteConnection($"Data Source={_dbPath}");
              await conn.OpenAsync(ct).ConfigureAwait(false);

            const string sql = @"CREATE TABLE IF NOT EXISTS Packages (
Id TEXT PRIMARY KEY,
Json TEXT NOT NULL)";
            await conn.ExecuteAsync(sql).ConfigureAwait(false);

            string json = JsonSerializer.Serialize(pkg);
            await conn.ExecuteAsync(
                "INSERT OR REPLACE INTO Packages(Id,Json) VALUES (@id,@json)",
                new { id = pkg.PackageId.ToString(), json }).ConfigureAwait(false);
        }
    }
}
