using System.Linq;
using Autodesk.Revit.DB;

namespace InstantSync.Core.Commands
{
    /// <summary>
    /// Failure preprocessor that ignores warnings and rolls back on errors.
    /// </summary>
    public class SimpleFailurePreprocessor : IFailuresPreprocessor
    {
        /// <inheritdoc />
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                }
            }

            bool hasErrors = failuresAccessor.GetFailureMessages().Any(f => f.GetSeverity() == FailureSeverity.Error);
            return hasErrors ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
