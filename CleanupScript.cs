using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Debug;

namespace SpaceTracker
{
    /// <summary>
    /// Utility script to clean up invalid ChangeLog entries
    /// </summary>
    public static class CleanupScript
    {
        public static async Task CleanupInvalidChangeLogEntries()
        {
            try
            {
                // Create a simple logger for the connector
                using var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
                var logger = loggerFactory.CreateLogger<Neo4jConnector>();
                
                var connector = new Neo4jConnector(logger);
                await connector.CleanupInvalidChangeLogEntriesAsync();
                
                Console.WriteLine("Cleanup completed successfully. Check sync.log for details.");
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Failed to cleanup invalid ChangeLog entries", ex);
                Console.WriteLine($"Cleanup failed: {ex.Message}");
                throw;
            }
        }
    }
}
