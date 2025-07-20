using System.Collections.Generic;

namespace SpaceTracker;

public static class NodeExtensions
{
    public static Dictionary<string, object> ToDictionary(this WallNode w) => new()
    {
        ["rvtClass"] = "Wall",
        ["uid"] = w.Uid,
        ["elementId"] = w.ElementId,
        ["typeId"] = w.TypeId,
        ["typeName"] = w.TypeName,
        ["familyName"] = w.FamilyName,
        ["levelId"] = w.LevelId,
        ["x1"] = w.X1,
        ["y1"] = w.Y1,
        ["z1"] = w.Z1,
        ["x2"] = w.X2,
        ["y2"] = w.Y2,
        ["z2"] = w.Z2,
        ["height_mm"] = w.HeightMm,
        ["thickness_mm"] = w.ThicknessMm,
        ["structural"] = w.Structural,
        ["flipped"] = w.Flipped,
        ["base_offset_mm"] = w.BaseOffsetMm,
        ["location_line"] = w.LocationLine
    };

    public static Dictionary<string, object> ToDictionary(this DoorNode d) => new()
    {
        ["rvtClass"] = "Door",
        ["uid"] = d.Uid,
        ["elementId"] = d.ElementId,
        ["typeId"] = d.TypeId,
        ["familyName"] = d.FamilyName,
        ["symbolName"] = d.SymbolName,
        ["levelId"] = d.LevelId,
        ["hostId"] = d.HostId,
        ["hostUid"] = d.HostUid,
        ["x"] = d.X,
        ["y"] = d.Y,
        ["z"] = d.Z,
        ["rotation"] = d.Rotation,
        ["width"] = d.Width,
        ["height"] = d.Height,
        ["thickness"] = d.Thickness
    };

    public static Dictionary<string, object> ToDictionary(this PipeNode p) => new()
    {
        ["rvtClass"] = "Pipe",
        ["uid"] = p.Uid,
        ["elementId"] = p.ElementId,
        ["typeId"] = p.TypeId,
        ["systemTypeId"] = p.SystemTypeId,
        ["levelId"] = p.LevelId,
        ["x1"] = p.X1,
        ["y1"] = p.Y1,
        ["z1"] = p.Z1,
        ["x2"] = p.X2,
        ["y2"] = p.Y2,
        ["z2"] = p.Z2,
        ["diameter"] = p.Diameter
    };

    public static Dictionary<string, object> ToDictionary(this ProvisionalSpaceNode ps) => new()
    {
        ["rvtClass"] = "ProvisionalSpace",
        ["guid"] = ps.Guid,
        ["name"] = ps.Name,
        ["familyName"] = ps.FamilyName,
        ["symbolName"] = ps.SymbolName,
        ["width"] = ps.Width,
        ["height"] = ps.Height,
        ["thickness"] = ps.Thickness,
        ["level"] = ps.Level,
        ["levelId"] = ps.LevelId,
        ["x"] = ps.X,
        ["y"] = ps.Y,
        ["z"] = ps.Z,
        ["rotation"] = ps.Rotation,
        ["hostId"] = ps.HostId,
        ["revitId"] = ps.RevitId,
        ["ifcType"] = ps.IfcType,
        ["category"] = ps.Category,
        ["phaseCreated"] = ps.PhaseCreated,
        ["phaseDemolished"] = ps.PhaseDemolished,
        ["bbMinX"] = ps.BbMinX,
        ["bbMinY"] = ps.BbMinY,
        ["bbMinZ"] = ps.BbMinZ,
        ["bbMaxX"] = ps.BbMaxX,
        ["bbMaxY"] = ps.BbMaxY,
        ["bbMaxZ"] = ps.BbMaxZ
    };
}