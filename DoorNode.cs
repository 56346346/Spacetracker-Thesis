using System.Runtime.Versioning;

namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public record DoorNode(
    string Uid,
    long ElementId,
    long TypeId,
    string FamilyName,
    string SymbolName,
    long LevelId,
    long HostId,
    string HostUid,
    double X,
    double Y,
    double Z,
    double Rotation,
    double Width,
    double Height,
    double Thickness
);