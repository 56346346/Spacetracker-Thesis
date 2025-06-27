using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using static SpaceTracker.ParameterUtils;

namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public static class ProvisionalSpaceSerializer
{
    private const string LogFile = "provisional_spaces.log";

    // Wandelt einen ProvisionalSpace in ein Dictionary zur Ablage in Neo4j um.

    public static Dictionary<string, object> ToNode(FamilyInstance inst)
    {
        Logger.LogToFile($"[Serializer] Start {inst.UniqueId}", LogFile);

        BoundingBoxXYZ bb = inst.get_BoundingBox(null);
        double width = 0, height = 0, thickness = 0;
        if (bb != null)
        {
            width = UnitConversion.ToMm(Math.Abs(bb.Max.X - bb.Min.X));
            height = UnitConversion.ToMm(Math.Abs(bb.Max.Z - bb.Min.Z));
            thickness = UnitConversion.ToMm(Math.Abs(bb.Max.Y - bb.Min.Y));
        }
        bool isProv = ParameterUtils.IsProvisionalSpace(inst);
        Logger.LogToFile($"[Serializer] isProv {isProv}", LogFile);
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
        Logger.LogToFile($"[Serializer] Dictionary ready for {inst.UniqueId}", LogFile);
        return dict;
    }

    // Erstellt einen ProvisionalSpaceNode und liefert zugleich das Dictionary
    // mit allen serialisierten Eigenschaften zur weiteren Verwendung.
    public static ProvisionalSpaceNode ToProvisionalSpaceNode(
        FamilyInstance inst,
        out Dictionary<string, object> dict)
    {
        Logger.LogToFile($"[Serializer] ToProvisionalSpaceNode {inst.UniqueId}", LogFile);
        dict = ToNode(inst);
        var node = new ProvisionalSpaceNode(
            dict.TryGetValue("guid", out var gObj) ? gObj.ToString() ?? string.Empty : string.Empty,
            dict.GetValueOrDefault("name", string.Empty).ToString() ?? string.Empty,
            dict.GetValueOrDefault("familyName", string.Empty).ToString() ?? string.Empty,
            dict.GetValueOrDefault("symbolName", string.Empty).ToString() ?? string.Empty,
            Convert.ToDouble(dict.GetValueOrDefault("width", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("height", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("thickness", 0.0)),
            dict.GetValueOrDefault("level", string.Empty).ToString() ?? string.Empty,
            Convert.ToInt64(dict["levelId"]),
            Convert.ToDouble(dict.GetValueOrDefault("x", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("y", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("z", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("rotation", 0.0)),
            Convert.ToInt64(dict.GetValueOrDefault("hostId", -1L)),
            Convert.ToInt32(dict.GetValueOrDefault("revitId", -1)),
            dict.GetValueOrDefault("ifcType", string.Empty).ToString() ?? string.Empty,
            dict.ContainsKey("category") ? dict["category"].ToString() : null,
            Convert.ToInt32(dict.GetValueOrDefault("phaseCreated", -1)),
            Convert.ToInt32(dict.GetValueOrDefault("phaseDemolished", -1)),
            Convert.ToDouble(dict.GetValueOrDefault("bbMinX", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("bbMinY", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("bbMinZ", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("bbMaxX", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("bbMaxY", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("bbMaxZ", 0.0))
        );
        Logger.LogToFile($"[Serializer] Node created for {inst.UniqueId}", LogFile);
        return node;

    }
}
