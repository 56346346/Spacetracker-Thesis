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
        double height = UnitConversion.ToFt(Convert.ToDouble(node["h"]));
        double offset = node.ContainsKey("bo") ? UnitConversion.ToFt(Convert.ToDouble(node["bo"])) : 0;
        bool flip = node.ContainsKey("flip") && Convert.ToBoolean(node["flip"]);
 bool structural = node.ContainsKey("structural") && Convert.ToBoolean(node["structural"]);
        Wall wall = Wall.Create(doc, line, typeId, levelId, height, offset, flip, structural);        if (node.TryGetValue("locLine", out var ll))
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
        ElementId systemTypeId = node.ContainsKey("systemTypeId") ? new ElementId(Convert.ToInt32(node["systemTypeId"])) : ElementId.InvalidElementId;
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
        string uid = node.Properties.ContainsKey("uid") ? node.Properties["uid"].As<string>() : string.Empty;
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
                UnitConversion.ToFt(node.Properties["base_offset_mm"].As<double>()),
                node.Properties.ContainsKey("flipped") && node.Properties["flipped"].As<bool>(),
                node.Properties.ContainsKey("structural") && node.Properties["structural"].As<bool>());
            Parameter llp = newWall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
            if (llp != null && !llp.IsReadOnly && node.Properties.ContainsKey("location_line"))
                llp.Set(node.Properties["location_line"].As<int>());
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
                // placeholder - not implemented
                Debug.WriteLine("ProvisionalSpace build not implemented.");
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
        string uid = node.Properties.ContainsKey("uid") ? node.Properties["uid"].As<string>() : string.Empty;
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
            double diam = UnitConversion.ToFt(node.Properties["diameter"].As<double>());
            var line = Line.CreateBound(s, e);
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
}