using System;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using System.IO;
using static System.Environment;



namespace SpaceTracker
{
    /// <summary>
    /// Monitors Neo4j for new change logs and triggers an automatic pull
    /// when remote modifications are detected.
    /// </summary>
    public class ChangeMonitor : IDisposable
    {
        private static readonly string _logDir =
                   Path.Combine(GetFolderPath(Environment.SpecialFolder.ApplicationData),
                   "SpaceTracker", "log");
        private static readonly string logPath =
            Path.Combine(_logDir, nameof(ChangeMonitor) + ".log");
        static ChangeMonitor()
        {
             if (!Directory.Exists(_logDir))
                Directory.CreateDirectory(_logDir);
            MethodLogger.InitializeLog(nameof(ChangeMonitor));
        }

        private static void LogMethodCall(string methodName, Dictionary<string, object?> parameters)
        {
            MethodLogger.Log(nameof(ChangeMonitor), methodName, parameters);
        }
        private readonly Neo4jConnector _connector;
        private readonly PullEventHandler _pullEventHandler;
        private CancellationTokenSource _cts;
        private Task _watchTask;
        private string _sessionId;
        private Document _document;

        public ChangeMonitor(Neo4jConnector connector, PullEventHandler pullEventHandler)
        {
            _connector = connector;
            _pullEventHandler = pullEventHandler;
        }

        /// <summary>
        /// Starts monitoring for remote changes.
        /// </summary>
        public void Start(Document doc, string sessionId)
        {
            LogMethodCall(nameof(Start), new()
            {
                ["doc"] = doc?.Title,
                ["sessionId"] = sessionId
            });
            _document = doc;
            _sessionId = sessionId;
            if (_watchTask != null && !_watchTask.IsCompleted)
                return;
            _cts = new CancellationTokenSource();
            _watchTask = Task.Run(() => WatchLoopAsync(_cts.Token));
        }

        /// <summary>
        /// Updates the currently active document.
        /// </summary>
        public void UpdateDocument(Document doc)
        {
            LogMethodCall(nameof(UpdateDocument), new() { ["doc"] = doc?.Title });

            _document = doc;
        }

        private async Task WatchLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var doc = _document ?? SessionManager.GetDocumentForSession(_sessionId);
                    if (doc != null)
                    {
                        var logs = await _connector.GetPendingChangeLogsAsync(
                            _sessionId,
                            CommandManager.Instance.LastSyncTime).ConfigureAwait(false);

                        if (logs.Count > 0 && !doc.IsModifiable && !doc.IsReadOnly)
                        {
                            _pullEventHandler.RequestPull(doc);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("ChangeMonitor", ex);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // ignore
                }
            }
        }

        public void Stop()
        {
            LogMethodCall(nameof(Stop), new());

            _cts?.Cancel();
        }

        public void Dispose()
        {
            LogMethodCall(nameof(Dispose), new());

            Stop();
        }
    }
}
