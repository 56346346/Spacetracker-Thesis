using System.Threading;
using System.Threading.Tasks;
using InstantSync.Core.Delta;
using Neo4j.Driver;

namespace InstantSync.Core.Repositories
{
    /// <summary>
    /// Persists delta packages to Neo4j.
    /// </summary>
    public class Neo4jRepository : IRepository
    {
        private readonly IDriver _driver;

        /// <summary>
        /// Initializes a new instance of the <see cref="Neo4jRepository"/> class.
        /// </summary>
        /// <param name="driver">Neo4j driver.</param>
        public Neo4jRepository(IDriver driver)
        {
            _driver = driver;
        }

        /// <inheritdoc />
        public async Task SaveAsync(DeltaPackage pkg, CancellationToken ct)
        {
            var session = _driver.AsyncSession();
            try
            {
                await session.ExecuteWriteAsync(async tx =>
                {
                    foreach (var elem in pkg.Elements)
                    {
                        await tx.RunAsync("MERGE (e:Element {Guid: $guid}) SET e += $data",
                            new { guid = elem.Guid.ToString(), data = elem.Data.ToString() });
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
