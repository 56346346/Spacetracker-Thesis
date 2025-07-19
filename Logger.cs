using System;
using System.IO;
using Serilog;

namespace SpaceTracker
{
    internal static class Logger
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpaceTracker",
            "log"
        );
        // Legt das Log-Verzeichnis an und initialisiert den Serilog-Logger
        static Logger()
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);
                
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(Path.Combine(LogDir, "main.log"), shared: true)
                .CreateLogger();
        }

        // Interne Helferfunktion, stellt das Log-Verzeichnis sicher.
        private static void EnsureLogDir()
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);
        }
         // Hilfsmethode zum Schreiben mit gemeinsamem Dateizugriff
        private static void AppendLine(string path, string message)
        {
            using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            writer.WriteLine(message);
        }


        // Protokolliert eine Crash-Exception
        // Schreibt einen Absturz oder eine Ausnahme in die crash.log.

        public static void LogCrash(string label, Exception ex)
        {
            
            var path = Path.Combine(LogDir, "crash.log");
            AppendLine(path, $"{DateTime.Now:O} [{label}] {ex}");
        }

        // Protokolliert eine beliebige Meldung in eine Datei
                // Fügt eine beliebige Textzeile an die angegebene Logdatei an.

        public static void LogToFile(string message, string fileName = "main.log")
        {
            // Stelle sicher, dass fileName mit .log endet
            if (!fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                fileName += ".log";

            var path = Path.Combine(LogDir, fileName);
            AppendLine(path, $"{DateTime.Now:O} {message}");
        }
    }
}