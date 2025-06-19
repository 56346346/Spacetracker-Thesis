using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace SpaceTracker.Utilities
{
    public class SolibriApiClient
    {
        private readonly string _baseUrl;
        private static readonly HttpClient Http = new HttpClient();


        public SolibriApiClient(int port)
        {
            _baseUrl = $"http://localhost:{port}";
            Http.Timeout = TimeSpan.FromMinutes(5);
        }

        public async Task<string> ImportIfcAsync(string ifcFilePath)
        {
            if (string.IsNullOrWhiteSpace(ifcFilePath))
                throw new ArgumentException("Pfad zur IFC-Datei darf nicht leer sein.", nameof(ifcFilePath));

            try
            {

                using var fs = File.OpenRead(ifcFilePath);
                var content = new StreamContent(fs);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var response = await Http.PostAsync($"{_baseUrl}/models", content); response.EnsureSuccessStatusCode();

                if (response.Headers.Location == null)
                    throw new Exception("Model-URI fehlt!");

                var modelUri = response.Headers.Location.ToString();
                if (string.IsNullOrEmpty(modelUri))
                    throw new Exception("Model-URI fehlt!");

                var parts = modelUri.Split('/');
                var modelId = parts[parts.Length - 1];
                return modelId;
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"Fehler beim Importieren des IFC-Modells: {ex.Message}", ex);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<string> ImportRulesetAsync(string csetFilePath)
        {
            if (string.IsNullOrWhiteSpace(csetFilePath))
                throw new ArgumentException("Pfad zur Ruleset-Datei darf nicht leer sein.", nameof(csetFilePath));

            try
            {
                using var fs = File.OpenRead(csetFilePath);
                var content = new StreamContent(fs);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var response = await Http.PostAsync($"{_baseUrl}/rulesets", content);
                response.EnsureSuccessStatusCode();

                if (response.Headers.Location == null)
                    throw new Exception("Ruleset-URI fehlt!");

                var rulesetUri = response.Headers.Location.ToString();
                if (string.IsNullOrEmpty(rulesetUri))
                    throw new Exception("Ruleset-URI fehlt!");

                var parts = rulesetUri.Split('/');
                var rulesetId = parts[parts.Length - 1];
                return rulesetId;
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"Fehler beim Hochladen der Ruleset-Datei: {ex.Message}", ex);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task CheckModelAsync(string modelId, string rulesetId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Modell-ID darf nicht leer sein.", nameof(modelId));
            if (string.IsNullOrWhiteSpace(rulesetId))
                throw new ArgumentException("Regelsatz-ID darf nicht leer sein.", nameof(rulesetId));

            try
            {

                var json = $"{{\"rulesetIds\":[\"{rulesetId}\"]}}";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await Http.PostAsync($"{_baseUrl}/models/{modelId}/check", content);
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"Fehler beim Ausführen der Modellprüfung: {ex.Message}", ex);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task PartialUpdateAsync(string modelId, string ifcFilePath)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Modell-ID darf nicht leer sein.", nameof(modelId));
            if (string.IsNullOrWhiteSpace(ifcFilePath))
                throw new ArgumentException("Pfad zur IFC-Datei darf nicht leer sein.", nameof(ifcFilePath));

            try
            {
                using var fs = File.OpenRead(ifcFilePath);
                var content = new StreamContent(fs);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var response = await Http.PutAsync($"{_baseUrl}/models/{modelId}/partialUpdate", content);
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"Fehler beim partiellen Update des IFC-Modells: {ex.Message}", ex);
            }
            catch (Exception)
            {
                throw;
            }
        }

        public async Task<string> ExportBcfAsync(string modelId, string outDirectory)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Modell-ID darf nicht leer sein.", nameof(modelId));
            if (string.IsNullOrWhiteSpace(outDirectory))
                throw new ArgumentException("Ausgabeverzeichnis darf nicht leer sein.", nameof(outDirectory));

            try
            {
                if (!Directory.Exists(outDirectory))
                    Directory.CreateDirectory(outDirectory);

                var response = await Http.GetAsync($"{_baseUrl}/models/{modelId}/bcfxml/two_one?scope=all");

                response.EnsureSuccessStatusCode();

                var filePath = Path.Combine(outDirectory, $"result_{modelId}.bcfzip");
                using (var fs = File.Create(filePath))
                {
                    await response.Content.CopyToAsync(fs);
                }
                return filePath;
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"Fehler beim Exportieren des BCF-Ergebnisses: {ex.Message}", ex);
            }
            catch (Exception)
            {
                throw;
            }
        }
        
        public async Task<bool> PingAsync()
        {
            try
            {
                var response = await Http.GetAsync($"{_baseUrl}/ping");
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> GetStatusAsync()
        {
            try
            {
                var response = await Http.GetAsync($"{_baseUrl}/status");
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"Fehler beim Abrufen des Serverstatus: {ex.Message}", ex);
            }
        }
    }
}