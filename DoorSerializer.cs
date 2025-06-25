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
}