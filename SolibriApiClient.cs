using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using SpaceTracker;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;



namespace SpaceTracker
{
    public class SolibriApiClient
    {
        private readonly string _baseUrl;
        // Static HttpClient instance to avoid socket exhaustion. Timeout is set
        // once in the static constructor before any requests are sent.
        private static readonly HttpClient Http;

        static SolibriApiClient()
        {
            Http = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
        }
        // Initialisiert den Client mit dem REST-Port von Solibri.
        public SolibriApiClient(int port)
        {
            // Die REST-API von Solibri hängt unter dem Pfad "/solibri/v1".
            // Ohne diesen Zusatz würden die Requests ein 404 zurückliefern.
            _baseUrl = $"http://localhost:{port}/solibri/v1";
            if (Http.BaseAddress == null || Http.BaseAddress.ToString() != _baseUrl)
            {
                Http.BaseAddress = new Uri(_baseUrl);
            }
            Debug.WriteLine($"[SolibriApiClient] Base URL set to {_baseUrl}");
        }
        // Importiert eine IFC-Datei in Solibri und liefert die Modell-ID zurück.
        public async Task<string> ImportIfcAsync(string ifcFilePath)
        {
            if (string.IsNullOrWhiteSpace(ifcFilePath))
                throw new ArgumentException("Pfad zur IFC-Datei darf nicht leer sein.", nameof(ifcFilePath));
            SolibriProcessManager.EnsureStarted();
            if (!await PingAsync().ConfigureAwait(false))
                throw new Exception("Solibri REST API nicht erreichbar");

            try
            {
                Logger.LogToFile($"Importiere IFC '{ifcFilePath}'");
                using var fs = File.OpenRead(ifcFilePath);
                var content = new StreamContent(fs);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var response = await Http.PostAsync($"{_baseUrl}/models", content).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Headers.Location == null)
                    throw new Exception("Model-URI fehlt!");

                var modelUri = response.Headers.Location.ToString();
                if (string.IsNullOrEmpty(modelUri))
                    throw new Exception("Model-URI fehlt!");

                var parts = modelUri.Split('/');
                var modelId = parts[parts.Length - 1];
                return modelId;
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException sockEx)
            {
                Logger.LogCrash("Solibri Import IFC", ex);
                throw new Exception($"Verbindung zu Solibri fehlgeschlagen: {sockEx.Message}. Bitte prüfen Sie, ob der Dienst auf Port {SolibriProcessManager.Port} läuft.", ex);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogCrash("Solibri Import IFC", ex);
                throw new Exception($"Fehler beim Importieren des IFC-Modells: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Import IFC", ex);
                throw;
            }
        }
        // Installiert ein Ruleset lokal und gibt dessen Dateinamen zurück.
        public async Task<string> ImportRulesetAsync(string csetFilePath)
        {
            if (string.IsNullOrWhiteSpace(csetFilePath))
                throw new ArgumentException("Pfad zur Ruleset-Datei darf nicht leer sein.", nameof(csetFilePath));
            SolibriProcessManager.EnsureStarted();
            if (!await PingAsync().ConfigureAwait(false))
                throw new Exception("Solibri REST API nicht erreichbar");

            try
            {
                Logger.LogToFile($"Installiere Ruleset '{csetFilePath}' lokal");
                var installed = InstallRulesetLocally(csetFilePath);
                // Sicherstellen, dass Solibri den neuen Ruleset liest
                await GetStatusAsync().ConfigureAwait(false);
                return Path.GetFileNameWithoutExtension(installed);
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Install Ruleset", ex);
                throw;
            }
        }
        // Startet eine Prüfung in Solibri für das angegebene Modell.
        public async Task CheckModelAsync(string modelId, string rulesetId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Modell-ID darf nicht leer sein.", nameof(modelId));
            if (string.IsNullOrWhiteSpace(rulesetId))
                throw new ArgumentException("Regelsatz-ID darf nicht leer sein.", nameof(rulesetId));
            SolibriProcessManager.EnsureStarted();
            if (!await PingAsync().ConfigureAwait(false))
                throw new Exception("Solibri REST API nicht erreichbar");

            try
            {

                var json = $"{{\"rulesetIds\":[\"{rulesetId}\"]}}";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                Logger.LogToFile($"Starte Modellprüfung für {modelId}");
                var response = await Http.PostAsync($"{_baseUrl}/models/{modelId}/check", content).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException sockEx)
            {
                Logger.LogCrash("Solibri Modellprüfung", ex);
                throw new Exception($"Verbindung zu Solibri fehlgeschlagen: {sockEx.Message}. Bitte prüfen Sie, ob der Dienst auf Port {SolibriProcessManager.Port} läuft.", ex);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogCrash("Solibri Modellprüfung", ex);

                throw new Exception($"Fehler beim Ausführen der Modellprüfung: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Modellprüfung", ex);

                throw;
            }
        }
        // Aktualisiert nur einen Teil des Modells in Solibri.
        public async Task PartialUpdateAsync(string modelId, string ifcFilePath)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Modell-ID darf nicht leer sein.", nameof(modelId));
            if (string.IsNullOrWhiteSpace(ifcFilePath))
                throw new ArgumentException("Pfad zur IFC-Datei darf nicht leer sein.", nameof(ifcFilePath));
            SolibriProcessManager.EnsureStarted();
            if (!await PingAsync().ConfigureAwait(false))
                throw new Exception("Solibri REST API nicht erreichbar");

            try
            {
                Logger.LogToFile($"Partielles Update für Modell {modelId}");

                using var fs = File.OpenRead(ifcFilePath);
                var content = new StreamContent(fs);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var response = await Http.PutAsync($"{_baseUrl}/models/{modelId}/partialUpdate", content).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException sockEx)
            {
                Logger.LogCrash("Solibri Partial Update", ex);
                throw new Exception($"Verbindung zu Solibri fehlgeschlagen: {sockEx.Message}. Bitte prüfen Sie, ob der Dienst auf Port {SolibriProcessManager.Port} läuft.", ex);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogCrash("Solibri Partial Update", ex);

                throw new Exception($"Fehler beim partiellen Update des IFC-Modells: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Partial Update", ex);

                throw;
            }
        }
        // Exportiert die BCF-Ergebnisse eines Modells in ein Verzeichnis.
        public async Task<string> ExportBcfAsync(string modelId, string outDirectory)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Modell-ID darf nicht leer sein.", nameof(modelId));
            if (string.IsNullOrWhiteSpace(outDirectory))
                throw new ArgumentException("Ausgabeverzeichnis darf nicht leer sein.", nameof(outDirectory));
            SolibriProcessManager.EnsureStarted();
            if (!await PingAsync().ConfigureAwait(false))
                throw new Exception("Solibri REST API nicht erreichbar");

            try
            {
                if (!Directory.Exists(outDirectory))
                    Directory.CreateDirectory(outDirectory);

                Logger.LogToFile($"Exportiere BCF für {modelId}");
                var response = await Http.GetAsync($"{_baseUrl}/models/{modelId}/bcfxml/two_one?scope=all").ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var filePath = Path.Combine(outDirectory, $"result_{modelId}.bcfzip");
                using (var fs = File.Create(filePath))
                {
                    await response.Content.CopyToAsync(fs);
                }
                return filePath;
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException sockEx)
            {
                Logger.LogCrash("Solibri Export BCF", ex);
                throw new Exception($"Verbindung zu Solibri fehlgeschlagen: {sockEx.Message}. Bitte prüfen Sie, ob der Dienst auf Port {SolibriProcessManager.Port} läuft.", ex);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogCrash("Solibri Export BCF", ex);

                throw new Exception($"Fehler beim Exportieren des BCF-Ergebnisses: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Export BCF", ex);

                throw;
            }
        }
        // Testet, ob die REST-API erreichbar ist.
        public async Task<bool> PingAsync()
        {
            SolibriProcessManager.EnsureStarted();

            try
            {
                var response = await Http.GetAsync($"{_baseUrl}/ping").ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException sockEx)
            {
                Logger.LogCrash("Solibri Ping", ex);
                Logger.LogToFile($"Verbindung zu Solibri fehlgeschlagen: {sockEx.Message}. Bitte prüfen Sie, ob Solibri auf Port {SolibriProcessManager.Port} läuft.", "solibri.log");
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Ping", ex);
                return false;
            }
        }
        // Liefert den aktuellen Status der Solibri-Instanz als JSON.
        public async Task<string> GetStatusAsync()
        {
            SolibriProcessManager.EnsureStarted();

            try
            {
                var response = await Http.GetAsync($"{_baseUrl}/status").ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException ex)
            {
                Logger.LogCrash("Solibri Status", ex);
                throw new Exception($"Fehler beim Abrufen des Serverstatus: {ex.Message}", ex);
            }
        }

        // Pollt die REST-API bis die laufende Prüfung abgeschlossen ist.
        public async Task<bool> WaitForCheckCompletionAsync(TimeSpan pollInterval, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                await Task.Delay(pollInterval).ConfigureAwait(false);
                string json;
                try
                {
                    json = await GetStatusAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("Solibri Status Poll", ex);
                    return false;
                }

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("state", out var state))
                    {
                        var val = state.GetString();
                        if (string.Equals(val, "IDLE", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(val, "READY", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                    // ignore malformed json
                }
            }

            Logger.LogToFile("Timeout beim Warten auf das Ende der Solibri Prüfung", "solibri.log");
            return false;

        }
        
        // Kopiert die angegebene Ruleset-Datei in den lokalen Solibri-Ordner.
        public string InstallRulesetLocally(string csetFilePath)
        {
            if (string.IsNullOrWhiteSpace(csetFilePath))
                throw new ArgumentException("Pfad zur Ruleset-Datei darf nicht leer sein.", nameof(csetFilePath));

            var programData = Environment.GetEnvironmentVariable("ProgramData");
            if (string.IsNullOrEmpty(programData))
                programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(programData))
                programData = Path.GetTempPath();

            string destDir = Path.Combine(programData, "Autodesk", "Solibri", "Rulesets Open");
            Directory.CreateDirectory(destDir);
            string destPath = Path.Combine(destDir, Path.GetFileName(csetFilePath));
            File.Copy(csetFilePath, destPath, true);
            return destPath;
        }
    }
}