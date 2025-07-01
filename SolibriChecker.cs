using Autodesk.Revit.DB;
using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SpaceTracker
{
    public class ModelInfo

    {
        public string Uuid { get; set; } = string.Empty;
    }

    public class Status
    {
        public bool Busy { get; set; }
    }

    public class SolibriChecker
    {
        private readonly HttpClient _client;
        private readonly INeo4jConnector _connector;
        private string? _modelUuid;

        public static SolibriChecker? Instance { get; private set; }

        public static void Initialize(IHttpClientFactory factory, INeo4jConnector connector)
        {
            Instance = new SolibriChecker(factory, connector);
        }

        /// <summary>
        /// Exports the given element as IFC, runs a Solibri validation and updates
        /// related log entries in Neo4j.
        /// </summary>
        /// <param name="id">The element to check.</param>
        /// <param name="doc">The Revit document containing the element.</param>
        public static async Task CheckElementAsync(ElementId id, Document doc)
        {
            if (Instance == null)
                return;
            try
            {
                var extractor = new SpaceExtractor(CommandManager.Instance);
                string ifcPath = extractor.ExportIfcSubset(doc, new List<ElementId> { id });
                if (string.IsNullOrEmpty(ifcPath) || !File.Exists(ifcPath))
                    return;

                using var fs = File.OpenRead(ifcPath);
                await Instance.ValidateChangesAsync(fs, Enumerable.Empty<string>(), Path.GetFileName(ifcPath), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Element Check", ex);
            }
        }


        private SolibriChecker(IHttpClientFactory factory, INeo4jConnector connector)
        {
            _client = factory.CreateClient("solibri");
            if (_client.BaseAddress == null)
                _client.BaseAddress = new Uri("http://localhost:10876/solibri/v1/");
            _connector = connector;
        }

        public async Task EnsureSolibriReadyAsync(CancellationToken ct)
        {
            await SendWithRetryAsync(() => _client.GetAsync("/ping", ct), "Ping", ct).ConfigureAwait(false);
            while (true)
            {
                var resp = await SendWithRetryAsync(() => _client.GetAsync("/status", ct), "Status", ct).ConfigureAwait(false);
                var status = await resp.Content.ReadFromJsonAsync<Status>(cancellationToken: ct).ConfigureAwait(false);
                if (status?.Busy != true)
                    break;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        public async Task<string> UploadIfcAsync(Stream ifcStream, string name, bool partial, CancellationToken ct)
        {
            var url = partial ? $"/models/{_modelUuid}/partialUpdate" : $"/models?name={name}";
            using var content = new StreamContent(ifcStream);
            HttpResponseMessage resp = await SendWithRetryAsync(
             () => partial ? _client.PutAsync(url, content, ct) : _client.PostAsync(url, content, ct),
             "Upload IFC",
             ct).ConfigureAwait(false);
            if (!partial)
            {
                var model = await resp.Content.ReadFromJsonAsync<ModelInfo>(cancellationToken: ct).ConfigureAwait(false);
                _modelUuid = model?.Uuid;
            }
            return _modelUuid!;
        }

        public async Task DeleteComponentsAsync(string modelUuid, IEnumerable<string> guids, CancellationToken ct)
        {
            await SendWithRetryAsync(() => _client.PostAsJsonAsync($"/models/{modelUuid}/deleteComponents", guids, ct), "Delete Components", ct).ConfigureAwait(false);
        }

        public async Task<string> GetBcfAsync(string modelUuid, string version = "two", string scope = "all", CancellationToken ct = default)
        {
            var url = $"/bcfxml/{version}?scope={scope}";
            var resp = await SendWithRetryAsync(() => _client.GetAsync(url, ct), "Get BCF", ct).ConfigureAwait(false);
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        public async Task ValidateChangesAsync(Stream ifcStream, IEnumerable<string> removedGuids, string name, CancellationToken ct)
        {
            await EnsureSolibriReadyAsync(ct).ConfigureAwait(false);

            string id = await UploadIfcAsync(ifcStream, name, _modelUuid != null, ct).ConfigureAwait(false);
            await EnsureSolibriReadyAsync(ct).ConfigureAwait(false);
            if (removedGuids.Any())
            {
                await DeleteComponentsAsync(id, removedGuids, ct).ConfigureAwait(false);
                await EnsureSolibriReadyAsync(ct).ConfigureAwait(false);
            }
            string bcf = await GetBcfAsync(id, version: "two", scope: "all", ct: ct).ConfigureAwait(false);
            await UpdateLogStatusAsync(bcf, ct).ConfigureAwait(false);
            var api = new SolibriApiClient(_client.BaseAddress?.Port ?? SolibriProcessManager.Port);
            api.InstallRulesetLocally(SpaceTrackerClass.SolibriRulesetPath);
            await EnsureSolibriReadyAsync(ct).ConfigureAwait(false);
        }

        public async Task UpdateLogStatusAsync(string bcfXml, CancellationToken ct)
        {
            var doc = XDocument.Parse(bcfXml);
            foreach (var issue in doc.Descendants("Issue"))
            {
                string guid = issue.Attribute("guid")?.Value ?? string.Empty;
                string sevText = issue.Descendants("Severity").FirstOrDefault()?.Value ?? string.Empty;
                string status = MapSeverity(sevText);
                if (!string.IsNullOrEmpty(guid))
                {
                    await _connector.RunWriteQueryAsync(
                        "MATCH (l:LogChange {guid:$guid}) SET l.status=$status",
                        new { guid, status }).ConfigureAwait(false);
                }
            }
        }

        private static string MapSeverity(string sev)
        {
            sev = sev?.Trim().ToUpperInvariant();
            return sev switch
            {
                "ROT" or "RED" or "ERROR" or "HIGH" or "CRITICAL" => "RED",
                "GELB" or "YELLOW" or "WARNING" or "MEDIUM" => "YELLOW",
                _ => "GREEN"
            };
        }

        public async Task SelectComponentsAsync(IEnumerable<string> guids, CancellationToken ct)
        {
            await SendWithRetryAsync(() => _client.PostAsJsonAsync("/selectionBasket", guids, ct), "Select Components", ct).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(Func<Task<HttpResponseMessage>> sender, string label, CancellationToken ct, int retries = 3)
        {
            for (int attempt = 1; attempt <= retries; attempt++)
            {
                try
                {
                    var resp = await sender().ConfigureAwait(false);
                    if ((int)resp.StatusCode >= 500 && attempt < retries)
                    {
                        Logger.LogToFile($"{label} returned {resp.StatusCode}, retry {attempt}", "solibri.log");
                        await Task.Delay(1000 * attempt, ct).ConfigureAwait(false);
                        continue;
                    }
                    resp.EnsureSuccessStatusCode();
                    return resp;
                }
                catch (HttpRequestException ex) when (attempt < retries)
                {
                    Logger.LogToFile($"HTTP error on {label}: {ex.Message}, retry {attempt}", "solibri.log");
                    await Task.Delay(1000 * attempt, ct).ConfigureAwait(false);
                }
            }
            throw new Exception($"{label} failed after {retries} attempts");
        }


        public async Task<string> GetInfoAsync(string guid, CancellationToken ct)
        {
            var resp = await SendWithRetryAsync(() => _client.GetAsync($"/info/{guid}", ct), "Get Info", ct).ConfigureAwait(false);
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
    }
}