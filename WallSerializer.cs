using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

using static SpaceTracker.ParameterUtils;


namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public static class WallSerializer
{
    // Erstellt ein Dictionary aller wichtigen Wand-Eigenschaften für Neo4j.
    public static Dictionary<string, object> ToNode(Wall wall)
    {
        var lc = wall.Location as LocationCurve;
        var line = lc?.Curve as Line;
        XYZ s = line?.GetEndPoint(0) ?? XYZ.Zero;
        XYZ e = line?.GetEndPoint(1) ?? XYZ.Zero;
        double height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
        double thickness = wall.WallType.Width;
        string name = wall.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString()
                    ?? wall.Name
                    ?? string.Empty;
        double baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
        int locationLine = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? (int)WallLocationLine.WallCenterline;
        
        // Get Level information - critical for reconstruction
        Level level = wall.Document.GetElement(wall.LevelId) as Level;
        string baseLevelUid = level?.UniqueId ?? string.Empty;
        
        var dict = new Dictionary<string, object>
        {
            ["rvtClass"] = "Wall",
            ["uid"] = GetNeo4jUid(wall),
            ["elementId"] = wall.Id.Value,
            ["typeId"] = wall.GetTypeId().Value,
            ["typeName"] = wall.WallType.Name,
            ["familyName"] = wall.WallType.FamilyName,
            ["Name"] = name,
            ["levelId"] = wall.LevelId.Value,
            // Store coordinates in meters for ChangeLog compatibility
            ["x1"] = UnitConversion.ToMeters(s.X),
            ["y1"] = UnitConversion.ToMeters(s.Y),
            ["z1"] = UnitConversion.ToMeters(s.Z),
            ["x2"] = UnitConversion.ToMeters(e.X),
            ["y2"] = UnitConversion.ToMeters(e.Y),
            ["z2"] = UnitConversion.ToMeters(e.Z),
            // Critical: Store baseLevelUid for reconstruction
            ["baseLevelUid"] = baseLevelUid,
            // Store height in meters for consistency
            ["height"] = UnitConversion.ToMeters(height),
            ["thickness_m"] = UnitConversion.ToMeters(thickness),
            // Keep mm versions for compatibility
            ["height_mm"] = UnitConversion.ToMm(height),
            ["thickness_mm"] = UnitConversion.ToMm(thickness),
            ["structural"] = WallNode.IsStructural(wall),
            ["flipped"] = wall.Flipped,
            ["base_offset_mm"] = UnitConversion.ToMm(baseOffset),
            ["location_line"] = locationLine,
            ["user"] = CommandManager.Instance.SessionId,
            ["created"] = DateTime.UtcNow,
            ["modified"] = DateTime.UtcNow
        };

        // Add room bounding property
        var roomBoundingParam = wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
        if (roomBoundingParam != null)
        {
            dict["roomBounding"] = roomBoundingParam.AsInteger() == 1;
        }

        // Add top constraint information
        var topConstraintParam = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
        var topOffsetParam = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
        
        if (topConstraintParam != null && topConstraintParam.AsElementId() != ElementId.InvalidElementId)
        {
            var topLevel = wall.Document.GetElement(topConstraintParam.AsElementId()) as Level;
            if (topLevel != null)
            {
                dict["topLevelUid"] = topLevel.UniqueId;
                dict["topLevelId"] = topLevel.Id.Value;
            }
        }
        
        if (topOffsetParam != null)
        {
            dict["top_offset_m"] = UnitConversion.ToMeters(topOffsetParam.AsDouble());
        }

        // Add unconnected height if wall uses it
        var unconnectedHeightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
        if (unconnectedHeightParam != null)
        {
            dict["unconnected_height_m"] = UnitConversion.ToMeters(unconnectedHeightParam.AsDouble());
        }

        SerializeParameters(wall, dict);
        return dict;
    }

    // Erstellt ein WallNode-Objekt aus den gesammelten Eigenschaften.
    public static WallNode ToWallNode(Wall wall)
    {
        var dict = ToNode(wall);
        return new WallNode(
            dict.TryGetValue("uid", out var uidObj) ? uidObj.ToString() ?? string.Empty : string.Empty,
            Convert.ToInt64(dict["elementId"]),
            Convert.ToInt64(dict["typeId"]),
            dict.GetValueOrDefault("typeName", string.Empty).ToString() ?? string.Empty,
            dict.GetValueOrDefault("familyName", string.Empty).ToString() ?? string.Empty,
            Convert.ToInt64(dict["levelId"]),
            Convert.ToDouble(dict["x1"]),
            Convert.ToDouble(dict["y1"]),
            Convert.ToDouble(dict["z1"]),
            Convert.ToDouble(dict["x2"]),
            Convert.ToDouble(dict["y2"]),
            Convert.ToDouble(dict["z2"]),
            Convert.ToDouble(dict["height_mm"]),
            Convert.ToDouble(dict["thickness_mm"]),
            Convert.ToBoolean(dict["structural"]),
            dict.TryGetValue("flipped", out var flipObj) && Convert.ToBoolean(flipObj),
            dict.TryGetValue("base_offset_mm", out var boObj) ? Convert.ToDouble(boObj) : 0.0,
            dict.TryGetValue("location_line", out var llObj) ? Convert.ToInt32(llObj) : (int)WallLocationLine.WallCenterline
        );
    }
    
}