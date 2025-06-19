using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace SpaceTracker
{
    public static class ChangeCacheHelper
    {
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpaceTracker",
            "cache");

        public static string WriteChange(string cypherCommand)
        {
            Directory.CreateDirectory(CacheDir);
            long elementId = ExtractElementId(cypherCommand);
            var payload = new
            {
                cypher = cypherCommand,
                elementId,
                timestampUtc = DateTime.UtcNow
            };
            string file = Path.Combine(CacheDir, $"change_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{elementId}.json");
            File.WriteAllText(file, JsonConvert.SerializeObject(payload));
            return file;
        }

        private static long ExtractElementId(string cmd)
        {
            var match = Regex.Match(cmd, @"ElementId\D+(\d+)");
            return match.Success && long.TryParse(match.Groups[1].Value, out var id) ? id : -1;
        }
    }
}