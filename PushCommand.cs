using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.IO;
using System.Threading.Tasks;
using SpaceTracker;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;

using System.Runtime.Versioning;

namespace SpaceTracker;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
[SupportedOSPlatform("windows")]
public class PushCommand : IExternalCommand

{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)

    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        if (uiDoc?.Document is null)
            return Result.Failed;
        Document doc = uiDoc.Document;
        var connector = CommandManager.Instance.Neo4jConnector;

        IList<Element> walls = new FilteredElementCollector(doc)
            .OfClass(typeof(Wall))
            .ToElements();
        _ = Task.Run(async () =>
    {
        foreach (Wall w in walls)




        {

            var data = WallSerializer.ToNode(w);
            data["modified"] = System.DateTime.UtcNow;
            try
            {
                await connector.UpsertWallAsync(data).ConfigureAwait(false);

            }
            catch (Neo4j.Driver.Neo4jException ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Neo4j", $"Fehler: {ex.Message}\nBitte erneut versuchen.");
                return;
            }

        }
    });
        return Result.Succeeded;
    }
}
