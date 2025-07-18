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
    // Interne Variante für Node-Objekte.
    private static void BuildWall(Document doc, INode node)
    {
        string uid = node.Properties.TryGetValue("uid", out var uidObj)
                    ? uidObj.As<string>()
                    : string.Empty;
        Element? existing = !string.IsNullOrEmpty(uid) ? doc.GetElement(uid) : null;
        XYZ s = new XYZ(UnitConversion.ToFt(node.Properties["x1"].As<double>()),
                        UnitConversion.ToFt(node.Properties["y1"].As<double>()),
                        UnitConversion.ToFt(node.Properties["z1"].As<double>()));
        XYZ e = new XYZ(UnitConversion.ToFt(node.Properties["x2"].As<double>()),
                        UnitConversion.ToFt(node.Properties["y2"].As<double>()),
                        UnitConversion.ToFt(node.Properties["z2"].As<double>()));
        ElementId typeId = new ElementId((long)node.Properties["typeId"].As<long>());
        ElementId levelId = new ElementId((long)node.Properties["levelId"].As<long>());
        double height = UnitConversion.ToFt(node.Properties["height_mm"].As<double>());
        double offset = UnitConversion.ToFt(node.Properties.TryGetValue("base_offset_mm", out var bo) ? bo.As<double>() : 0.0);
        bool flip = node.Properties.TryGetValue("flipped", out var flVal) && flVal.As<bool>();
        bool structural = node.Properties.TryGetValue("structural", out var stVal) && stVal.As<bool>();
        if (existing is Wall wall)
        {
            var lc = wall.Location as LocationCurve;
            if (lc != null)
                lc.Curve = Line.CreateBound(s, e);
            if (wall.WallType.Id != typeId)
                wall.ChangeTypeId(typeId);
            wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(UnitConversion.ToFt(node.Properties["height_mm"].As<double>()));
            wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.Set(offset);
            if (wall.Flipped != (node.Properties.TryGetValue("flipped", out var fl) && fl.As<bool>()))
                wall.Flip();
            wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.Set(node.Properties.TryGetValue("structural", out var st) && st.As<bool>() ? 1 : 0);
            if (node.Properties.TryGetValue("location_line", out var llv))
                wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set(llv.As<int>());
            ParameterUtils.ApplyParameters(wall, node.Properties.ToDictionary(k => k.Key, k => (object)k.Value));
            ParameterUtils.SetNeo4jUid(wall, uid);

        }
        else
        {
            var line = Line.CreateBound(s, e);
            var newWall = Wall.Create(doc, line,
               typeId,
                levelId,
                height,
                offset,
                flip,
                structural);
            Parameter llp = newWall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
            if (llp != null && !llp.IsReadOnly && node.Properties.TryGetValue("location_line", out var llv))
                llp.Set(llv.As<int>());

            ParameterUtils.ApplyParameters(newWall, node.Properties.ToDictionary(k => k.Key, k => (object)k.Value));
            ParameterUtils.SetNeo4jUid(newWall, uid); ParameterUtils.SetNeo4jUid(newWall, uid);
        }
    }
    // Erkennt den Knotentyp und ruft den passenden Builder auf.
    public static void BuildFromNodes(Document doc, INode node)
    {
        if (node == null) return;
        try
        {
            if (node.Labels.Contains("Wall"))
            {
                BuildWall(doc, node);
            }
            else if (node.Labels.Contains("Pipe"))
            {
                BuildPipe(doc, node);
            }
            else if (node.Labels.Contains("Door"))
            {
                BuildDoor(doc, node);
            }
            else if (node.Labels.Contains("ProvisionalSpace"))
            {
                BuildProvisionalSpace(doc, node);
            }
            else
            {
                Debug.WriteLine($"Unsupported node type: {string.Join(',', node.Labels)}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BuildFromNode Error] {ex.Message}");
        }
    }
    // Erstellt bzw. aktualisiert ein Rohr anhand eines Neo4j-Knotens.
    private static void BuildPipe(Document doc, INode node)
    {
        string uid = node.Properties.TryGetValue("uid", out var uidProp)
                  ? uidProp.As<string>()
                  : string.Empty;
        Element? existing = !string.IsNullOrEmpty(uid) ? doc.GetElement(uid) : null;
        XYZ s = new XYZ(UnitConversion.ToFt(node.Properties["x1"].As<double>()),
                        UnitConversion.ToFt(node.Properties["y1"].As<double>()),
                        UnitConversion.ToFt(node.Properties["z1"].As<double>()));
        XYZ e = new XYZ(UnitConversion.ToFt(node.Properties["x2"].As<double>()),
                        UnitConversion.ToFt(node.Properties["y2"].As<double>()),
                        UnitConversion.ToFt(node.Properties["z2"].As<double>()));
        if (existing is MEPCurve pipe)
        {
            var lc = pipe.Location as LocationCurve;
            if (lc != null)
                lc.Curve = Line.CreateBound(s, e);
            ParameterUtils.SetNeo4jUid(pipe, uid);

        }
        else
        {
            double diam = UnitConversion.ToFt(node.Properties.TryGetValue("diameter", out var dval) ? dval.As<double>() : 0.0); var line = Line.CreateBound(s, e);
            ElementId systemTypeId = node.Properties.ContainsKey("systemTypeId")
                ? new ElementId((long)node.Properties["systemTypeId"].As<long>())
                : ElementId.InvalidElementId;
            Pipe newPipe = Pipe.Create(doc,
             systemTypeId,
                doc.GetDefaultElementTypeId(ElementTypeGroup.PipeType),
                new ElementId((long)node.Properties["levelId"].As<long>()),
                line.GetEndPoint(0), line.GetEndPoint(1));
            newPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diam);
            ParameterUtils.SetNeo4jUid(newPipe, uid);

        }
    }

    // Erstellt eine Tür anhand eines Neo4j-Knotens.
    private static void BuildDoor(Document doc, INode node)
    {
        string uid = node.Properties.TryGetValue("uid", out var uidProp) ? uidProp.As<string>() : string.Empty;
        Element? existing = !string.IsNullOrEmpty(uid) ? doc.GetElement(uid) : null;
        if (existing != null) return;

        FamilySymbol? symbol = doc.GetElement(new ElementId((long)node.Properties["typeId"].As<long>())) as FamilySymbol;
        if (symbol == null) return;
        if (!symbol.IsActive) symbol.Activate();

        double x = node.Properties.TryGetValue("x", out var xObj) ? xObj.As<double>() : 0.0;
        double y = node.Properties.TryGetValue("y", out var yObj) ? yObj.As<double>() : 0.0;
        double z = node.Properties.TryGetValue("z", out var zObj) ? zObj.As<double>() : 0.0;
        XYZ p = new XYZ(UnitConversion.ToFt(x), UnitConversion.ToFt(y), UnitConversion.ToFt(z));
        Level? level = doc.GetElement(new ElementId((long)node.Properties["levelId"].As<long>())) as Level;
        Element? host = null;
        if (node.Properties.TryGetValue("hostUid", out var hostUidProp))
        {
            string hUid = hostUidProp.As<string>();
            if (!string.IsNullOrEmpty(hUid))
                host = doc.GetElement(hUid);
        }
        if (host == null && node.Properties.TryGetValue("hostId", out var hostObj) && hostObj.As<long>() > 0)
            host = doc.GetElement(new ElementId((int)hostObj.As<long>()));
        if (host == null)
            host = FindHostWall(doc, p);
        FamilyInstance fi = host != null
            ? doc.Create.NewFamilyInstance(p, symbol, host, level, StructuralType.NonStructural)
            : doc.Create.NewFamilyInstance(p, symbol, level, StructuralType.NonStructural);

        if (node.Properties.TryGetValue("rotation", out var rotObj) && Math.Abs(rotObj.As<double>()) > 1e-6)
        {
            var axis = Line.CreateBound(p, new XYZ(p.X, p.Y, p.Z + 1));
            ElementTransformUtils.RotateElement(doc, fi.Id, axis, rotObj.As<double>());
        }

        if (node.Properties.TryGetValue("width", out var width))
            fi.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.Set(UnitConversion.ToFt(width.As<double>()));
        if (node.Properties.TryGetValue("height", out var height))
            fi.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.Set(UnitConversion.ToFt(height.As<double>()));

        var dict = node.Properties.ToDictionary(k => k.Key, k => (object)k.Value);
        ParameterUtils.ApplyParameters(fi, dict);
        ParameterUtils.SetNeo4jUid(fi, uid);

    }
    // Legt einen ProvisionalSpace im Modell an.
    private static void BuildProvisionalSpace(Document doc, INode node)
    {
        Logger.LogToFile("Start BuildProvisionalSpace", ProvLog);

        string guid = node.Properties.TryGetValue("guid", out var guidObj)
                   ? guidObj.As<string>()
                   : string.Empty;
        Element? existing = !string.IsNullOrEmpty(guid) ? doc.GetElement(guid) : null;
        if (existing != null)
        {
            Logger.LogToFile($"ProvisionalSpace {guid} already exists", ProvLog);
            return;
        }
        string familyName = node.Properties.TryGetValue("familyName", out var famObj)
                   ? famObj.As<string>()
                   : string.Empty;
        FamilySymbol? symbol = new FilteredElementCollector(doc)
.OfClass(typeof(FamilySymbol))
.Cast<FamilySymbol>()
.FirstOrDefault(fs => fs.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase));
        if (symbol == null)
        {
            Logger.LogToFile($"FamilySymbol '{familyName}' not found", ProvLog);
            Debug.WriteLine($"FamilySymbol '{familyName}' not found.");
            return;
        }
        if (!symbol.IsActive)
            symbol.Activate();

        double minX = node.Properties.TryGetValue("bbMinX", out var bbMinX) ? bbMinX.As<double>() : 0.0;
        double minY = node.Properties.TryGetValue("bbMinY", out var bbMinY) ? bbMinY.As<double>() : 0.0;
        double minZ = node.Properties.TryGetValue("bbMinZ", out var bbMinZ) ? bbMinZ.As<double>() : 0.0;
        double maxX = node.Properties.TryGetValue("bbMaxX", out var bbMaxX) ? bbMaxX.As<double>() : 0.0;
        double maxY = node.Properties.TryGetValue("bbMaxY", out var bbMaxY) ? bbMaxY.As<double>() : 0.0;
        double maxZ = node.Properties.TryGetValue("bbMaxZ", out var bbMaxZ) ? bbMaxZ.As<double>() : 0.0;
        XYZ center = new XYZ(
            UnitConversion.ToFt((minX + maxX) / 2),
            UnitConversion.ToFt((minY + maxY) / 2),
            UnitConversion.ToFt((minZ + maxZ) / 2));

        ElementId levelId = node.Properties.TryGetValue("levelId", out var lvl) ? new ElementId((long)lvl.As<long>()) : ElementId.InvalidElementId; Level? level = levelId != ElementId.InvalidElementId ? doc.GetElement(levelId) as Level : null;

        FamilyInstance inst = doc.Create.NewFamilyInstance(center, symbol, level, StructuralType.NonStructural);
        Logger.LogToFile($"Created provisional space instance {guid}", ProvLog);
        int phaseCreated = node.Properties.TryGetValue("phaseCreated", out var pc) ? pc.As<int>() : -1;
        int phaseDemolished = node.Properties.TryGetValue("phaseDemolished", out var pd) ? pd.As<int>() : -1;
        inst.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phaseCreated);
        inst.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.Set(phaseDemolished);

        double widthFt = UnitConversion.ToFt(maxX - minX);
        double depthFt = UnitConversion.ToFt(maxY - minY);
        double heightFt = UnitConversion.ToFt(maxZ - minZ);

        inst.LookupParameter("Width")?.Set(widthFt);
        inst.LookupParameter("Depth")?.Set(depthFt);
        inst.LookupParameter("Height")?.Set(heightFt);

        if (node.Properties.TryGetValue("ifcType", out var ifcObj))
        {
            var p = inst.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT);
            if (p != null && !p.IsReadOnly)
                p.Set(ifcObj.As<string>());
        }

        var paramDict = node.Properties.ToDictionary(k => k.Key, k => (object)k.Value);
        ParameterUtils.ApplyParameters(inst, paramDict);
        Logger.LogToFile($"Finished BuildProvisionalSpace {guid}", ProvLog);

    }
}