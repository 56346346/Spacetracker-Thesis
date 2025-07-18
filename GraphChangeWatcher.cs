using System;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;

namespace SpaceTracker
{
    /// <summary>
    /// Monitors Neo4j for new change logs and triggers an automatic pull
    /// when remote modifications are detected.
    /// </summary>
    public class ChangeMonitor : IDisposable
    {
        private readonly Neo4jConnector _connector;
        private readonly GraphPuller _puller;
        private CancellationTokenSource _cts;
        private Task _watchTask;
        private string _sessionId;
        private Document _document;

        public ChangeMonitor(Neo4jConnector connector, GraphPuller puller)
        {
            _connector = connector;
            _puller = puller;
        }

        /// <summary>
        /// Starts monitoring for remote changes.
        /// </summary>
        public void Start(Document doc, string sessionId)
        {
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
            _document = doc;
        }

        private async Task WatchLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_document != null)
                    {
                        var logs = await _connector.GetPendingChangeLogsAsync(_sessionId).ConfigureAwait(false);
                        if (logs.Count > 0)
                            _puller.RequestPull(_document, Environment.UserName);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("ChangeMonitor", ex);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // ignore
                }
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
