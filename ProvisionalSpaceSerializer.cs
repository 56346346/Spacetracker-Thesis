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
                bool isProv = ParameterUtils.IsProvisionalSpace(inst);

        BoundingBoxXYZ? bbView = null;
        XYZ bbMin = XYZ.Zero, bbMax = XYZ.Zero;
        if (isProv)
        {
            bbView = inst.get_BoundingBox(inst.Document.ActiveView) ?? inst.get_BoundingBox(null);
            if (bbView != null)
            {
                bbMin = bbView.Min;
                bbMax = bbView.Max;
            }
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
        if (isProv)
        {
            dict["category"] = inst.Category?.Name ?? string.Empty;
            dict["familyName"] = inst.Symbol?.FamilyName ?? string.Empty;
            dict["phaseCreated"] = inst.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsInteger() ?? -1;
            dict["phaseDemolished"] = inst.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsInteger() ?? -1;
            dict["bbMinX"] = UnitConversion.ToMm(bbMin.X);
            dict["bbMinY"] = UnitConversion.ToMm(bbMin.Y);
            dict["bbMinZ"] = UnitConversion.ToMm(bbMin.Z);
            dict["bbMaxX"] = UnitConversion.ToMm(bbMax.X);
            dict["bbMaxY"] = UnitConversion.ToMm(bbMax.Y);
            dict["bbMaxZ"] = UnitConversion.ToMm(bbMax.Z);
        }
        
        SerializeParameters(inst, dict);
        return dict;
    }
}
