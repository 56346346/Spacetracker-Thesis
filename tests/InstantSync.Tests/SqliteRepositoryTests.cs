using System.IO;
using System.Threading;
using System.Threading.Tasks;
using InstantSync.Core.Delta;
using InstantSync.Core.Repositories;
using Xunit;

namespace SpaceTracker.tests.InstantSync.Tests
{
    public class SqliteRepositoryTests
    {
        [Fact]
        public async Task SaveAsync_CreatesDatabase()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var repo = new SqliteRepository(path);
            var pkg = new DeltaPackage();

            await repo.SaveAsync(pkg, CancellationToken.None);

            Assert.True(File.Exists(path));
        }
    }
}
