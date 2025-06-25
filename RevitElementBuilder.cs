using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Neo4j.Driver;
using System.Diagnostics;

namespace SpaceTracker;

#nullable enable

[SupportedOSPlatform("windows")]
public static class RevitElementBuilder
{
    public static Element BuildFromNode(Document doc, Dictionary<string, object> node)
    {
        if (!node.TryGetValue("rvtClass", out object? clsObj))
            throw new ArgumentException("Node is missing 'rvtClass'");

        string cls = clsObj.ToString() ?? string.Empty;
        return cls switch
        {
            "Wall" => BuildWall(doc, node),
            "Pipe" => BuildPipe(doc, node),
            "Door" => BuildFamilyInstance(doc, node),
            "ProvisionalSpace" => BuildFamilyInstance(doc, node),
            _ => throw new NotSupportedException($"Unsupported rvtClass {cls}")
        };
    }

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
        ElementId levelId = new ElementId(Convert.ToInt32(node["levelId"]));
        double height = UnitConversion.ToFt(Convert.ToDouble(node["height_mm"]));
        double offset = node.TryGetValue("base_offset_mm", out var offObj)
          ? UnitConversion.ToFt(Convert.ToDouble(offObj))
          : 0;
        bool flip = node.TryGetValue("flipped", out var flipObj) && Convert.ToBoolean(flipObj);
        bool structural = node.TryGetValue("structural", out var structObj) && Convert.ToBoolean(structObj);
        Wall wall = Wall.Create(doc, line, typeId, levelId, height, offset, flip, structural);
        if (node.TryGetValue("location_line", out var ll))
            wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set(Convert.ToInt32(ll));
        ParameterUtils.ApplyParameters(wall, node);
        return wall;
    }

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
        if (node.TryGetValue("diameter", out var d))
            pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(UnitConversion.ToFt(Convert.ToDouble(d)));
        ParameterUtils.ApplyParameters(pipe, node);
        return pipe;
    }

    private static FamilyInstance BuildFamilyInstance(Document doc, Dictionary<string, object> node)
    {
        ElementId typeId = new ElementId(Convert.ToInt32(node["typeId"]));
        FamilySymbol symbol = doc.GetElement(typeId) as FamilySymbol ?? throw new InvalidOperationException("Symbol not found");
        if (!symbol.IsActive)
            symbol.Activate();
        XYZ p = new XYZ(UnitConversion.ToFt(Convert.ToDouble(node.GetValueOrDefault("x", 0.0))),
                        UnitConversion.ToFt(Convert.ToDouble(node.GetValueOrDefault("y", 0.0))),
                        UnitConversion.ToFt(Convert.ToDouble(node.GetValueOrDefault("z", 0.0))));
        Level? level = doc.GetElement(new ElementId(Convert.ToInt32(node["levelId"]))) as Level;
        Element? host = null;
        if (node.TryGetValue("hostId", out var hostObj) && Convert.ToInt64(hostObj) > 0)
            host = doc.GetElement(new ElementId(Convert.ToInt32(hostObj)));
        FamilyInstance fi = host != null
            ? doc.Create.NewFamilyInstance(p, symbol, host, level, StructuralType.NonStructural)
            : doc.Create.NewFamilyInstance(p, symbol, level, StructuralType.NonStructural);
        ParameterUtils.ApplyParameters(fi, node);
        return fi;
    }
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
        if (existing is Wall wall)
        {
            var lc = wall.Location as LocationCurve;
            if (lc != null)
                lc.Curve = Line.CreateBound(s, e);
        }
        else
        {
            var line = Line.CreateBound(s, e);
            var newWall = Wall.Create(doc, line,
                new ElementId((long)node.Properties["typeId"].As<long>()),
                new ElementId((long)node.Properties["levelId"].As<long>()),
                UnitConversion.ToFt(node.Properties["height_mm"].As<double>()),
               UnitConversion.ToFt(node.Properties.TryGetValue("base_offset_mm", out var bo) ? bo.As<double>() : 0.0),
                node.Properties.TryGetValue("flipped", out var fl) && fl.As<bool>(),
                node.Properties.TryGetValue("structural", out var st) && st.As<bool>());
            Parameter llp = newWall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
            if (llp != null && !llp.IsReadOnly && node.Properties.TryGetValue("location_line", out var llv))
                llp.Set(llv.As<int>());
        }
    }
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
        }
    }

    private static void BuildProvisionalSpace(Document doc, INode node)
    {
        string guid = node.Properties.TryGetValue("guid", out var guidObj)
                   ? guidObj.As<string>()
                   : string.Empty;
        Element? existing = !string.IsNullOrEmpty(guid) ? doc.GetElement(guid) : null;
        if (existing != null)
            return;

        string familyName = node.Properties.TryGetValue("familyName", out var famObj)
                   ? famObj.As<string>()
                   : string.Empty;
        FamilySymbol? symbol = new FilteredElementCollector(doc)
.OfClass(typeof(FamilySymbol))
.Cast<FamilySymbol>()
.FirstOrDefault(fs => fs.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase));
        if (symbol == null)
        {
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

    }
}