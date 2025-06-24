using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public static class PipeSerializer
{
    public static Dictionary<string, object> ToNode(MEPCurve pipe)
    {
        var lc = pipe.Location as LocationCurve;
        var line = lc?.Curve as Line;
        XYZ s = line?.GetEndPoint(0) ?? XYZ.Zero;
        XYZ e = line?.GetEndPoint(1) ?? XYZ.Zero;
        double diameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;
        Level level = pipe.ReferenceLevel ?? pipe.Document.GetElement(pipe.LevelId) as Level;
        return new Dictionary<string, object>
        {
            ["uid"] = pipe.UniqueId,
            ["elementId"] = pipe.Id.Value,
            ["levelId"] = level?.Id.Value ?? -1,
            ["x1"] = UnitConversion.ToMm(s.X),
            ["y1"] = UnitConversion.ToMm(s.Y),
            ["z1"] = UnitConversion.ToMm(s.Z),
            ["x2"] = UnitConversion.ToMm(e.X),
            ["y2"] = UnitConversion.ToMm(e.Y),
            ["z2"] = UnitConversion.ToMm(e.Z),
            ["diameter"] = UnitConversion.ToMm(diameter),
            ["created"] = DateTime.UtcNow,
            ["modified"] = DateTime.UtcNow,
            ["user"] = Environment.UserName
        };
    }
}