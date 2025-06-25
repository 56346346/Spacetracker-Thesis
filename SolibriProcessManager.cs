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
                public static int Port { get; set; } = DefaultPort;

        // Startet Solibri falls es noch nicht läuft und wartet auf die REST-API.
        public static void EnsureStarted()
        {
            // Only verify that the REST API is reachable. The user is responsible
            // for starting Solibri externally.
            WaitForApi();
        }

        private static void WaitForApi()
        {
            using var http = new HttpClient();
            for (int i = 0; i < 30; i++)
            {
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

            var err = "Solibri REST API not reachable. Please start Solibri.";
            Logger.LogToFile(err);
            throw new Exception(err);
        }
    }
}