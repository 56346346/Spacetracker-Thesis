using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using static SpaceTracker.ParameterUtils;

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
        var dict = new Dictionary<string, object>
        {
            ["rvtClass"] = "Pipe",
            ["uid"] = pipe.UniqueId,
            ["elementId"] = pipe.Id.Value,
            ["typeId"] = pipe.GetTypeId().Value,
            ["systemTypeId"] = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsInteger() ?? -1,
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
        
        SerializeParameters(pipe, dict);
        return dict;
    }
}