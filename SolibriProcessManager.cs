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
          public static int Port { get; set; } = Port;
        // Optional path to the Solibri executable. If set, the process will be
        // started automatically when the REST API cannot be reached.
        public static string? SolibriExePath { get; set; } =
            Environment.GetEnvironmentVariable("SOLIBRI_EXE");

        /// <summary>
        /// Ensures that the Solibri REST API is available. If the API is not
        /// reachable and <see cref="SolibriExePath"/> is specified, the process
        /// will be started automatically.
        /// </summary>
        public static void EnsureStarted()
        {
            if (WaitForApi())
                return;

            if (!string.IsNullOrEmpty(SolibriExePath) && File.Exists(SolibriExePath))
            {
                try
                {
                    Process.Start(SolibriExePath);
                    Thread.Sleep(5000); // give the process some time to start
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("Start Solibri", ex);
                }
                if (WaitForApi())
                    return;
            }

            var err = "Solibri REST API not reachable. Please start Solibri.";
            Logger.LogToFile(err);
            throw new Exception(err);
        }
        // Wartet maximal einige Sekunden bis die REST-API erreichbar ist.
        private static bool WaitForApi()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    var resp = http.GetAsync($"http://localhost:{Port}/solibri/v1/status").Result;
                    if (resp.IsSuccessStatusCode)
                    {
                        Logger.LogToFile($"Solibri REST API reachable on port {Port}");
                        return true;
                    }
                }
                catch
                {
                    // ignore and retry
                }
                Thread.Sleep(1000);
            }

            var err = "Solibri REST API not reachable. Please start Solibri.";
            Logger.LogToFile(err);
                        return false;

        }
    }
}