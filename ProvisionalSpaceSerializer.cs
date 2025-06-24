using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using static SpaceTracker.ParameterUtils;

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
        string levelName = level?.Name ?? string.Empty;
        string name = inst.Name ?? inst.Symbol?.FamilyName ?? string.Empty;
        var loc = inst.Location as LocationPoint;

        var dict = new Dictionary<string, object>
        {
            ["rvtClass"] = "ProvisionalSpace",
            ["guid"] = inst.UniqueId,
            ["elementId"] = inst.Id.Value,
            ["typeId"] = inst.GetTypeId().Value,
            ["familyName"] = inst.Symbol?.FamilyName ?? string.Empty,
            ["symbolName"] = inst.Symbol?.Name ?? string.Empty,
            ["name"] = name,
            ["width"] = width,
            ["height"] = height,
            ["thickness"] = thickness,
            ["level"] = levelName,
            ["levelId"] = inst.LevelId.Value,
            ["x"] = UnitConversion.ToMm(loc?.Point.X ?? 0),
            ["y"] = UnitConversion.ToMm(loc?.Point.Y ?? 0),
            ["z"] = UnitConversion.ToMm(loc?.Point.Z ?? 0),
            ["rotation"] = loc?.Rotation ?? 0,
            ["hostId"] = inst.Host?.Id.Value ?? -1,
            ["revitId"] = inst.Id.Value,
            ["ifcType"] = GetIfcEntity(inst),
            ["created"] = DateTime.UtcNow,
            ["modified"] = DateTime.UtcNow,
            ["user"] = Environment.UserName
        };
        
        SerializeParameters(inst, dict);
        return dict;
    }
}
