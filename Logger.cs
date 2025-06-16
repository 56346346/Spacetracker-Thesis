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

         static Logger()
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);
        }

        // Stellt sicher, dass das Verzeichnis existiert
        private static void EnsureLogDir()
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);
        }

        // Protokolliert eine Crash-Exception
        public static void LogCrash(string label, Exception ex)
        {
            // crash.log
            var path = Path.Combine(LogDir, "crash.log");
            File.AppendAllText(path, $"{DateTime.Now:O} [{label}] {ex}\n");
        }

        // Protokolliert eine beliebige Meldung in eine Datei
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