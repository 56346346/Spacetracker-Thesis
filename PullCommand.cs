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
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)

    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        if (uiDoc?.Document is null)
            return Result.Failed;
        Document doc = uiDoc.Document;
        var cmdMgr = CommandManager.Instance;
        var connector = cmdMgr.Neo4jConnector;

         List<WallNode> walls;
        try
        {
             // Daten direkt laden (kein Hintergrund-Thread, um Revit API sicher zu nutzen)
            walls = connector.GetUpdatedWallsAsync(cmdMgr.LastSyncTime)
                .GetAwaiter().GetResult();
        }
        catch (Neo4j.Driver.Neo4jException ex)
        {
            TaskDialog.Show("Neo4j", $"Fehler: {ex.Message}\nBitte erneut versuchen.");
            return Result.Failed;
        }
       using (var revitTx = new Transaction(doc, "Import Wall"))
        {
            revitTx.Start();
            foreach (var w in walls)
            {
                if (doc.GetElement(w.Uid) != null)
                    continue;
                Line loc = Line.CreateBound(
                    new XYZ(UnitConversion.ToFt(w.X1), UnitConversion.ToFt(w.Y1), UnitConversion.ToFt(w.Z1)),
                    new XYZ(UnitConversion.ToFt(w.X2), UnitConversion.ToFt(w.Y2), UnitConversion.ToFt(w.Z2)));
 Wall.Create(doc, loc, new ElementId(w.TypeId), new ElementId(w.LevelId),
                    UnitConversion.ToFt(w.HeightMm), UnitConversion.ToFt(w.ThicknessMm), false, w.Structural);            }
            revitTx.Commit();
   }

        cmdMgr.LastSyncTime = System.DateTime.UtcNow;
        cmdMgr.PersistSyncTime();

        TaskDialog.Show("Neo4j", $"{walls.Count} Wände importiert.");
        return Result.Succeeded;
           
}
}