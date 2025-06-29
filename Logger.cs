using System;
using System.IO;
using SpaceTracker;

namespace SpaceTracker
{
    internal static class Logger
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpaceTracker"
        );
// Legt das Log-Verzeichnis an, falls es noch nicht existiert
        static Logger()
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);
        }

        // Interne Helferfunktion, stellt das Log-Verzeichnis sicher.
        private static void EnsureLogDir()
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);
        }

        // Protokolliert eine Crash-Exception
                // Schreibt einen Absturz oder eine Ausnahme in die crash.log.

        public static void LogCrash(string label, Exception ex)
        {
            // crash.log
            var path = Path.Combine(LogDir, "crash.log");
            File.AppendAllText(path, $"{DateTime.Now:O} [{label}] {ex}\n");
        }

        // Protokolliert eine beliebige Meldung in eine Datei
                // Fügt eine beliebige Textzeile an die angegebene Logdatei an.

        public static void LogToFile(string message, string fileName = "main.log")
        {
            // Stelle sicher, dass fileName mit .log endet
            if (!fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                fileName += ".log";

            var path = Path.Combine(LogDir, fileName);
            File.AppendAllText(path, $"{DateTime.Now:O} {message}\n");
        }
    }
}