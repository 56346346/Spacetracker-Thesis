using Autodesk.Revit.DB;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SpaceTracker
{
    // Simple stub to call Solibri validation asynchronously.
    // In production this would integrate with Solibri's API.
    public static class SolibriChecker
    {
        public static Task CheckElementAsync(ElementId id, Document doc)
        {
            Debug.WriteLine($"[SolibriChecker] Checking element {id.Value}");
            return Task.CompletedTask;
        }
    }
}