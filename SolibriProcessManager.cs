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
        public const int DefaultPort = 10876;
 public static int Port { get; set; }
            = int.TryParse(Environment.GetEnvironmentVariable("SOLIBRI_PORT"), out var envPort)
                ? envPort
                : DefaultPort;
         // Path to Solibri executable. Can be overridden via the SOLIBRI_EXE
        // environment variable if needed.
        public static string ExePath { get; set; }
            = Environment.GetEnvironmentVariable("SOLIBRI_EXE")
              ?? @"C:\\Program Files\\Solibri\\SOLIBRI\\Solibri.exe";
        /// <summary>
        /// Ensures the Solibri REST API is running. If it is not reachable the
        /// method will try to start Solibri with the required parameters and
        /// then wait until the API responds to /ping.
        /// </summary>
        public static void EnsureStarted()
        {
            if (IsApiReachable())
            {
                Logger.LogToFile($"Solibri REST API already running on port {Port}");
                return;
            }

            StartSolibri();
                        WaitForApi();

        }
        // Wartet maximal einige Sekunden bis die REST-API erreichbar ist.
        private static void StartSolibri()
        {
            try
            {
                if (!File.Exists(ExePath))

                {
                    Logger.LogToFile($"Solibri executable not found at '{ExePath}'");
                    return;
                }

                var args = $"--rest-api-server-port={Port} --rest-api-server-http --rest-api-server-local-content";
                Process.Start(new ProcessStartInfo(ExePath, args) { UseShellExecute = true });
                Logger.LogToFile($"Starting Solibri with arguments '{args}'");
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Start Solibri", ex);
            }
        }

        // Checks whether the REST API responds to /ping.
        private static bool IsApiReachable()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try
            {
                var resp = http.GetAsync($"http://localhost:{Port}/solibri/v1/ping").Result;
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        // Waits up to ~30 seconds for the REST API to become reachable.
        private static void WaitForApi()
        {
            for (int i = 0; i < 30; i++)
            {
                if (IsApiReachable())
                {
                    Logger.LogToFile($"Solibri REST API reachable on port {Port}");
                    return;
                }
                Thread.Sleep(1000);
            }

            var err = "Solibri REST API not reachable. Please start Solibri.";
            Logger.LogToFile(err);
            throw new Exception(err);
        }
    }
}