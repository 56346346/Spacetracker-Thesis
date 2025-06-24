using System.Runtime.Versioning;

namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public class ProvisionalSpaceDto
{
    public string Guid { get; set; }
    public string Name { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Thickness { get; set; }
    public string Level { get; set; }
    public int RevitId { get; set; }
    public string IfcType { get; set; } = "IfcOpeningElement";
}