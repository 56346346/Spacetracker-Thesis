using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

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
        return new Dictionary<string, object>
        {
            ["uid"] = wall.UniqueId,
            ["typeId"] = wall.GetTypeId().Value,
            ["levelId"] = wall.LevelId.Value,
            ["x1"] = UnitConversion.ToMm(s.X),
            ["y1"] = UnitConversion.ToMm(s.Y),
            ["z1"] = UnitConversion.ToMm(s.Z),
            ["x2"] = UnitConversion.ToMm(e.X),
            ["y2"] = UnitConversion.ToMm(e.Y),
            ["z2"] = UnitConversion.ToMm(e.Z),
            ["h"] = UnitConversion.ToMm(height),
            ["t"] = UnitConversion.ToMm(thickness),
            ["struct"] = wall.Structural(),
            ["flip"] = wall.Flipped,
            ["bo"] = UnitConversion.ToMm(baseOffset),
            ["locLine"] = locationLine,
            ["user"] = Environment.UserName,
            ["created"] = DateTime.UtcNow,
            ["modified"] = DateTime.UtcNow
        };
    }
}