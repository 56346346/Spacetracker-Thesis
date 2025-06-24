using System.Runtime.Versioning;

namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public record ProvisionalSpaceNode(
    string Guid,
    string Name,
    double Width,
    double Height,
    double Thickness,
    string Level,
    int RevitId,
    string IfcType
);