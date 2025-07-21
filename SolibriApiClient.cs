using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using System.Net;
using SpaceTracker;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Autodesk.Revit.UI;



namespace SpaceTracker
{
    public class SolibriApiClient
    {
        public record CheckingError(
        string Name,
        string Location,
        string Severity,
        string Description,
        string UniqueKey);

        private readonly string _baseUrl;
        // Holds the last successfully imported model ID to allow partial
        // updates across multiple calls.
        private string _currentModelId = string.Empty;
        // Shared HttpClient instance so that connections are reused across
        // requests. A default base address is configured to allow using
        // relative request URIs.
        private static readonly HttpClient Http = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:10876/solibri/v1"),
            Timeout = TimeSpan.FromMinutes(5)
        };
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
            if (!await PingAsync())
                throw new Exception("Solibri REST API nicht erreichbar");

            try
            {
                Logger.LogToFile($"Importiere IFC '{ifcFilePath}'");
                using var fs = File.OpenRead(ifcFilePath);
                var content = new StreamContent(fs);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var modelName = Path.GetFileNameWithoutExtension(ifcFilePath);
                modelName = WebUtility.UrlEncode(modelName);
                var response = await Http.PostAsync($"{_baseUrl}/models?name={modelName}", content);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogToFile($"SOLIBRI {response.StatusCode} bei {response.RequestMessage.RequestUri}", "crash.log");
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                    Logger.LogToFile("Import endpoint not found when uploading IFC model.", "solibri.log");
                response.EnsureSuccessStatusCode();
                string modelId = null;
                try
                {
                    var info = await response.Content.ReadFromJsonAsync<ModelInfo>().ConfigureAwait(false);
                    modelId = info?.Uuid;
                }
                catch
                {
                    // ignore json parse errors
                }
                if (string.IsNullOrWhiteSpace(modelId))
                {
                    var uri = response.Headers.Location?.ToString();
                    if (!string.IsNullOrWhiteSpace(uri))
                    {
                        var parts = uri.Split('/');
                        modelId = parts[^1];
                    }
                }
                if (string.IsNullOrWhiteSpace(modelId))
                    throw new Exception("Model-URI fehlt!");

                _currentModelId = modelId;

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

        /// <summary>
        /// Imports or updates the IFC model depending on whether a model ID is
        /// already known. If no ID is stored, a new model is created first and
        /// the resulting ID persisted for subsequent calls.
        /// </summary>
        public async Task<string> ImportOrUpdateAsync(string ifcFilePath)
        {
            if (string.IsNullOrWhiteSpace(ifcFilePath))
                throw new ArgumentException("Pfad zur IFC-Datei darf nicht leer sein.", nameof(ifcFilePath));

            if (string.IsNullOrEmpty(_currentModelId))
            {
                // create empty model to obtain an id
                var createResp = await Http.PostAsync($"{_baseUrl}/models", null);
                if (!createResp.IsSuccessStatusCode)
                {
                    Logger.LogToFile($"SOLIBRI {createResp.StatusCode} bei {createResp.RequestMessage.RequestUri}", "crash.log");
                }
                if (createResp.IsSuccessStatusCode)
                {
                    var uri = createResp.Headers.Location?.ToString();
                    if (!string.IsNullOrWhiteSpace(uri))
                        _currentModelId = uri.Split('/')[^1];
                    else
                    {
                        try
                        {
                            var info = await createResp.Content.ReadFromJsonAsync<ModelInfo>().ConfigureAwait(false);
                            _currentModelId = info?.Uuid ?? string.Empty;
                        }
                        catch
                        {
                        }
                    }
                }

                _currentModelId = await ImportIfcAsync(ifcFilePath).ConfigureAwait(false);
            }
            else
            {
                _currentModelId = await PartialUpdateAsync(_currentModelId, ifcFilePath).ConfigureAwait(false);
            }

            return _currentModelId;
        }
        // Installiert ein Ruleset lokal und gibt dessen Dateinamen zurück.
        public async Task<string> ImportRulesetAsync(string csetFilePath)
        {
            if (string.IsNullOrWhiteSpace(csetFilePath))
                throw new ArgumentException("Pfad zur Ruleset-Datei darf nicht leer sein.", nameof(csetFilePath));
            SolibriProcessManager.EnsureStarted();
            if (!await PingAsync())
                throw new Exception("Solibri REST API nicht erreichbar");

            try
            {
                Logger.LogToFile($"Installiere Ruleset '{csetFilePath}' lokal");
                var installed = await InstallRulesetLocally(csetFilePath).ConfigureAwait(false);

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
            if (!await PingAsync())
                throw new Exception("Solibri REST API nicht erreichbar");

            try
            {
                Logger.LogToFile($"Starte {nameof(CheckModelAsync)} für Modell {modelId}", "solibri.log");
                Logger.LogToFile($"Starte Solibri Check f\u00fcr Modell {modelId}", "solibri.log");
                using var response = await Http.PostAsync(
                      "http://localhost:10876/solibri/v1/checking?checkSelected=false",
                    null);

                if (!response.IsSuccessStatusCode) 
                {
                    string content = await response.Content.ReadAsStringAsync();
                    try
                    {
                        var errors = JsonSerializer.Deserialize<List<CheckingError>>(content);
                        if (errors != null && errors.Count > 0)
                        {
                            bool hasFields = !string.IsNullOrEmpty(errors[0].Name) &&
                                             !string.IsNullOrEmpty(errors[0].Location) &&
                                             !string.IsNullOrEmpty(errors[0].Severity) &&
                                             !string.IsNullOrEmpty(errors[0].Description) &&
                                             !string.IsNullOrEmpty(errors[0].UniqueKey);
                            if (hasFields)
                            {
                                var msg = new StringBuilder();
                                foreach (var e in errors)
                                    msg.AppendLine(e.Description);
                                Autodesk.Revit.UI.TaskDialog.Show("Solibri", msg.ToString());
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogCrash("Parse Solibri Error", ex);
                    }
                }
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogToFile($"SOLIBRI {response.StatusCode} bei {response.RequestMessage.RequestUri}", "crash.log");
                }
                response.EnsureSuccessStatusCode();
                if (response.StatusCode == HttpStatusCode.NotFound)
                    Logger.LogToFile($"Model {modelId} not found when running check.", "solibri.log");
                response.EnsureSuccessStatusCode();
                Logger.LogToFile($"Solibri Check f\u00fcr Modell {modelId} abgeschlossen", "solibri.log");
                Logger.LogToFile($"{nameof(CheckModelAsync)} erfolgreich", "solibri.log");
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
        // Aktualisiert nur einen Teil des Modells in Solibri. Ist keine Modell-
        // ID vorhanden oder liefert die REST API einen 404, wird das IFC-Modell
        // neu importiert und die neue Modell-ID zurückgegeben.
        public async Task<string> PartialUpdateAsync(string modelId, string ifcFilePath)
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                modelId = _currentModelId;
                if (string.IsNullOrWhiteSpace(modelId))
                {
                    Logger.LogToFile(
                        "Model ID empty. Importing IFC model before partial update.",
                        "solibri.log");
                    Logger.LogToFile($"Starte {nameof(PartialUpdateAsync)} für Modell {modelId}", "solibri.log");
                    var resultId = await ImportIfcAsync(ifcFilePath);
                    Logger.LogToFile($"{nameof(PartialUpdateAsync)} erfolgreich", "solibri.log");
                    return resultId;
                }
            }
            if (string.IsNullOrWhiteSpace(ifcFilePath))
                throw new ArgumentException("Pfad zur IFC-Datei darf nicht leer sein.", nameof(ifcFilePath));
            SolibriProcessManager.EnsureStarted();
            if (!await PingAsync())
                throw new Exception("Solibri REST API nicht erreichbar");

            try
            {
                Logger.LogToFile($"Starte {nameof(PartialUpdateAsync)} für Modell {modelId}", "solibri.log");

                using var fs = File.OpenRead(ifcFilePath);
                var content = new StreamContent(fs);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var response = await Http.PutAsync($"{_baseUrl}/models/{modelId}/partialUpdate", content);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogToFile($"SOLIBRI {response.StatusCode} bei {response.RequestMessage.RequestUri}", "crash.log");
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Modell existiert nicht mehr in Solibri -> neu importieren
                    Logger.LogToFile($"Model {modelId} not found. Importing new model.", "solibri.log");
                    string newId = await ImportIfcAsync(ifcFilePath).ConfigureAwait(false);
                    SpaceTrackerClass.SolibriModelUUID = newId;
                    _currentModelId = newId;
                    Logger.LogToFile($"{nameof(PartialUpdateAsync)} erfolgreich", "solibri.log");

                    return newId;
                }
                response.EnsureSuccessStatusCode();
                _currentModelId = modelId;
                Logger.LogToFile($"{nameof(PartialUpdateAsync)} erfolgreich", "solibri.log");
                return modelId;

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


        // Entfernt gelöschte Komponenten aus dem Modell in Solibri.
        public async Task DeleteComponentsAsync(string modelId, IEnumerable<string> guids)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Modell-ID darf nicht leer sein.", nameof(modelId));
            if (guids == null)
                throw new ArgumentNullException(nameof(guids));

            SolibriProcessManager.EnsureStarted();
            if (!await PingAsync())
                throw new Exception("Solibri REST API nicht erreichbar");

            try
            {
                Logger.LogToFile($"Starte {nameof(DeleteComponentsAsync)} für Modell {modelId}", "solibri.log");
                Logger.LogToFile($"Lösche {guids.Count()} Komponenten in Modell {modelId}");
                using var response = await Http.PostAsJsonAsync($"{_baseUrl}/models/{modelId}/deleteComponents", guids);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogToFile($"SOLIBRI {response.StatusCode} bei {response.RequestMessage.RequestUri}", "crash.log");
                }
                response.EnsureSuccessStatusCode();
                Logger.LogToFile($"{nameof(DeleteComponentsAsync)} erfolgreich", "solibri.log");
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException sockEx)
            {
                Logger.LogCrash("Solibri Delete Components", ex);
                throw new Exception($"Verbindung zu Solibri fehlgeschlagen: {sockEx.Message}. Bitte prüfen Sie, ob der Dienst auf Port {SolibriProcessManager.Port} läuft.", ex);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogCrash("Solibri Delete Components", ex);
                throw new Exception($"Fehler beim Löschen von Komponenten: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Delete Components", ex);
                throw;
            }
        }
        // Exportiert die BCF-Ergebnisse des aktuell aktiven Modells in ein Verzeichnis.
        // Seit Solibri 9.13 erfolgt der Export über einen globalen Endpunkt,
        // daher wird keine Modell-ID mehr benötigt.
        public async Task<string> ExportBcfAsync(string outDirectory)
        {
            if (string.IsNullOrWhiteSpace(outDirectory))
                throw new ArgumentException("Ausgabeverzeichnis darf nicht leer sein.", nameof(outDirectory));
            SolibriProcessManager.EnsureStarted();
            if (!await PingAsync().ConfigureAwait(false))
                throw new Exception("Solibri REST API nicht erreichbar");

            try
            {
                if (!Directory.Exists(outDirectory))
                    Directory.CreateDirectory(outDirectory);

                Logger.LogToFile("Exportiere BCF für aktives Modell");
                var response = await Http.GetAsync($"{_baseUrl}/bcfxml/two_one?scope=all");
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogToFile($"SOLIBRI {response.StatusCode} bei {response.RequestMessage.RequestUri}", "crash.log");
                }
                response.EnsureSuccessStatusCode();

                var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var filePath = Path.Combine(outDirectory, $"result_{timestamp}.bcfzip"); using (var fs = File.Create(filePath))
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
                var response = await Http.GetAsync($"{_baseUrl}/ping");
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogToFile($"SOLIBRI {response.StatusCode} bei {response.RequestMessage.RequestUri}", "crash.log");
                }
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
                var response = await Http.GetAsync($"{_baseUrl}/status");
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogToFile($"SOLIBRI {response.StatusCode} bei {response.RequestMessage.RequestUri}", "crash.log");
                }
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
                    // Solibri's status endpoint exposes a "busy" flag. The
                    // polling should stop as soon as Solibri is no longer busy.
                    if (doc.RootElement.TryGetProperty("busy", out var busyElem) &&
                        !busyElem.GetBoolean())
                    {
                        Logger.LogToFile("Solibri Pr\u00fcfung abgeschlossen", "solibri.log");
                        return true; // Solibri is idle/ready
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

        // Lädt das DeltaRuleset, führt die Prüfung aus und gibt die Ergebnisse zurück.
        public async Task<List<ClashResult>> RunRulesetCheckAsync(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Modell-ID darf nicht leer sein.", nameof(modelId));

            SolibriProcessManager.EnsureStarted();
            if (!await PingAsync())
                throw new Exception("Solibri REST API nicht erreichbar");

            try
            {
                Logger.LogToFile($"Starte {nameof(RunRulesetCheckAsync)} für Modell {modelId}", "solibri.log");
                Logger.LogToFile($"Hole Rulesets für Modell {modelId}", "solibri.log");
                using var rsResp = await Http.GetAsync($"{_baseUrl}/models/{modelId}/rulesets");
                if (!rsResp.IsSuccessStatusCode)
                {
                    Logger.LogToFile($"SOLIBRI {rsResp.StatusCode} bei {rsResp.RequestMessage.RequestUri}", "crash.log");
                }
                rsResp.EnsureSuccessStatusCode();
                string rsJson = await rsResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                string? deltaId = null;
                bool active = false;
                try
                {
                    using var doc = JsonDocument.Parse(rsJson);
                    if (doc.RootElement.TryGetProperty("rulesets", out var arr))
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            var name = item.GetProperty("name").GetString();
                            if (string.Equals(name, "DeltaRuleset.cset", StringComparison.OrdinalIgnoreCase))
                            {
                                deltaId = item.GetProperty("id").GetString();
                                if (item.TryGetProperty("active", out var a))
                                    active = a.GetBoolean();
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("Parse Rulesets", ex);
                }

                if (!string.IsNullOrEmpty(deltaId) && !active)
                {
                    Logger.LogToFile($"Aktiviere Ruleset {deltaId}", "solibri.log");
                    using var actResp = await Http.PostAsync($"{_baseUrl}/models/{modelId}/rulesets/{deltaId}/activate", null);
                    if (!actResp.IsSuccessStatusCode)
                    {
                        Logger.LogToFile($"SOLIBRI {actResp.StatusCode} bei {actResp.RequestMessage.RequestUri}", "crash.log");
                    }
                    actResp.EnsureSuccessStatusCode();
                }

                Logger.LogToFile($"Starte Solibri Check für Modell {modelId}", "solibri.log");
                using var checkResp = await Http.PostAsync($"{_baseUrl}/models/{modelId}/check", null);
                if (!checkResp.IsSuccessStatusCode)
                {
                    Logger.LogToFile($"SOLIBRI {checkResp.StatusCode} bei {checkResp.RequestMessage.RequestUri}", "crash.log");
                }
                checkResp.EnsureSuccessStatusCode();

                var sw = Stopwatch.StartNew();
                string? status = null;
                do
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    using var statusResp = await Http.GetAsync($"{_baseUrl}/models/{modelId}/status");
                    if (!statusResp.IsSuccessStatusCode)
                    {
                        Logger.LogToFile($"SOLIBRI {statusResp.StatusCode} bei {statusResp.RequestMessage.RequestUri}", "crash.log");
                    }
                    statusResp.EnsureSuccessStatusCode();
                    string statusJson = await statusResp.Content.ReadAsStringAsync();
                    try
                    {
                        using var doc = JsonDocument.Parse(statusJson);
                        status = doc.RootElement.GetProperty("status").GetString();
                    }
                    catch
                    {
                        // ignore parse errors and continue
                    }
                } while (!string.Equals(status, "SAVED", StringComparison.OrdinalIgnoreCase) && sw.Elapsed < TimeSpan.FromMinutes(10));

                if (!string.Equals(status, "SAVED", StringComparison.OrdinalIgnoreCase))
                    throw new TimeoutException("Solibri hat den SAVED-Status nicht erreicht.");

                var resultJson = await checkResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                List<ClashResult>? results = null;
                try
                {
                    results = JsonSerializer.Deserialize<List<ClashResult>>(resultJson);
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("Parse Check Results", ex);
                }
                Logger.LogToFile($"Solibri Check f\u00fcr Modell {modelId} abgeschlossen", "solibri.log");
                Logger.LogToFile($"{nameof(RunRulesetCheckAsync)} erfolgreich", "solibri.log");
                return results ?? new List<ClashResult>();
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException sockEx)
            {
                Logger.LogCrash("Solibri RunRulesetCheck", ex);
                throw new Exception($"Verbindung zu Solibri fehlgeschlagen: {sockEx.Message}. Bitte prüfen Sie, ob der Dienst auf Port {SolibriProcessManager.Port} läuft.", ex);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogCrash("Solibri RunRulesetCheck", ex);
                throw new Exception($"Fehler beim Ausführen der Ruleset-Prüfung: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri RunRulesetCheck", ex);
                throw;
            }
            finally
            {
                Logger.LogToFile($"Solibri Check für Modell {modelId} abgeschlossen", "solibri.log");
            }
        }

        // Kopiert die angegebene Ruleset-Datei in den lokalen Solibri-Ordner.
        public async Task<string> InstallRulesetLocally(string csetFilePath)
        {
            if (string.IsNullOrWhiteSpace(csetFilePath))
                throw new ArgumentException("Pfad zur Ruleset-Datei darf nicht leer sein.", nameof(csetFilePath));

            string destDir = Path.Combine("C:\\", "Users", "Public", "Solibri", "SOLIBRI", "Rulesets");

            Directory.CreateDirectory(destDir);
            string destPath = Path.Combine(destDir, Path.GetFileName(csetFilePath));
            File.Copy(csetFilePath, destPath, true);

            await Task.Delay(2000).ConfigureAwait(false);
            await GetStatusAsync().ConfigureAwait(false);
            return destPath;
        }
    }
}