using Autodesk.Revit.DB;

namespace SpaceTracker;

public static class WallExtensions
{
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