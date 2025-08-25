using Autodesk.Revit.DB;
using System.Runtime.Versioning;

namespace SpaceTracker;

[SupportedOSPlatform("windows")]
// Kleine Hilfsklasse für Revit- und metrische Einheitenumrechnung mit Precision-Rundung.
public static class UnitConversion
{
    // Wandelt Fuß in Millimeter um.
    public static double ToMm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
    
    // Wandelt Millimeter in Revit-Fuß um mit Precision-Rundung.
    public static double ToFt(double mm) => 
        Math.Round(UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters), 10);
    
    // Wandelt Fuß in Meter um.
    public static double ToMeters(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
    
    // Wandelt Meter in Revit-Fuß um mit Precision-Rundung.
    public static double FromMeters(double meters) => 
        Math.Round(UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters), 10);
    
    // Präzise Conversion mit Logging
    public static double FromMetersWithLogging(double meters, string context)
    {
        double feetRaw = UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
        double feetRounded = Math.Round(feetRaw, 10);
        
        if (Math.Abs(feetRaw - feetRounded) > 1e-12)
        {
            // Logger.LogToFile($"UNIT PRECISION FIX {context}: {meters}m → {feetRaw}ft → {feetRounded}ft (rounded)", "sync.log");
        }
        
        return feetRounded;
    }
}