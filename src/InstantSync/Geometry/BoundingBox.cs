using Autodesk.Revit.DB;

namespace InstantSync.Core.Geometry
{
    /// <summary>
    /// Represents a simple axis-aligned bounding box.
    /// </summary>
    public class BoundingBox
    {
        public BoundingBox(XYZ min, XYZ max)
        {
            Min = min;
            Max = max;
        }

        /// <summary>
        /// Gets the minimum corner of the box.
        /// </summary>
        public XYZ Min { get; }

        /// <summary>
        /// Gets the maximum corner of the box.
        /// </summary>
        public XYZ Max { get; }

        /// <summary>
        /// Calculates the width, height and depth of the box.
        /// </summary>
        /// <returns>Tuple containing width (X), height (Y) and depth (Z).</returns>
        public (double Width, double Height, double Depth) GetDimensions()
        {
            return (Max.X - Min.X, Max.Y - Min.Y, Max.Z - Min.Z);
        }
    }
}