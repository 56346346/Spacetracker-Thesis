using System.Runtime.Versioning;

namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public record ProvisionalSpaceNode(
    string Guid,
    string Name,
    string FamilyName,
    string SymbolName,
    double Width,
    double Height,
    double Thickness,
    string Level,
    long LevelId,
    double X,
    double Y,
    double Z,
    double Rotation,
    long HostId,
    int RevitId,
    string IfcType,
    string? Category,
    int PhaseCreated,
    int PhaseDemolished,
    double BbMinX,
    double BbMinY,
    double BbMinZ,
    double BbMaxX,
    double BbMaxY,
double BbMaxZ,
    string RvtClass = "ProvisionalSpace");