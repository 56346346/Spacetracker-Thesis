using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Neo4j.Driver;
using System.Diagnostics;
using System.Linq;


namespace SpaceTracker;

#nullable enable

[SupportedOSPlatform("windows")]
public static class RevitElementBuilder
{
    private const string ProvLog = "provisional_spaces.log"; 
    private static double RoundValue(double v)
    {
        return Math.Abs(v - Math.Round(v)) < 0.001 ? Math.Round(v, 1) : v;
    }
    private static Wall? FindHostWall(Document doc, XYZ point, double tol = 1.0)
    {
        Wall? bestWall = null;
        double bestDist = double.MaxValue;
        var collector = new FilteredElementCollector(doc)
            .OfClass(typeof(Wall));
        foreach (Wall w in collector.Cast<Wall>())
        {
            BoundingBoxXYZ? bb = w.get_BoundingBox(null);
            if (bb == null) continue;
            bool inside =
               point.X >= bb.Min.X - tol && point.X <= bb.Max.X + tol &&
               point.Y >= bb.Min.Y - tol && point.Y <= bb.Max.Y + tol &&
               point.Z >= bb.Min.Z - tol && point.Z <= bb.Max.Z + tol;

            if (!inside)
                continue;

            XYZ center = (bb.Min + bb.Max) * 0.5;
            double dist = center.DistanceTo(point);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestWall = w;
            }
        }
        return bestWall;
    }
    // Erstellt ein Revit-Element anhand der in Neo4j gespeicherten Eigenschaften.
    public static Element BuildFromNode(Document doc, Dictionary<string, object> node)
    {
        if (!node.TryGetValue("rvtClass", out object? clsObj))
            throw new ArgumentException("Node is missing 'rvtClass'");

        string cls = clsObj.ToString() ?? string.Empty;
        return cls switch
        {
            "Wall" => BuildWall(doc, node),
            "Pipe" => BuildPipe(doc, node),
            "Door" => BuildDoor(doc, node),
            "ProvisionalSpace" => BuildProvisionalSpace(doc, node),
            _ => throw new NotSupportedException($"Unsupported rvtClass {cls}")
        };
    }
    // Baut eine Wand aus den übertragenen Attributen nach.
    private static Wall BuildWall(Document doc, Dictionary<string, object> node)
    {
        XYZ s = new XYZ(UnitConversion.ToFt(Convert.ToDouble(node["x1"])),
                        UnitConversion.ToFt(Convert.ToDouble(node["y1"])),
                        UnitConversion.ToFt(Convert.ToDouble(node["z1"])));
        XYZ e = new XYZ(UnitConversion.ToFt(Convert.ToDouble(node["x2"])),
                        UnitConversion.ToFt(Convert.ToDouble(node["y2"])),
                        UnitConversion.ToFt(Convert.ToDouble(node["z2"])));
        Line line = Line.CreateBound(s, e);
        ElementId typeId = new ElementId(Convert.ToInt32(node["typeId"]));
         if (node.TryGetValue("WallType", out var typeNameObj) && typeNameObj is string typeName)
        {
            var wt = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (wt != null)
                typeId = wt.Id;
        }
        ElementId levelId = new ElementId(Convert.ToInt32(node["levelId"]));
   if (node.TryGetValue("BaseLevelName", out var lvlNameObj) && lvlNameObj is string lvlName)
        {
            var lvl = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name.Equals(lvlName, StringComparison.OrdinalIgnoreCase));
            if (lvl != null)
                levelId = lvl.Id;
        }
        double height = UnitConversion.ToFt(RoundValue(Convert.ToDouble(node["height_mm"])));        double offset = node.TryGetValue("base_offset_mm", out var offObj)
          ? UnitConversion.ToFt(RoundValue(Convert.ToDouble(offObj)))
          : 0;
        bool flip = node.TryGetValue("flipped", out var flipObj) && Convert.ToBoolean(flipObj);
        bool structural = node.TryGetValue("structural", out var structObj) && Convert.ToBoolean(structObj);

        Wall? wall = null;
        if (node.TryGetValue("uid", out var uidObj) && uidObj is string uid && !string.IsNullOrEmpty(uid))
        {
            wall = doc.GetElement(uid) as Wall;
            if (wall != null)
            {
                // Update existing wall
                var lc = wall.Location as LocationCurve;
                if (lc != null)
                    lc.Curve = line;
                if (wall.WallType.Id != typeId)
                    wall.ChangeTypeId(typeId);
                // Set flipped orientation
                if (wall.Flipped != flip)
                    wall.Flip();
                ParameterUtils.ApplyParameters(wall, node);
                ParameterUtils.SetNeo4jUid(wall, uid);
                return wall;
            }
        }

        // Create new wall if none exists
        wall = Wall.Create(doc, line, typeId, levelId, height, offset, flip, structural);
        ParameterUtils.ApplyParameters(wall, node);
        if (node.TryGetValue("uid", out uidObj) && uidObj is string newUid)
            ParameterUtils.SetNeo4jUid(wall, newUid);
        return wall;
    }
    // Baut ein Rohr nach.
    private static Pipe BuildPipe(Document doc, Dictionary<string, object> node)
    {
        XYZ s = new XYZ(UnitConversion.ToFt(Convert.ToDouble(node["x1"])),
                        UnitConversion.ToFt(Convert.ToDouble(node["y1"])),
                        UnitConversion.ToFt(Convert.ToDouble(node["z1"])));
        XYZ e = new XYZ(UnitConversion.ToFt(Convert.ToDouble(node["x2"])),
                        UnitConversion.ToFt(Convert.ToDouble(node["y2"])),
                        UnitConversion.ToFt(Convert.ToDouble(node["z2"])));
        ElementId pipeTypeId = new ElementId(Convert.ToInt32(node["typeId"]));
        ElementId systemTypeId = node.TryGetValue("systemTypeId", out var sysObj)
                   ? new ElementId(Convert.ToInt32(sysObj))
                   : ElementId.InvalidElementId;
        ElementId levelId = new ElementId(Convert.ToInt32(node["levelId"]));
        Pipe pipe = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, s, e);
        ParameterUtils.ApplyParameters(pipe, node);
        if (node.TryGetValue("uid", out var uidObj) && uidObj is string uid)
            ParameterUtils.SetNeo4jUid(pipe, uid);
        return pipe;
    }
    // Erstellt ein FamilyInstance-Element (z.B. Tür oder ProvisionalSpace).
    private static FamilyInstance BuildFamilyInstance(Document doc, Dictionary<string, object> node, Element? hostOverride = null)
    {
        ElementId typeId = new ElementId(Convert.ToInt32(node["typeId"]));
        FamilySymbol symbol = doc.GetElement(typeId) as FamilySymbol ?? throw new InvalidOperationException("Symbol not found");
        if (!symbol.IsActive)
            symbol.Activate();
        XYZ p = new XYZ(UnitConversion.ToFt(Convert.ToDouble(node.GetValueOrDefault("x", 0.0))),
                        UnitConversion.ToFt(Convert.ToDouble(node.GetValueOrDefault("y", 0.0))),
                        UnitConversion.ToFt(Convert.ToDouble(node.GetValueOrDefault("z", 0.0))));
        Level? level = doc.GetElement(new ElementId(Convert.ToInt32(node["levelId"]))) as Level;
        Element? host = hostOverride;
        if (host == null && node.TryGetValue("hostUid", out var hostUidObj) && hostUidObj is string hUid && !string.IsNullOrEmpty(hUid))
            host = doc.GetElement(hUid);
        if (host == null && node.TryGetValue("hostId", out var hostObj) && Convert.ToInt64(hostObj) > 0)
            host = doc.GetElement(new ElementId(Convert.ToInt32(hostObj)));
        FamilyInstance fi = host != null
? doc.Create.NewFamilyInstance(p, symbol, host, level, StructuralType.NonStructural)
: doc.Create.NewFamilyInstance(p, symbol, level, StructuralType.NonStructural);
        ParameterUtils.ApplyParameters(fi, node);
        return fi;
    }

    // Erstellt eine Tür aus den übertragenen Attributen.
    private static FamilyInstance BuildDoor(Document doc, Dictionary<string, object> node)
    {
        XYZ p = new XYZ(UnitConversion.ToFt(Convert.ToDouble(node.GetValueOrDefault("x", 0.0))),
                        UnitConversion.ToFt(Convert.ToDouble(node.GetValueOrDefault("y", 0.0))),
                        UnitConversion.ToFt(Convert.ToDouble(node.GetValueOrDefault("z", 0.0))));
        Element? host = null;
        if (node.TryGetValue("hostUid", out var hUidObj) && hUidObj is string hUid && !string.IsNullOrEmpty(hUid))
            host = doc.GetElement(hUid);
        if (host == null && node.TryGetValue("hostId", out var hIdObj) && Convert.ToInt64(hIdObj) > 0)
            host = doc.GetElement(new ElementId(Convert.ToInt32(hIdObj)));
        if (host == null)
            host = FindHostWall(doc, p);

        FamilyInstance fi = BuildFamilyInstance(doc, node, host);
        p = (fi.Location as LocationPoint)?.Point ?? p;
        if (node.TryGetValue("rotation", out var rotObj) && Math.Abs(Convert.ToDouble(rotObj)) > 1e-6)
        {
            var axis = Line.CreateBound(p, new XYZ(p.X, p.Y, p.Z + 1));
            ElementTransformUtils.RotateElement(doc, fi.Id, axis, Convert.ToDouble(rotObj));
            ParameterUtils.ApplyParameters(fi, node);

            if (node.TryGetValue("uid", out var uidObj) && uidObj is string uid)
                ParameterUtils.SetNeo4jUid(fi, uid);
        }
            return fi;
        }
    
    // Legt einen ProvisionalSpace im Modell an.
    private static FamilyInstance BuildProvisionalSpace(Document doc, Dictionary<string, object> node)
    {
        Logger.LogToFile("Start BuildProvisionalSpace", ProvLog);

        string guid = node.TryGetValue("guid", out var gObj) ? gObj.ToString() ?? string.Empty : string.Empty;
        Element? existing = !string.IsNullOrEmpty(guid) ? doc.GetElement(guid) : null;
        if (existing is FamilyInstance fiExisting)
        {
            Logger.LogToFile($"ProvisionalSpace {guid} already exists", ProvLog);
            return fiExisting;
        }

        string familyName = node.TryGetValue("familyName", out var famObj) ? famObj.ToString() ?? string.Empty : string.Empty;
        FamilySymbol? symbol = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault(fs => fs.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase));
        if (symbol == null)
        {
            Logger.LogToFile($"FamilySymbol '{familyName}' not found", ProvLog);
            throw new InvalidOperationException($"FamilySymbol '{familyName}' not found");
        }
        if (!symbol.IsActive)
            symbol.Activate();

        double minX = node.TryGetValue("bbMinX", out var minXObj) ? RoundValue(Convert.ToDouble(minXObj)) : 0.0;
        double minY = node.TryGetValue("bbMinY", out var minYObj) ? RoundValue(Convert.ToDouble(minYObj)) : 0.0;
        double minZ = node.TryGetValue("bbMinZ", out var minZObj) ? RoundValue(Convert.ToDouble(minZObj)) : 0.0;
        double maxX = node.TryGetValue("bbMaxX", out var maxXObj) ? RoundValue(Convert.ToDouble(maxXObj)) : 0.0;
        double maxY = node.TryGetValue("bbMaxY", out var maxYObj) ? RoundValue(Convert.ToDouble(maxYObj)) : 0.0;
        double maxZ = node.TryGetValue("bbMaxZ", out var maxZObj) ? RoundValue(Convert.ToDouble(maxZObj)) : 0.0;

        XYZ center = new XYZ(
            UnitConversion.ToFt((minX + maxX) / 2),
            UnitConversion.ToFt((minY + maxY) / 2),
            UnitConversion.ToFt((minZ + maxZ) / 2));

        Level? level = null;
        if (node.TryGetValue("levelId", out var lvlObj))
            level = doc.GetElement(new ElementId(Convert.ToInt32(lvlObj))) as Level;

        FamilyInstance inst = doc.Create.NewFamilyInstance(center, symbol, level, StructuralType.NonStructural);

        ParameterUtils.ApplyParameters(inst, node);
        if (!string.IsNullOrEmpty(guid))
            ParameterUtils.SetNeo4jUid(inst, guid);

        Logger.LogToFile($"Finished BuildProvisionalSpace {guid}", ProvLog);
        return inst;
    }
}