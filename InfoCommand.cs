using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SpaceTracker
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class InfoCommand : IExternalCommand
    {
                // Zeigt eine kurze Erläuterung der verfügbaren Befehle an.

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string infoText =
                "Export to Neo4j: Exportiert das gesamte Modell in die Datenbank.\n" +
                "Pull Changes: Holt externe Änderungen in das Modell.\n" +
                "Consistency Check: Prüft die Konsistenz zwischen Modell und Datenbank.";

            Autodesk.Revit.UI.TaskDialog.Show("SpaceTracker Info", infoText);
            return Result.Succeeded;
        }
    }
}