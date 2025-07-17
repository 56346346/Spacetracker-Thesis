using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Autodesk.Revit.UI;


namespace SpaceTracker
{
    // Simple wrapper around the Solibri REST validation. Currently a stub
    // returning an empty list so the rest of the add-in can compile/run.
    public enum Severity { Info, Warning, Error }

    public static class SolibriRulesetValidator
    {
        public class ValidationError
        {
            public string Message { get; set; }
            public Severity Severity { get; set; }
        }

        // Validates the given document against the current Solibri ruleset.
        // Diese Methode muss im Revit API-Kontext ausgeführt werden (z.B. aus
        // einem ExternalCommand oder ExternalEvent). Nur dann dürfen
        // Transaktionen gestartet werden.
        // Exportiert das Modell, prüft es mit Solibri und liefert gefundene Fehler.

        public static List<ValidationError> Validate(Document doc)
        {
            var errors = new List<ValidationError>();
            try
            {
                if (doc.IsReadOnly)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Solibri", "Dokument ist schreibgesch\u00fctzt. IFC-Export nicht m\u00f6glich.");
                    return errors;
                }
                // Export the entire model as IFC to a temporary location
                var allIds = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Select(e => e.Id)
                    .ToList();
                SpaceTrackerClass.RequestIfcExport(doc, allIds);
                string ifcPath = SpaceTrackerClass.ExportHandler.ExportedPath;
                var client = new SolibriApiClient(SpaceTrackerClass.SolibriApiPort);
                string modelId = SpaceTrackerClass.SolibriModelUUID;

                if (string.IsNullOrEmpty(SpaceTrackerClass.SolibriRulesetId))
                {
                    SpaceTrackerClass.SolibriRulesetId = client
                      .ImportRulesetAsync(SpaceTrackerClass.SolibriRulesetPath)
                      .GetAwaiter().GetResult();
                }

                modelId = client.PartialUpdateAsync(modelId, ifcPath).GetAwaiter().GetResult();
                SpaceTrackerClass.SolibriModelUUID = modelId;
                client.CheckModelAsync(modelId, SpaceTrackerClass.SolibriRulesetId).GetAwaiter().GetResult();
                bool done = client.WaitForCheckCompletionAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
                if (!done)
                {
                    Logger.LogToFile("Solibri Prüfung hat das Zeitlimit überschritten", "solibri.log");
                    return errors;
                }
                var bcfDir = Path.Combine(Path.GetTempPath(), CommandManager.Instance.SessionId);
                string bcfZip = client.ExportBcfAsync(bcfDir).GetAwaiter().GetResult();
                errors = ParseBcfResults(bcfZip);
                foreach (var err in errors)
                    Logger.LogToFile($"Solibri Issue: {err.Severity} - {err.Message}", "solibri.log");
            }
            catch (Exception ex)
            {
                Logger.LogCrash("Solibri Validation", ex);
            }

            return errors;
        }
        // Liest eine BCF-ZIP-Datei ein und wandelt sie in ValidationError-Objekte um.
        private static List<ValidationError> ParseBcfResults(string bcfZipPath)
        {
            var result = new List<ValidationError>();
            using var archive = ZipFile.OpenRead(bcfZipPath);
            foreach (var entry in archive.Entries.Where(e => e.Name.Equals("markup.bcf", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = entry.Open();
                var xdoc = XDocument.Load(stream);

                string title = xdoc.Descendants("Title").FirstOrDefault()?.Value ?? "Issue";
                string desc = xdoc.Descendants("Description").FirstOrDefault()?.Value ?? string.Empty;
                string sevText = xdoc.Descendants("Severity").FirstOrDefault()?.Value ??
                                  xdoc.Descendants("Priority").FirstOrDefault()?.Value;

                Severity sev = Severity.Info;
                if (!string.IsNullOrEmpty(sevText))
                {
                    if (int.TryParse(sevText, out int sevNum))
                    {
                        if (sevNum >= 80) sev = Severity.Error;
                        else if (sevNum >= 40) sev = Severity.Warning;
                    }
                    else
                    {
                        if (sevText.Equals("high", StringComparison.OrdinalIgnoreCase) ||
                            sevText.Equals("critical", StringComparison.OrdinalIgnoreCase) ||
                            sevText.Equals("error", StringComparison.OrdinalIgnoreCase))
                            sev = Severity.Error;
                        else if (sevText.Equals("medium", StringComparison.OrdinalIgnoreCase) ||
                                 sevText.Equals("warning", StringComparison.OrdinalIgnoreCase) ||
                                 sevText.Equals("moderate", StringComparison.OrdinalIgnoreCase))
                            sev = Severity.Warning;
                    }
                }

                result.Add(new ValidationError
                {
                    Message = string.IsNullOrWhiteSpace(desc) ? title : $"{title}: {desc}",
                    Severity = sev
                });
            }
            return result;
        }
    }
}