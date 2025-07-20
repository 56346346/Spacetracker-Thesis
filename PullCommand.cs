#nullable enable
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.Collections.Generic;
using System.IO;
using Neo4j.Driver;  // für IRecord .As<T> Extensions
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;
using Autodesk.Revit.DB.Architecture;
using System.Runtime.Versioning;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB.Structure;
using SpaceTracker;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using Autodesk.Revit.DB.Plumbing;





namespace SpaceTracker;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
[SupportedOSPlatform("windows")]
public class PullCommand : IExternalCommand
{

    // Führt den eigentlichen Pull-Vorgang aus. Optional kann die Erfolgsmeldung
    // angezeigt werden.
    public static Result RunPull(Document doc, bool showDialog = true)
    {
        return RunPullAsync(doc, showDialog).GetAwaiter().GetResult();
    }

    private static async Task<Result> RunPullAsync(Document doc, bool showDialog)
    {
        var cmdMgr = CommandManager.Instance;
        var connector = cmdMgr.Neo4jConnector;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        List<WallNode> walls;
        List<DoorNode> doors;
        List<PipeNode> pipes;
        List<ProvisionalSpaceNode> provisionalSpaces;
        try
        {
            var wallsTask = connector.GetUpdatedWallsAsync(cmdMgr.LastSyncTime);
            var doorsTask = connector.GetUpdatedDoorsAsync(cmdMgr.LastSyncTime);
            var pipesTask = connector.GetUpdatedPipesAsync(cmdMgr.LastSyncTime);
            var spacesTask = connector.GetUpdatedProvisionalSpacesAsync(cmdMgr.LastSyncTime);

            var allTasks = Task.WhenAll(wallsTask, doorsTask, pipesTask, spacesTask);
            var completed = await Task.WhenAny(allTasks, Task.Delay(Timeout.Infinite, cts.Token));
            if (completed != allTasks)
                throw new OperationCanceledException("Pull-Operation wurde nach 10 Sekunden abgebrochen.");

            walls = await wallsTask;
            doors = await doorsTask;
            pipes = await pipesTask;
            provisionalSpaces = await spacesTask;
        }
        catch (OperationCanceledException oce)
        {
            Logger.LogCrash("Pull abgebrochen: " + oce.Message, oce);
            TaskDialog.Show("Neo4j", "Pull-Vorgang abgebrochen (Timeout).");
            return Result.Cancelled;
        }
        catch (Neo4jException ne)
        {
            Logger.LogCrash("Neo4j-Fehler beim Pull: " + ne.Message, ne);
            TaskDialog.Show("Neo4j", $"Fehler beim Datenabruf: {ne.Message}");
            return Result.Failed;
        }
        catch (Exception ex)
        {
            Logger.LogCrash("Unbekannter Fehler beim Pull: " + ex, ex);
            TaskDialog.Show("Neo4j", "Unerwarteter Fehler: " + ex.Message);
            return Result.Failed;
        }
        using (var revitTx = new Transaction(doc, "Import Elements"))
        {
            revitTx.Start();
            foreach (var w in walls)
            {
                if (doc.GetElement(w.Uid) != null)
                    continue;
                Line loc = Line.CreateBound(
                    new XYZ(UnitConversion.ToFt(w.X1), UnitConversion.ToFt(w.Y1), UnitConversion.ToFt(w.Z1)),
                    new XYZ(UnitConversion.ToFt(w.X2), UnitConversion.ToFt(w.Y2), UnitConversion.ToFt(w.Z2)));
                Wall newWall = Wall.Create(doc, loc, new ElementId(w.TypeId), new ElementId(w.LevelId),
                                   UnitConversion.ToFt(w.HeightMm), UnitConversion.ToFt(w.BaseOffsetMm), w.Flipped, w.Structural);
                Parameter llp = newWall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                if (llp != null && !llp.IsReadOnly)
                    llp.Set(w.LocationLine);
            }
            foreach (var d in doors)
            {
                if (!string.IsNullOrEmpty(d.Uid) && doc.GetElement(d.Uid) != null)
                    continue;
                var symbol = doc.GetElement(new ElementId((int)d.TypeId)) as FamilySymbol;
                if (symbol == null) continue;
                if (!symbol.IsActive) symbol.Activate();

                XYZ p = new XYZ(UnitConversion.ToFt(d.X), UnitConversion.ToFt(d.Y), UnitConversion.ToFt(d.Z));
                Level level = doc.GetElement(new ElementId((int)d.LevelId)) as Level;
                Element host = null;
                if (!string.IsNullOrEmpty(d.HostUid))
                    host = doc.GetElement(d.HostUid);
                if (host == null && d.HostId > 0)
                    host = doc.GetElement(new ElementId((int)d.HostId)); FamilyInstance fi = host != null
                    ? doc.Create.NewFamilyInstance(p, symbol, host, level, StructuralType.NonStructural)
                    : doc.Create.NewFamilyInstance(p, symbol, level, StructuralType.NonStructural);

                if (Math.Abs(d.Rotation) > 1e-6)
                {
                    var axis = Line.CreateBound(p, new XYZ(p.X, p.Y, p.Z + 1));
                    ElementTransformUtils.RotateElement(doc, fi.Id, axis, d.Rotation);
                }
                fi.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.Set(UnitConversion.ToFt(d.Width));
                fi.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.Set(UnitConversion.ToFt(d.Height));
            }

            foreach (var p in pipes)
            {
                if (!string.IsNullOrEmpty(p.Uid) && doc.GetElement(p.Uid) != null)
                    continue;
                XYZ s = new XYZ(UnitConversion.ToFt(p.X1), UnitConversion.ToFt(p.Y1), UnitConversion.ToFt(p.Z1));
                XYZ e = new XYZ(UnitConversion.ToFt(p.X2), UnitConversion.ToFt(p.Y2), UnitConversion.ToFt(p.Z2));
                ElementId sysId = p.SystemTypeId > 0 ? new ElementId((int)p.SystemTypeId) : ElementId.InvalidElementId;
                Pipe newPipe = Pipe.Create(doc, sysId, new ElementId((int)p.TypeId), new ElementId((int)p.LevelId), s, e);
                if (p.Diameter > 0)
                    newPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(UnitConversion.ToFt(p.Diameter));
            }

            foreach (var ps in provisionalSpaces)
            {
                if (!string.IsNullOrEmpty(ps.Guid) && doc.GetElement(ps.Guid) != null)
                    continue;
                FamilySymbol symbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.FamilyName.Equals(ps.FamilyName, StringComparison.OrdinalIgnoreCase));
                if (symbol == null)
                    continue;
                if (!symbol.IsActive)
                    symbol.Activate();

                double cX = (ps.BbMinX + ps.BbMaxX) / 2;
                double cY = (ps.BbMinY + ps.BbMaxY) / 2;
                double cZ = (ps.BbMinZ + ps.BbMaxZ) / 2;
                XYZ center = new XYZ(UnitConversion.ToFt(cX), UnitConversion.ToFt(cY), UnitConversion.ToFt(cZ));
                Level level = doc.GetElement(new ElementId((int)ps.LevelId)) as Level;
                FamilyInstance inst = doc.Create.NewFamilyInstance(center, symbol, level, StructuralType.NonStructural);
                inst.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(ps.PhaseCreated);
                inst.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.Set(ps.PhaseDemolished);

                double widthFt = UnitConversion.ToFt(ps.BbMaxX - ps.BbMinX);
                double depthFt = UnitConversion.ToFt(ps.BbMaxY - ps.BbMinY);
                double heightFt = UnitConversion.ToFt(ps.BbMaxZ - ps.BbMinZ);

                inst.LookupParameter("Width")?.Set(widthFt);
                inst.LookupParameter("Depth")?.Set(depthFt);
                inst.LookupParameter("Height")?.Set(heightFt);
            }
            revitTx.Commit();
        }

        cmdMgr.LastSyncTime = System.DateTime.UtcNow;
        cmdMgr.PersistSyncTime();
        await connector.UpdateSessionLastSyncAsync(cmdMgr.SessionId, cmdMgr.LastSyncTime);
        var key = doc.PathName ?? doc.Title;
        if (SessionManager.OpenSessions.TryGetValue(key, out var session))
            session.LastSyncTime = cmdMgr.LastSyncTime;
        if (showDialog)
        {
            TaskDialog.Show("Neo4j", $"{walls.Count} Wände, {doors.Count} Türen, {pipes.Count} Rohre und {provisionalSpaces.Count} Provisional Spaces importiert.");
        }

        return Result.Succeeded;
    }

    // Importiert neue oder geänderte Wände aus Neo4j in das aktuelle Modell.
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)

    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        if (uiDoc?.Document is null)
            return Result.Failed;
        return RunPull(uiDoc.Document, showDialog: true);

    }
}