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
using Autodesk.Revit.DB.Architecture;
using System.Runtime.Versioning;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB.Structure;
using SpaceTracker;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;





namespace SpaceTracker;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
[SupportedOSPlatform("windows")]
public class PullCommand : IExternalCommand
{

    // Importiert neue oder geänderte Wände aus Neo4j in das aktuelle Modell.
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)

    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        if (uiDoc?.Document is null)
            return Result.Failed;
        Document doc = uiDoc.Document;
        var cmdMgr = CommandManager.Instance;
        var connector = cmdMgr.Neo4jConnector;

        List<WallNode> walls;
        List<DoorNode> doors;
        try
        {
            // Daten direkt laden (kein Hintergrund-Thread, um Revit API sicher zu nutzen)
            walls = connector.GetUpdatedWallsAsync(cmdMgr.LastSyncTime)
                .GetAwaiter().GetResult();
            doors = connector.GetUpdatedDoorsAsync(cmdMgr.LastSyncTime)
           .GetAwaiter().GetResult();
        }
        catch (Neo4j.Driver.Neo4jException ex)
        {
            TaskDialog.Show("Neo4j", $"Fehler: {ex.Message}\nBitte erneut versuchen.");
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
                Element host = d.HostId > 0 ? doc.GetElement(new ElementId((int)d.HostId)) : null;
                FamilyInstance fi = host != null
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
            revitTx.Commit();
        }

        cmdMgr.LastSyncTime = System.DateTime.UtcNow;
        cmdMgr.PersistSyncTime();

        TaskDialog.Show("Neo4j", $"{walls.Count} Wände und {doors.Count} Türen importiert.");
        return Result.Succeeded;

    }
}