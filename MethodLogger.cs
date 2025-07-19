using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using static System.Environment;


namespace SpaceTracker
{
    internal static class MethodLogger
    {
        private static readonly object _lock = new();
  private static readonly string BaseDir = Path.Combine(
            GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpaceTracker",
            "log");
        public static void InitializeLog(string className)
        {
            EnsureDir();
            var path = Path.Combine(BaseDir, $"{className}.log");
            File.WriteAllText(path, string.Empty);
        }

        public static void Log(string className, string methodName, Dictionary<string, object?> parameters)
        {
            EnsureDir();
            var path = Path.Combine(BaseDir, $"{className}.log");
            var paramStr = string.Join(", ", parameters.Select(kv => $"{kv.Key}={Serialize(kv.Value)}"));
            var line = $"{className}.{methodName}({paramStr})";
            lock (_lock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }

        private static string Serialize(object? value)
        {
            if (value == null) return "null";
            try
            {
                return JsonConvert.SerializeObject(value);
            }
            catch
            {
                return value.ToString() ?? "";
            }
        }

        private static void EnsureDir()
        {
            if (!Directory.Exists(BaseDir))
                Directory.CreateDirectory(BaseDir);
        }
    }
}