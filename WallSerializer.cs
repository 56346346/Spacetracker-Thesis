using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

using static SpaceTracker.ParameterUtils;


namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public static class WallSerializer
{
    public static Dictionary<string, object> ToNode(Wall wall)
    {
        var lc = wall.Location as LocationCurve;
        var line = lc?.Curve as Line;
        XYZ s = line?.GetEndPoint(0) ?? XYZ.Zero;
        XYZ e = line?.GetEndPoint(1) ?? XYZ.Zero;
        double height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
        double thickness = wall.WallType.Width;
        double baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
        int locationLine = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? (int)WallLocationLine.WallCenterline;
        var dict = new Dictionary<string, object>
        {
            ["rvtClass"] = "Wall",
            ["uid"] = wall.UniqueId,
            ["elementId"] = wall.Id.Value,
            ["typeId"] = wall.GetTypeId().Value,
            ["typeName"] = wall.WallType.Name,
            ["familyName"] = wall.WallType.FamilyName,
            ["levelId"] = wall.LevelId.Value,
            ["x1"] = UnitConversion.ToMm(s.X),
            ["y1"] = UnitConversion.ToMm(s.Y),
            ["z1"] = UnitConversion.ToMm(s.Z),
            ["x2"] = UnitConversion.ToMm(e.X),
            ["y2"] = UnitConversion.ToMm(e.Y),
            ["z2"] = UnitConversion.ToMm(e.Z),
            ["height_mm"] = UnitConversion.ToMm(height),
            ["thickness_mm"] = UnitConversion.ToMm(thickness),
            ["structural"] = wall.Structural(),
            ["flipped"] = wall.Flipped,
            ["base_offset_mm"] = UnitConversion.ToMm(baseOffset),
            ["location_line"] = locationLine,
            ["user"] = Environment.UserName,
            ["created"] = DateTime.UtcNow,
            ["modified"] = DateTime.UtcNow
        };
        
        SerializeParameters(wall, dict);
        return dict;
    }
}