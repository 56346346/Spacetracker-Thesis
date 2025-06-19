using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using System.Collections.Generic;


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
                timestampUtc = DateTime.Now
            };
            string file = Path.Combine(CacheDir, $"change_{DateTime.Now:yyyyMMddHHmmssfff}_{elementId}.json");
            File.WriteAllText(file, JsonConvert.SerializeObject(payload));
            return file;
        }

        public static IEnumerable<ChangePayload> ReadChanges()
        {
            if (!Directory.Exists(CacheDir))
                yield break;
            foreach (var file in Directory.GetFiles(CacheDir, "change_*.json"))
            {
                ChangePayload payload = null;

                try
                {
                    var json = File.ReadAllText(file);
                    payload = JsonConvert.DeserializeObject<ChangePayload>(json);
                }
                catch
                {
                    // ignore malformed cache entries
                }

                if (payload != null)
                    yield return payload;
            }
        }

        public static void ClearCache()
        {
            if (!Directory.Exists(CacheDir))
                return;
            foreach (var file in Directory.GetFiles(CacheDir, "change_*.json"))
            {
                try { File.Delete(file); } catch { }
            }
        }


        private static long ExtractElementId(string cmd)
        {
            var match = Regex.Match(cmd, @"ElementId\D+(\d+)");
            return match.Success && long.TryParse(match.Groups[1].Value, out var id) ? id : -1;
        }
        
         public class ChangePayload
        {
            public string cypher { get; set; }
            public long elementId { get; set; }
            public DateTime timestampUtc { get; set; }
        }
    }
}