using System;
using System.Threading;
using System.Threading.Tasks;
using Neo4j.Driver;

namespace SpaceTracker
{
    /// <summary>
    /// Event-based change notification system that listens for ChangeLog updates in Neo4j
    /// and triggers immediate pull operations instead of polling.
    /// </summary>
    public class Neo4jChangeNotifier : IDisposable
    {
        private readonly IDriver _driver;
        private readonly object _lock = new object();
        private bool _disposed = false;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _notificationTask;

        // Event that fires when a ChangeLog entry is created that requires a pull
        public event Action<string> ChangeLogCreated;

        public Neo4jChangeNotifier(IDriver driver)
        {
            _driver = driver;
            _cancellationTokenSource = new CancellationTokenSource();
            
            Logger.LogToFile("NEO4J CHANGE NOTIFIER: Starting event-based change notification system", "sync.log");
            
            // Start background notification listener
            _notificationTask = Task.Run(ListenForChangeLogNotifications, _cancellationTokenSource.Token);
        }

        /// <summary>
        /// DEPRECATED: Use Neo4jConnector.CreateChangeLogEntryWithRelationshipsAsync instead
        /// </summary>
        public async Task CreateChangeLogWithNotificationAsync(int elementId, string operation, string sourceSessionId, string targetSessionId)
        {
            try
            {
                Logger.LogToFile($"NEO4J CHANGE NOTIFY: Redirecting to central ChangeLog method for element {elementId}, operation {operation}, from {sourceSessionId} to {targetSessionId}", "sync.log");
                
                // Use the central method instead
                var connector = new Neo4jConnector(null); // Use null logger 
                await connector.CreateChangeLogEntryWithRelationshipsAsync(elementId, operation, sourceSessionId);
                connector.Dispose();
                
                Logger.LogToFile($"NEO4J CHANGE NOTIFY SUCCESS: Created ChangeLog via central method, triggering immediate notification for target session {targetSessionId}", "sync.log");
                
                // Trigger immediate notification instead of waiting for poll
                OnChangeLogCreated(targetSessionId);
            }
            catch (Exception ex)
            {
                Logger.LogToFile($"NEO4J CHANGE NOTIFY ERROR: Failed to create ChangeLog entry: {ex.Message}", "sync.log");
                Logger.LogCrash("Neo4jChangeNotifier CreateChangeLogWithNotification error", ex);
                throw;
            }
        }

        /// <summary>
        /// Background task that simulates Neo4j change stream (in absence of native change streams)
        /// This is a lightweight check that only runs when changes are expected
        /// </summary>
        private async Task ListenForChangeLogNotifications()
        {
            Logger.LogToFile("NEO4J CHANGE LISTENER: Starting background change listener", "sync.log");
            
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // Only check every 1 second instead of 3 seconds for faster response
                    await Task.Delay(1000, _cancellationTokenSource.Token);
                    
                    if (_disposed) break;
                    
                    // Check for very recent ChangeLog entries (last 2 seconds)
                    await CheckForRecentChangeLogsAsync();
                }
                catch (OperationCanceledException)
                {
                    Logger.LogToFile("NEO4J CHANGE LISTENER: Background listener cancelled", "sync.log");
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogToFile($"NEO4J CHANGE LISTENER ERROR: Exception in background listener: {ex.Message}", "sync.log");
                    Logger.LogCrash("Neo4jChangeNotifier listener error", ex);
                }
            }
            
            Logger.LogToFile("NEO4J CHANGE LISTENER: Background change listener stopped", "sync.log");
        }

        /// <summary>
        /// Checks for very recent ChangeLog entries and triggers notifications
        /// </summary>
        private async Task CheckForRecentChangeLogsAsync()
        {
            try
            {
                string currentSessionId = CommandManager.Instance.SessionId;
                var recentThreshold = DateTime.UtcNow.AddSeconds(-2); // Last 2 seconds only
                
                const string cypher = @"
                    MATCH (c:ChangeLog)
                    WHERE c.sessionId <> $currentSessionId 
                      AND c.acknowledged = false
                      AND c.timestamp > datetime($recentThreshold)
                    RETURN DISTINCT c.sessionId as sourceSessionId, count(c) as changeCount";

                await using var session = _driver.AsyncSession();
                var result = await session.RunAsync(cypher, new { 
                    currentSessionId,
                    recentThreshold = recentThreshold.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                });
                
                await foreach (var record in result)
                {
                    var sourceSessionId = record["sourceSessionId"]?.As<string>();
                    var changeCount = record["changeCount"]?.As<long>() ?? 0;
                    
                    if (!string.IsNullOrEmpty(sourceSessionId) && changeCount > 0)
                    {
                        Logger.LogToFile($"NEO4J CHANGE LISTENER: Detected {changeCount} recent changes from session {sourceSessionId}, triggering notification", "sync.log");
                        OnChangeLogCreated(currentSessionId);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogToFile($"NEO4J CHANGE LISTENER: Exception checking recent ChangeLogs: {ex.Message}", "sync.log");
            }
        }

        private void OnChangeLogCreated(string targetSessionId)
        {
            try
            {
                Logger.LogToFile($"NEO4J CHANGE EVENT: Firing ChangeLogCreated event for session {targetSessionId}", "sync.log");
                ChangeLogCreated?.Invoke(targetSessionId);
            }
            catch (Exception ex)
            {
                Logger.LogToFile($"NEO4J CHANGE EVENT ERROR: Exception firing ChangeLogCreated event: {ex.Message}", "sync.log");
                Logger.LogCrash("Neo4jChangeNotifier event fire error", ex);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            Logger.LogToFile("NEO4J CHANGE NOTIFIER: Disposing change notifier", "sync.log");
            
            _cancellationTokenSource?.Cancel();
            
            try
            {
                _notificationTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Logger.LogToFile($"NEO4J CHANGE NOTIFIER: Exception waiting for task completion: {ex.Message}", "sync.log");
            }
            
            _cancellationTokenSource?.Dispose();
            
            Logger.LogToFile("NEO4J CHANGE NOTIFIER: Change notifier disposed", "sync.log");
        }
    }
}
