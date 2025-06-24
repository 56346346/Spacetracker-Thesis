using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public static class ProvisionalSpaceSerializer
{
    public static Dictionary<string, object> ToNode(FamilyInstance inst)
    {
        BoundingBoxXYZ bb = inst.get_BoundingBox(null);
        double width = 0, height = 0, thickness = 0;
        if (bb != null)
        {
            width = UnitConversion.ToMm(Math.Abs(bb.Max.X - bb.Min.X));
            height = UnitConversion.ToMm(Math.Abs(bb.Max.Z - bb.Min.Z));
            thickness = UnitConversion.ToMm(Math.Abs(bb.Max.Y - bb.Min.Y));
        }

        var level = inst.Document.GetElement(inst.LevelId) as Level;
        string levelName = level?.Name ?? "";
                string name = inst.Name ?? inst.Symbol?.FamilyName ?? string.Empty;

        return new Dictionary<string, object>
        {
            ["guid"] = inst.UniqueId,
            ["name"] = name,
            ["width"] = width,
            ["height"] = height,
            ["thickness"] = thickness,
            ["level"] = levelName,
            ["revitId"] = inst.Id.Value,
            ["ifcType"] = "IfcOpeningElement",
            ["created"] = DateTime.UtcNow,
            ["modified"] = DateTime.UtcNow,
            ["user"] = Environment.UserName
        };
    }
}
