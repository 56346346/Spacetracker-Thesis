using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.IO;
using SpaceTracker;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;



namespace SpaceTracker
{
    public static class SolibriProcessManager
    {
        public const int Port = 10876;
        // Prüft lediglich, ob Solibri bereits läuft. Die Applikation startet
        // Solibri bewusst nicht selbst, da ein manueller Start empfohlen ist.
        // Öffnen Sie Solibri also vor der Validierung und lassen Sie es im
        // Hintergrund geöffnet.

        public static bool EnsureStarted()
        {
            if (IsApiReachable())
                return true;

            Logger.LogToFile($"Solibri REST API not reachable on port {Port}. Versuche Solibri zu starten...");
           

            if (WaitForApi())
                return true;

            TaskDialog.Show(
                "Solibri",
                $"Die Solibri REST API konnte nicht erreicht werden. Bitte starten Sie Solibri manuell mit aktivierter REST API auf Port {Port}.");
            return false;
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
            }

            Logger.LogToFile("Solibri REST API not reachable after waiting.");
            return false;
        }
    }
}