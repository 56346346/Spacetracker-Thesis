using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

using static SpaceTracker.ParameterUtils;

namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public static class DoorSerializer
{
    // Erstellt ein Dictionary mit allen relevanten Eigenschaften einer Tür,
    // welches später nach Neo4j geschrieben werden kann.
    public static Dictionary<string, object> ToNode(FamilyInstance door)
    {
        var loc = door.Location as LocationPoint;
        BoundingBoxXYZ bb = door.get_BoundingBox(null);
        double width = 0, height = 0, thickness = 0;
        if (bb != null)
        {
            width = UnitConversion.ToMm(Math.Abs(bb.Max.X - bb.Min.X));
            height = UnitConversion.ToMm(Math.Abs(bb.Max.Z - bb.Min.Z));
            thickness = UnitConversion.ToMm(Math.Abs(bb.Max.Y - bb.Min.Y));
        }

        var dict = new Dictionary<string, object>
        {
            ["rvtClass"] = "Door",
            ["uid"] = door.UniqueId,
            ["elementId"] = door.Id.Value,
            ["typeId"] = door.GetTypeId().Value,
            ["familyName"] = door.Symbol?.FamilyName ?? string.Empty,
            ["symbolName"] = door.Symbol?.Name ?? string.Empty,
            ["levelId"] = door.LevelId.Value,
            ["hostId"] = door.Host?.Id.Value ?? -1,
            ["x"] = UnitConversion.ToMm(loc?.Point.X ?? 0),
            ["y"] = UnitConversion.ToMm(loc?.Point.Y ?? 0),
            ["z"] = UnitConversion.ToMm(loc?.Point.Z ?? 0),
            ["rotation"] = loc?.Rotation ?? 0,
            ["width"] = width,
            ["height"] = height,
            ["thickness"] = thickness,
            ["created"] = DateTime.UtcNow,
            ["modified"] = DateTime.UtcNow,
            ["user"] = Environment.UserName
        };

        SerializeParameters(door, dict);
        return dict;
    }
    
    // Erstellt einen DoorNode aus den übertragenen Attributen.
    public static DoorNode ToDoorNode(FamilyInstance door)
    {
        var dict = ToNode(door);
        return new DoorNode(
            dict.TryGetValue("uid", out var uidObj) ? uidObj.ToString() ?? string.Empty : string.Empty,
            Convert.ToInt64(dict["elementId"]),
            Convert.ToInt64(dict["typeId"]),
            dict.GetValueOrDefault("familyName", string.Empty).ToString() ?? string.Empty,
            dict.GetValueOrDefault("symbolName", string.Empty).ToString() ?? string.Empty,
            Convert.ToInt64(dict["levelId"]),
            Convert.ToInt64(dict["hostId"]),
            Convert.ToDouble(dict.GetValueOrDefault("x", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("y", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("z", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("rotation", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("width", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("height", 0.0)),
            Convert.ToDouble(dict.GetValueOrDefault("thickness", 0.0))
        );
    }
}