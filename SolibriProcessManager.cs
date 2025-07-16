using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.IO;
using SpaceTracker;



namespace SpaceTracker
{
    public static class SolibriProcessManager
    {
        public const int DefaultPort = 10876;
                public static int Port { get; set; } = DefaultPort;

                 // Prüft lediglich, ob Solibri bereits läuft. Die Applikation startet
        // Solibri bewusst nicht selbst, da ein manueller Start empfohlen ist.
        // Öffnen Sie Solibri also vor der Validierung und lassen Sie es im
        // Hintergrund geöffnet.

        public static void EnsureStarted()
        {
            // Only verify that the REST API is reachable. The user is responsible
            // for starting Solibri externally.
            WaitForApi();
        }


        /// <summary>
        /// Logs instructions for starting Solibri with the REST API and warns if
        /// the API is not reachable on the configured port.
        /// </summary>
        public static void StartSolibriWithRestApi()
        {
            var message =
                "Bitte Solibri Office mit aktivierter REST API starten:\n" +
                "\"C:\\Program Files\\Solibri\\Solibri.exe\" " +
                "--rest-api-server-port=10876 --rest-api-server-local-content " +
                "--rest-api-server-http";

            Logger.LogToFile(message);

            if (!IsApiReachable())
            {
                Logger.LogToFile(
                    "Warnung: Solibri läuft nicht oder die REST API ist nicht erreichbar.");
            }
            else
            {
                Logger.LogToFile($"Solibri REST API erreichbar. Juhu!");
            }
        }

        // Checks once whether the REST API responds on the configured port
        private static bool IsApiReachable()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            try
            {
                var resp = http.GetAsync($"http://localhost:{Port}/solibri/v1/status")
                               .Result;
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
  // Wartet maximal einige Sekunden bis die REST-API erreichbar ist.
        private static void WaitForApi()
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