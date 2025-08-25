using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Linq;
using Neo4j.Driver;

namespace SpaceTracker
{
    /// <summary>
    /// Test utility to diagnose and repair GOT_CHANGED relationship issues
    /// </summary>
    public class GotChangedRepairTool
    {
        private readonly Neo4jConnector _connector;

        public GotChangedRepairTool(Neo4jConnector connector)
        {
            _connector = connector;
        }

        /// <summary>
        /// Run full analysis and repair of GOT_CHANGED relationships
        /// </summary>
        public async Task AnalyzeAndRepairAsync()
        {
            try
            {
                Logger.LogToFile("=== Starting GOT_CHANGED Relationships Analysis and Repair ===", "sync.log");
                
                // Step 1: Analyze current situation
                Logger.LogToFile("Step 1: Analyzing current GOT_CHANGED relationships...", "sync.log");
                await _connector.AnalyzeGotChangedRelationshipsAsync();
                
                // Step 2: Repair missing relationships
                Logger.LogToFile("Step 2: Repairing missing GOT_CHANGED relationships...", "sync.log");
                await _connector.RepairMissingGotChangedRelationshipsAsync();
                
                // Step 3: Re-analyze to verify repair
                Logger.LogToFile("Step 3: Re-analyzing after repair...", "sync.log");
                await _connector.AnalyzeGotChangedRelationshipsAsync();
                
                Logger.LogToFile("=== GOT_CHANGED Repair Process Completed ===", "sync.log");
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Failed to complete GOT_CHANGED repair process", ex);
                throw;
            }
        }

        /// <summary>
        /// Test method to create a ChangeLog entry with proper GOT_CHANGED relationship
        /// </summary>
        public async Task TestCreateChangeLogWithRelationshipAsync(int elementId, string operation = "Insert")
        {
            try
            {
                Logger.LogToFile($"=== Testing ChangeLog creation for ElementId {elementId} ===", "sync.log");
                
                // Use central method instead of direct Cypher
                await _connector.CreateChangeLogEntryWithRelationshipsAsync(elementId, operation, "TEST_SESSION");
                
                Logger.LogToFile($"Test ChangeLog creation completed for ElementId {elementId}", "sync.log");
            }
            catch (Exception ex)
            {
                Logger.LogCrash($"Failed to create test ChangeLog for element {elementId}", ex);
                throw;
            }
        }

        /// <summary>
        /// Test method that can be called from other parts of the application
        /// </summary>
        public static async Task RunRepairTool()
        {
            try
            {
                // Create a simple null logger for the Neo4jConnector
                using var loggerFactory = LoggerFactory.Create(builder => { });
                var logger = loggerFactory.CreateLogger<Neo4jConnector>();
                
                var connector = new Neo4jConnector(logger);
                var repairTool = new GotChangedRepairTool(connector);
                await repairTool.AnalyzeAndRepairAsync();
                
                connector.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Failed to run GOT_CHANGED repair tool", ex);
            }
        }

        /// <summary>
        /// Test method to create a test ChangeLog entry for a specific element ID
        /// </summary>
        public static async Task TestChangeLogCreation(int elementId)
        {
            try
            {
                using var loggerFactory = LoggerFactory.Create(builder => { });
                var logger = loggerFactory.CreateLogger<Neo4jConnector>();
                
                var connector = new Neo4jConnector(logger);
                var repairTool = new GotChangedRepairTool(connector);
                
                await repairTool.TestCreateChangeLogWithRelationshipAsync(elementId);
                
                connector.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogCrash($"Failed to test ChangeLog creation for element {elementId}", ex);
            }
        }
    }
}
