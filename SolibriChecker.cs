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

        private SolibriChecker(IHttpClientFactory factory, INeo4jConnector connector)
        {
            _client = factory.CreateClient("solibri");
            if (_client.BaseAddress == null)
                _client.BaseAddress = new Uri("http://localhost:10876/solibri/v1/");
            _connector = connector;
        }

        public async Task EnsureSolibriReadyAsync(CancellationToken ct)
        {
            await _client.GetStringAsync("/ping", ct).ConfigureAwait(false);
            while ((await _client.GetFromJsonAsync<Status>("/status", ct).ConfigureAwait(false))?.Busy == true)
                await Task.Delay(500, ct).ConfigureAwait(false);
        }

        public async Task<string> UploadIfcAsync(Stream ifcStream, string name, bool partial, CancellationToken ct)
        {
            var url = partial ? $"/models/{_modelUuid}/partialUpdate" : $"/models?name={name}";
            using var content = new StreamContent(ifcStream);
            var resp = partial
                ? await _client.PutAsync(url, content, ct).ConfigureAwait(false)
                : await _client.PostAsync(url, content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            if (!partial)
            {
                var model = await resp.Content.ReadFromJsonAsync<ModelInfo>(cancellationToken: ct).ConfigureAwait(false);
                _modelUuid = model?.Uuid;
            }
            return _modelUuid!;
        }

        public async Task DeleteComponentsAsync(string modelUuid, IEnumerable<string> guids, CancellationToken ct)
        {
            await _client.PostAsJsonAsync($"/models/{modelUuid}/deleteComponents", guids, ct).ConfigureAwait(false);
        }

        public async Task<string> GetBcfAsync(string modelUuid, string version = "two", string scope = "all", CancellationToken ct = default)
        {
            var url = $"/bcfxml/{version}?scope={scope}";
            return await _client.GetStringAsync(url, ct).ConfigureAwait(false);
        }

        public async Task ValidateChangesAsync(Stream ifcStream, IEnumerable<string> removedGuids, string name, CancellationToken ct)
        {
            await EnsureSolibriReadyAsync(ct).ConfigureAwait(false);
            string id = await UploadIfcAsync(ifcStream, name, _modelUuid != null, ct).ConfigureAwait(false);
            if (removedGuids.Any())
                await DeleteComponentsAsync(id, removedGuids, ct).ConfigureAwait(false);
            string bcf = await GetBcfAsync(id, ct: ct).ConfigureAwait(false);
            await UpdateLogStatusAsync(bcf, ct).ConfigureAwait(false);
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
            await _client.PostAsJsonAsync("/selectionBasket", guids, ct).ConfigureAwait(false);
        }

        public async Task<string> GetInfoAsync(string guid, CancellationToken ct)
        {
            return await _client.GetStringAsync($"/info/{guid}", ct).ConfigureAwait(false);
        }
    }
}