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
using System.Threading;
using System.Runtime.Versioning;

namespace SpaceTracker;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
[SupportedOSPlatform("windows")]
public class PushCommand : IExternalCommand

{

    // Lädt alle Wände des aktuellen Modells zu Neo4j hoch.

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
        // Wanddaten auf dem Revit-Hauptthread sammeln
        var wallData = new List<Dictionary<string, object>>();
        foreach (Wall w in walls)
        {
            var data = WallSerializer.ToNode(w);
            data["modified"] = DateTime.UtcNow;
            wallData.Add(data);
        }
        // Asynchron in Neo4j schreiben
        _ = Task.Run(() => PushWallsAsync(wallData, connector));
        return Result.Succeeded;
    }
    private static async Task PushWallsAsync(List<Dictionary<string, object>> wallData, Neo4jConnector connector)
    {
        Logger.LogToFile($"PushWallsAsync start ({wallData.Count} walls)", "concurrency.log");
        var tasks = new List<Task>();
        using var semaphore = new SemaphoreSlim(4); // limit parallel writes

        foreach (var data in wallData)

        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await connector.UpsertWallAsync(data).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("PushWall", ex);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }
        await Task.WhenAll(tasks);
        Logger.LogToFile("PushWallsAsync completed", "concurrency.log");
    }
}
