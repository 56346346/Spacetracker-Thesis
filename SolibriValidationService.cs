using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Net;
using System.Text.Json;

namespace SpaceTracker
{
    /// <summary>
    /// Complete Solibri integration service handling validation and REST API communication
    /// Consolidates functionality from SolibriValidationService and SolibriApiClient
    /// </summary>
    public class SolibriValidationService
    {
        private readonly string _tempDirectory;
        private readonly string _baseUrl;
        private string _currentModelId = string.Empty;
        private bool _isModelLoaded = false;
        
        private static readonly HttpClient Http = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:10876/solibri/v1/"),
            Timeout = TimeSpan.FromMinutes(5)
        };

        // Timing configuration for ChangeLog synchronization
        public static readonly TimeSpan ChangeLogProcessingDelay = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan ValidationTimeout = TimeSpan.FromMinutes(5);

        public SolibriValidationService(int port = 10876)
        {
            _baseUrl = $"http://localhost:{port}/solibri/v1/";
            if (Http.BaseAddress?.ToString() != _baseUrl)
            {
                Http.BaseAddress = new Uri(_baseUrl);
            }
            _tempDirectory = Path.Combine(Path.GetTempPath(), "SpaceTracker", "Solibri");
            Directory.CreateDirectory(_tempDirectory);
        }

        /// <summary>
        /// Validates elements from ChangeLog entries against Solibri ruleset
        /// </summary>
        public async Task<bool> ValidateChangeLogElementsAsync(Document doc, List<ElementId> changedElementIds, List<ElementId> deletedElementIds)
        {
            try
            {
                Logger.LogToFile($"SOLIBRI VALIDATION: Starting validation for {changedElementIds.Count} changed + {deletedElementIds.Count} deleted elements", "solibri.log");

                // Check if Solibri is running
                if (!await PingAsync())
                {
                    Logger.LogToFile("SOLIBRI VALIDATION: Solibri REST API not reachable", "solibri.log");
                    Autodesk.Revit.UI.TaskDialog.Show("Solibri Validation", "Solibri REST API ist nicht erreichbar. Bitte starten Sie Solibri mit --rest-api-server-port=10876 --rest-api-server-http");
                    return false;
                }

                // Export changed elements to IFC
                string ifcPath = await ExportElementsToIfcAsync(doc, changedElementIds);
                if (string.IsNullOrEmpty(ifcPath))
                {
                    Logger.LogToFile("SOLIBRI VALIDATION: IFC export failed", "solibri.log");
                    return false;
                }

                // Upload to Solibri (new model or partial update)
                string modelId = await UploadIfcAsync(ifcPath, _isModelLoaded);
                _isModelLoaded = true;

                // Delete components if needed
                if (deletedElementIds.Any())
                {
                    var deletedGuids = GetIfcGuidsForElements(doc, deletedElementIds);
                    if (deletedGuids.Any())
                    {
                        await DeleteComponentsAsync(deletedGuids);
                    }
                }

                // Start validation
                await StartCheckingAsync();

                // Wait for completion
                bool completed = await WaitForCheckingCompleteAsync(TimeSpan.FromMinutes(5));
                if (!completed)
                {
                    Logger.LogToFile("SOLIBRI VALIDATION: Validation timeout", "solibri.log");
                    Autodesk.Revit.UI.TaskDialog.Show("Solibri Validation", "Validierung hat das Zeitlimit überschritten");
                    return false;
                }

                // Export BCF results
                string bcfPath = await ExportBcfXmlAsync(_tempDirectory);

                // Parse BCF and display in Revit
                var issues = ParseBcfXml(bcfPath);
                await DisplayValidationResultsInRevitAsync(doc, issues);

                Logger.LogToFile($"SOLIBRI VALIDATION: Completed successfully with {issues.Count} issues", "solibri.log");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Validation", ex);
                Autodesk.Revit.UI.TaskDialog.Show("Solibri Validation Error", $"Fehler bei der Validierung: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Exports specified elements to IFC file
        /// </summary>
        private async Task<string> ExportElementsToIfcAsync(Document doc, List<ElementId> elementIds)
        {
            try
            {
                if (!elementIds.Any())
                {
                    // Export entire model if no specific elements
                    elementIds = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType()
                        .ToElementIds()
                        .ToList();
                }

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var ifcPath = Path.Combine(_tempDirectory, $"validation_{timestamp}.ifc");

                // Use existing IFC export functionality
                SpaceTrackerClass.RequestIfcExport(doc, elementIds);
                
                // Wait a moment for export to complete
                await Task.Delay(1000);
                
                var exportedPath = SpaceTrackerClass.ExportHandler?.ExportedPath;
                if (!string.IsNullOrEmpty(exportedPath) && File.Exists(exportedPath))
                {
                    // Copy to our temp directory
                    File.Copy(exportedPath, ifcPath, true);
                    return ifcPath;
                }

                Logger.LogToFile("SOLIBRI VALIDATION: IFC export failed - no exported file found", "solibri.log");
                return null;
            }
            catch (Exception ex)
            {
                Logger.LogCrash("IFC Export for Solibri", ex);
                return null;
            }
        }

        /// <summary>
        /// Extracts IFC GUIDs for deleted elements (if available)
        /// </summary>
        private List<string> GetIfcGuidsForElements(Document doc, List<ElementId> elementIds)
        {
            var guids = new List<string>();
            
            foreach (var id in elementIds)
            {
                try
                {
                    var element = doc.GetElement(id);
                    if (element != null)
                    {
                        // Try to get IFC GUID parameter
                        var ifcGuidParam = element.get_Parameter(BuiltInParameter.IFC_GUID);
                        if (ifcGuidParam != null && !string.IsNullOrEmpty(ifcGuidParam.AsString()))
                        {
                            guids.Add(ifcGuidParam.AsString());
                        }
                        else
                        {
                            // Fallback: use UniqueId
                            guids.Add(element.UniqueId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogToFile($"Error getting GUID for element {id}: {ex.Message}", "solibri.log");
                }
            }
            
            return guids;
        }

        /// <summary>
        /// Parses BCF-XML file and extracts validation issues
        /// </summary>
        private List<ValidationIssue> ParseBcfXml(string bcfZipPath)
        {
            var issues = new List<ValidationIssue>();
            
            try
            {
                using var archive = ZipFile.OpenRead(bcfZipPath);
                
                foreach (var entry in archive.Entries.Where(e => e.Name.Equals("markup.bcf", StringComparison.OrdinalIgnoreCase)))
                {
                    using var stream = entry.Open();
                    var xdoc = XDocument.Load(stream);

                    var issue = new ValidationIssue();
                    
                    // Extract basic information
                    issue.Title = xdoc.Descendants("Title").FirstOrDefault()?.Value ?? "Validation Issue";
                    issue.Description = xdoc.Descendants("Description").FirstOrDefault()?.Value ?? "";
                    
                    // Extract severity
                    var severityText = xdoc.Descendants("Priority").FirstOrDefault()?.Value ?? 
                                      xdoc.Descendants("Severity").FirstOrDefault()?.Value ?? "Info";
                    issue.Severity = ParseSeverity(severityText);
                    
                    // Extract component GUIDs
                    var components = xdoc.Descendants("Component")
                        .Select(c => c.Attribute("IfcGuid")?.Value)
                        .Where(guid => !string.IsNullOrEmpty(guid))
                        .ToList();
                    issue.ComponentGuids = components;
                    
                    // Extract viewpoint information if available
                    var viewpoint = xdoc.Descendants("Viewpoints").FirstOrDefault();
                    if (viewpoint != null)
                    {
                        issue.ViewpointFile = viewpoint.Attribute("Viewpoint")?.Value;
                    }
                    
                    issues.Add(issue);
                }
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Parse BCF XML", ex);
            }
            
            return issues;
        }

        /// <summary>
        /// Displays validation results as text fields in both Revit sessions
        /// </summary>
        private Task DisplayValidationResultsInRevitAsync(Document doc, List<ValidationIssue> issues)
        {
            return Task.Run(() =>
            {
                try
                {
                if (!issues.Any())
                {
                    Logger.LogToFile("SOLIBRI VALIDATION: No issues found - displaying success message", "solibri.log");
                    
                    // Create success text field
                    using (var trans = new Transaction(doc, "Solibri Validation - Success"))
                    {
                        trans.Start();
                        CreateValidationTextField(doc, "✓ Solibri Validation Passed", "No issues found in validation", ValidationSeverity.Success);
                        trans.Commit();
                    }
                    return;
                }

                Logger.LogToFile($"SOLIBRI VALIDATION: Displaying {issues.Count} issues in Revit", "solibri.log");

                using (var trans = new Transaction(doc, "Solibri Validation Results"))
                {
                    trans.Start();
                    
                    int issueNumber = 1;
                    foreach (var issue in issues.Take(10)) // Limit to first 10 issues to avoid clutter
                    {
                        var title = $"Issue {issueNumber}: {issue.Title}";
                        var description = $"{issue.Severity}: {issue.Description}";
                        
                        if (issue.ComponentGuids.Any())
                        {
                            description += $"\nAffected Components: {string.Join(", ", issue.ComponentGuids.Take(3))}";
                            if (issue.ComponentGuids.Count > 3)
                                description += $" (and {issue.ComponentGuids.Count - 3} more)";
                        }
                        
                        CreateValidationTextField(doc, title, description, issue.Severity);
                        issueNumber++;
                    }
                    
                    if (issues.Count > 10)
                    {
                        CreateValidationTextField(doc, $"Additional Issues", $"{issues.Count - 10} more issues found. Check BCF export for details.", ValidationSeverity.Warning);
                    }
                    
                    trans.Commit();
                }
                
                // Show summary dialog
                var summary = $"Solibri Validation completed with {issues.Count} issues:\n";
                var errorCount = issues.Count(i => i.Severity == ValidationSeverity.Error);
                var warningCount = issues.Count(i => i.Severity == ValidationSeverity.Warning);
                var infoCount = issues.Count(i => i.Severity == ValidationSeverity.Info);
                
                summary += $"• {errorCount} Errors\n• {warningCount} Warnings\n• {infoCount} Info";
                
                Autodesk.Revit.UI.TaskDialog.Show("Solibri Validation Results", summary);
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("Display Validation Results", ex);
                }
            });
        }

        /// <summary>
        /// Creates a text field in Revit to display validation issue
        /// </summary>
        private void CreateValidationTextField(Document doc, string title, string description, ValidationSeverity severity)
        {
            try
            {
                // Find a text note type
                var textNoteType = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .FirstOrDefault() as TextNoteType;
                
                if (textNoteType == null)
                {
                    Logger.LogToFile("SOLIBRI VALIDATION: No TextNoteType found", "solibri.log");
                    return;
                }

                // Create text content
                var content = $"{title}\n{description}";
                
                // Position text notes in a column
                var basePoint = new XYZ(0, 0, 0);
                var existingTextNotes = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .Where(tn => tn.Text.StartsWith("Issue ") || tn.Text.StartsWith("✓ Solibri") || tn.Text.StartsWith("Additional Issues"))
                    .Count();
                
                var offset = new XYZ(0, -existingTextNotes * 10, 0); // 10 feet between text notes
                var position = basePoint + offset;
                
                // Create text note
                var textNote = TextNote.Create(doc, doc.ActiveView.Id, position, content, textNoteType.Id);
                
                Logger.LogToFile($"SOLIBRI VALIDATION: Created text field '{title}' at position {position}", "solibri.log");
            }
            catch (Exception ex)
            {
                Logger.LogToFile($"Error creating text field: {ex.Message}", "solibri.log");
            }
        }

        private ValidationSeverity ParseSeverity(string severityText)
        {
            if (string.IsNullOrEmpty(severityText))
                return ValidationSeverity.Info;
                
            severityText = severityText.ToUpperInvariant();
            
            if (severityText.Contains("ERROR") || severityText.Contains("CRITICAL") || severityText.Contains("HIGH"))
                return ValidationSeverity.Error;
            else if (severityText.Contains("WARNING") || severityText.Contains("MEDIUM") || severityText.Contains("WARN"))
                return ValidationSeverity.Warning;
            else
                return ValidationSeverity.Info;
        }

        #region Solibri REST API Methods (consolidated from SolibriApiClient)

        /// <summary>
        /// Tests if Solibri REST API is reachable
        /// </summary>
        public async Task<bool> PingAsync()
        {
            try
            {
                var response = await Http.GetAsync("ping");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Uploads IFC file as new model or partial update
        /// </summary>
        public async Task<string> UploadIfcAsync(string ifcFilePath, bool isPartialUpdate = false)
        {
            if (string.IsNullOrWhiteSpace(ifcFilePath) || !File.Exists(ifcFilePath))
                throw new ArgumentException("IFC file path is invalid", nameof(ifcFilePath));

            Logger.LogToFile($"SOLIBRI UPLOAD: {(isPartialUpdate ? "Partial update" : "New model")} - {ifcFilePath}", "solibri.log");

            using var fs = File.OpenRead(ifcFilePath);
            var content = new StreamContent(fs);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            HttpResponseMessage response;
            
            if (isPartialUpdate && !string.IsNullOrEmpty(_currentModelId))
            {
                // Partial update for existing model
                response = await Http.PutAsync($"models/{_currentModelId}/partialUpdate", content);
                
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Model doesn't exist anymore, create new one
                    Logger.LogToFile($"SOLIBRI UPLOAD: Model {_currentModelId} not found, creating new model", "solibri.log");
                    var modelName = Path.GetFileNameWithoutExtension(ifcFilePath);
                    response = await Http.PostAsync($"models?name={WebUtility.UrlEncode(modelName)}", content);
                    _currentModelId = await ExtractModelIdFromResponse(response);
                }
            }
            else
            {
                // New model
                var modelName = Path.GetFileNameWithoutExtension(ifcFilePath);
                response = await Http.PostAsync($"models?name={WebUtility.UrlEncode(modelName)}", content);
                _currentModelId = await ExtractModelIdFromResponse(response);
            }

            response.EnsureSuccessStatusCode();
            Logger.LogToFile($"SOLIBRI UPLOAD: Success - Model ID: {_currentModelId}", "solibri.log");
            return _currentModelId;
        }

        /// <summary>
        /// Starts validation/checking for the current model
        /// </summary>
        public async Task StartCheckingAsync()
        {
            Logger.LogToFile("SOLIBRI CHECKING: Starting validation", "solibri.log");
            
            var response = await Http.PostAsync("checking?checkSelected=false", null);
            response.EnsureSuccessStatusCode();
            
            Logger.LogToFile("SOLIBRI CHECKING: Validation started", "solibri.log");
        }

        /// <summary>
        /// Waits for checking to complete by polling status
        /// </summary>
        public async Task<bool> WaitForCheckingCompleteAsync(TimeSpan timeout)
        {
            Logger.LogToFile("SOLIBRI CHECKING: Waiting for completion", "solibri.log");
            
            var startTime = DateTime.Now;
            while (DateTime.Now - startTime < timeout)
            {
                var response = await Http.GetAsync("status");
                response.EnsureSuccessStatusCode();
                
                var statusJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(statusJson);
                
                if (doc.RootElement.TryGetProperty("busy", out var busyElement) && !busyElement.GetBoolean())
                {
                    Logger.LogToFile("SOLIBRI CHECKING: Validation completed", "solibri.log");
                    return true;
                }
                
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            
            Logger.LogToFile("SOLIBRI CHECKING: Timeout waiting for completion", "solibri.log");
            return false;
        }

        /// <summary>
        /// Exports BCF-XML with validation results
        /// </summary>
        public async Task<string> ExportBcfXmlAsync(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory is required", nameof(outputDirectory));

            if (!Directory.Exists(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            Logger.LogToFile("SOLIBRI BCF: Exporting BCF-XML", "solibri.log");
            
            var response = await Http.GetAsync("bcfxml/two_one?scope=all");
            response.EnsureSuccessStatusCode();

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"solibri_validation_{timestamp}.bcfzip";
            var filePath = Path.Combine(outputDirectory, fileName);

            using (var fs = File.Create(filePath))
            {
                await response.Content.CopyToAsync(fs);
            }

            Logger.LogToFile($"SOLIBRI BCF: Exported to {filePath}", "solibri.log");
            return filePath;
        }

        /// <summary>
        /// Deletes components from the model (for deleted elements)
        /// </summary>
        public async Task DeleteComponentsAsync(IEnumerable<string> ifcGuids)
        {
            if (string.IsNullOrEmpty(_currentModelId))
                throw new InvalidOperationException("No model loaded");

            var guidList = ifcGuids.ToList();
            if (!guidList.Any())
                return;

            Logger.LogToFile($"SOLIBRI DELETE: Deleting {guidList.Count} components", "solibri.log");
            
            var json = JsonSerializer.Serialize(guidList);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await Http.PostAsync($"models/{_currentModelId}/deleteComponents", content);
            response.EnsureSuccessStatusCode();
            
            Logger.LogToFile("SOLIBRI DELETE: Components deleted", "solibri.log");
        }

        private async Task<string> ExtractModelIdFromResponse(HttpResponseMessage response)
        {
            // Try to get model ID from response body first
            try
            {
                var responseJson = await response.Content.ReadAsStringAsync();
                var modelInfo = JsonSerializer.Deserialize<ModelInfo>(responseJson);
                if (!string.IsNullOrEmpty(modelInfo?.Uuid))
                    return modelInfo.Uuid;
            }
            catch
            {
                // Ignore JSON parsing errors
            }

            // Fallback: extract from Location header
            var locationHeader = response.Headers.Location?.ToString();
            if (!string.IsNullOrEmpty(locationHeader))
            {
                var parts = locationHeader.Split('/');
                return parts[^1];
            }

            throw new Exception("Could not extract model ID from response");
        }

        public class ModelInfo
        {
            public string Uuid { get; set; } = string.Empty;
        }

        #endregion
    }

    public enum ValidationSeverity
    {
        Success,
        Info,
        Warning,
        Error
    }

    public class ValidationIssue
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public ValidationSeverity Severity { get; set; } = ValidationSeverity.Info;
        public List<string> ComponentGuids { get; set; } = new List<string>();
        public string ViewpointFile { get; set; } = "";
    }
}
