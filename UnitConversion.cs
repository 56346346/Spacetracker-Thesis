using Autodesk.Revit.DB;
using System.Runtime.Versioning;

namespace SpaceTracker;

[SupportedOSPlatform("windows")]
// Kleine Hilfsklasse für Revit- und metrische Einheitenumrechnung.
public static class UnitConversion
{
        // Wandelt Fuß in Millimeter um.
    public static double ToMm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
       // Wandelt Millimeter in Revit-Fuß um.
    public static double ToFt(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}