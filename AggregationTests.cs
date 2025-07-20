using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace SpaceTracker.Tests
{
    public class AggregationTests
    {
        [Fact]
        public async Task WhenUpsertFails_AllInnerExceptionsAreLogged()
        {
            // redirect log directory to temp
            var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Environment.SetEnvironmentVariable("HOME", temp);
            Directory.CreateDirectory(Path.Combine(temp, "SpaceTracker"));
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceTracker", "crash.log");
            if (File.Exists(logPath)) File.Delete(logPath);

            var tasks = new List<Task>
            {
                Task.Run(() => throw new InvalidOperationException("boom"))
            };

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (AggregateException agg)
            {
                foreach (var ex in agg.Flatten().InnerExceptions)
                    Logger.LogCrash("Test", ex);
            }

            Assert.True(File.Exists(logPath));
            string content = File.ReadAllText(logPath);
            Assert.Contains("boom", content);
        }
    }
}