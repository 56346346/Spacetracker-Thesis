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

    // Lädt Wände, Türen, Provisional Spaces und Rohre des aktuellen Modells zu Neo4j hoch.

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

        IList<Element> doors = new FilteredElementCollector(doc)
     .OfCategory(BuiltInCategory.OST_Doors)
            .OfClass(typeof(FamilyInstance))
            .ToElements();
        IList<Element> pipes = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_PipeCurves)
            .OfClass(typeof(MEPCurve))
            .ToElements();

        IList<Element> provSpaces = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_GenericModel)
            .OfClass(typeof(FamilyInstance))
            .ToElements()
            .Where(e => ParameterUtils.IsProvisionalSpace(e))
            .ToList();
        // Wanddaten auf dem Revit-Hauptthread sammeln
        var wallData = new List<Dictionary<string, object>>();
        foreach (Wall w in walls)
        {
            var data = WallSerializer.ToNode(w);
            data["modified"] = DateTime.UtcNow;
            wallData.Add(data);
        }

        var doorData = new List<Dictionary<string, object>>();
        foreach (FamilyInstance d in doors)
        {
            var data = DoorSerializer.ToNode(d);
            data["modified"] = DateTime.UtcNow;
            doorData.Add(data);
        }
        var pipeData = new List<Dictionary<string, object>>();
        foreach (MEPCurve p in pipes)
        {
            var data = PipeSerializer.ToNode(p);
            data["modified"] = DateTime.UtcNow;
            pipeData.Add(data);
        }

        var provData = new List<Dictionary<string, object>>();
        foreach (FamilyInstance ps in provSpaces.Cast<FamilyInstance>())
        {
            _ = ProvisionalSpaceSerializer.ToProvisionalSpaceNode(ps, out var data);
            data["modified"] = DateTime.UtcNow;
            provData.Add(data);
        }

        // Bounding Box auswerten um Rohr/ProvisionalSpace-Beziehungen zu finden
        var extractor = new SpaceExtractor(CommandManager.Instance);
        var relations = extractor.GetPipeProvisionalSpaceRelations(doc);


      // Asynchron in Neo4j schreiben. Zuerst alle Knoten aktualisieren,
        // danach die Beziehungen anlegen, damit die Zielknoten bereits existieren.
        _ = Task.Run(async () =>
        {
            var wallTask = PushWallsAsync(wallData, connector);
            var doorTask = PushDoorsAsync(doorData, connector);
            var pipeTask = PushPipesAsync(pipeData, connector);
            var provTask = PushProvisionalSpacesAsync(provData, connector);

            await Task.WhenAll(wallTask, doorTask, pipeTask, provTask);

            await LinkPipeProvisionalSpacesAsync(relations, connector);
        });
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

    private static async Task PushDoorsAsync(List<Dictionary<string, object>> doorData, Neo4jConnector connector)
    {
        Logger.LogToFile($"PushDoorsAsync start ({doorData.Count} doors)", "concurrency.log");
        var tasks = new List<Task>();
        using var semaphore = new SemaphoreSlim(4);

        foreach (var data in doorData)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await connector.UpsertDoorAsync(data).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("PushDoor", ex);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        Logger.LogToFile("PushDoorsAsync completed", "concurrency.log");
    }

    private static async Task PushPipesAsync(List<Dictionary<string, object>> pipeData, Neo4jConnector connector)
    {
        Logger.LogToFile($"PushPipesAsync start ({pipeData.Count} pipes)", "concurrency.log");
        var tasks = new List<Task>();
        using var semaphore = new SemaphoreSlim(4);

        foreach (var data in pipeData)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await connector.UpsertPipeAsync(data).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("PushPipe", ex);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        Logger.LogToFile("PushPipesAsync completed", "concurrency.log");
    }

    private static async Task PushProvisionalSpacesAsync(List<Dictionary<string, object>> provData, Neo4jConnector connector)
    {
        Logger.LogToFile($"PushProvisionalSpacesAsync start ({provData.Count} provisional spaces)", "concurrency.log");
        var tasks = new List<Task>();
        using var semaphore = new SemaphoreSlim(4);

        foreach (var data in provData)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    string guid = data["guid"].ToString();
                    await connector.UpsertProvisionalSpaceAsync(guid!, data).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("PushProvisionalSpace", ex);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        Logger.LogToFile("PushProvisionalSpacesAsync completed", "concurrency.log");
    }

    private static async Task LinkPipeProvisionalSpacesAsync(List<(string PipeUid, string ProvGuid)> relations, Neo4jConnector connector)
    {
        Logger.LogToFile($"LinkPipeProvisionalSpacesAsync start ({relations.Count} links)", "concurrency.log");
        var tasks = new List<Task>();
        using var semaphore = new SemaphoreSlim(4);

        foreach (var rel in relations)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await connector.LinkPipeToProvisionalSpaceAsync(rel.PipeUid, rel.ProvGuid).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogCrash("LinkPipeProvisionalSpace", ex);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        Logger.LogToFile("LinkPipeProvisionalSpacesAsync completed", "concurrency.log");
    }
}
