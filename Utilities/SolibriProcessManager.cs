using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;

namespace SpaceTracker.Utilities
{
    public static class SolibriProcessManager
    {
        private static Process _process;
        public const int DefaultPort = 10876;
        private const string SolibriExePath = @"C:\\Program Files\\Solibri\\SOLIBRI\\Solibri.exe";

        public static int Port { get; set; } = DefaultPort;

        public static void EnsureStarted()
        {
            if (_process != null && !_process.HasExited)
            {
                return;
            }

            var startInfo = new ProcessStartInfo(SolibriExePath, $"--rest-api-server-port={Port}")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _process = Process.Start(startInfo);
            WaitForApi();
        }

        private static void WaitForApi()
        {
            using var http = new HttpClient();
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    var resp = http.GetAsync($"http://localhost:{Port}/status").Result;
                    if (resp.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch
                {
                    // ignore and retry
                }
                Thread.Sleep(1000);
            }
            throw new Exception("Solibri REST API nicht erreichbar!");
        }

        public static void Stop()
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
                _process = null;
            }
        }
    }
}