using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using static System.Environment;

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
            Directory.CreateDirectory(Path.Combine(temp, "SpaceTracker", "log"));
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SpaceTracker",
               "log");

            // Stelle sicher, dass der Ordner existiert
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            // Nur Inhalte löschen, nicht den Ordner selbst
            foreach (var file in Directory.GetFiles(logDir))
            {
                // Truncate statt Delete, um Sperrkonflikte zu vermeiden
                using (var fs = new FileStream(
                    file,
                    FileMode.Truncate,
                    FileAccess.Write,
                    FileShare.ReadWrite))
                { }
            }

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

            Assert.True(File.Exists(logDir));
            string content = File.ReadAllText(logDir);
            Assert.Contains("boom", content);
        }
    }
}