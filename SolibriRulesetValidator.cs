using Autodesk.Revit.DB;
using System.Collections.Generic;

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
        // In production this would call the Solibri API and return the found issues.
        public static List<ValidationError> Validate(Document doc)
        {
            return new List<ValidationError>();
        }
    }
}