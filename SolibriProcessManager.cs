using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.IO;
using SpaceTracker;



namespace SpaceTracker
{
    public static class SolibriProcessManager
    {
        private static Process _process;
        public const int DefaultPort = 10876;
        private static readonly string SolibriExePath =
                 Environment.GetEnvironmentVariable("SOLIBRI_EXE_PATH") ??
                 @"C:\\Program Files\\Solibri\\SOLIBRI\\Solibri.exe";
        public static int Port { get; set; } = DefaultPort;

        public static void EnsureStarted()
        {
            if (_process != null && !_process.HasExited)
            {
                Logger.LogToFile("Solibri process already running");

                return;
            }
            if (!File.Exists(SolibriExePath))

            {
                var msg = $"Solibri.exe not found at '{SolibriExePath}'";
                Logger.LogCrash("Solibri start", new FileNotFoundException(msg));
                throw new FileNotFoundException(msg, SolibriExePath);
            }

            try
            {
                Logger.LogToFile("Starting Solibri process");
                var startInfo = new ProcessStartInfo(SolibriExePath, $"--rest-api-server-port={Port} --rest-api-server-http --rest-api-server-local-content")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _process = Process.Start(startInfo);
                if (_process == null)
                    throw new InvalidOperationException("Process.Start returned null");

                WaitForApi();
                Logger.LogToFile("Solibri process started and REST API reachable");
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri start", ex);
                throw new Exception("Could not start Solibri process. See log for details.", ex);
            }
        }

        private static void WaitForApi()
        {
            using var http = new HttpClient();
            for (int i = 0; i < 30; i++)
            {
                if (_process != null && _process.HasExited)
                {
                    var msg = $"Solibri process exited with code {_process.ExitCode} while waiting for REST API";
                    Logger.LogToFile(msg);
                    throw new Exception(msg);
                }

                try
                {
                    var resp = http.GetAsync($"http://localhost:{Port}/solibri/v1/status").Result;
                    if (resp.IsSuccessStatusCode)
                    {
                        Logger.LogToFile($"Solibri REST API reachable on port {Port}");

                        return;
                    }
                }
                catch
                {
                    // ignore and retry
                }
                Thread.Sleep(1000);
            }

            var err = "Solibri REST API not reachable";
            Logger.LogToFile(err);
            throw new Exception(err);
        }

        public static void Stop()
        {
            if (_process != null && !_process.HasExited)
            {
                Logger.LogToFile("Stopping Solibri process");

                _process.Kill();
                Logger.LogToFile($"Solibri process exited with code {_process.ExitCode}");

                _process = null;
            }
        }
    }
}