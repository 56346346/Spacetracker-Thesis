using Autodesk.Revit.DB;

namespace SpaceTracker;
// Erweiterungsmethoden für Revit-Wände.
public static class WallExtensions
{
        // Gibt true zurück, wenn die Wand als tragend markiert ist.
    public static bool Structural(this Wall wall)
    {
        var param = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_USAGE_PARAM);
        if (param != null)
        {
            return param.AsInteger() != 0;
        }
        param = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
        return param?.AsInteger() != 0;
    }
}