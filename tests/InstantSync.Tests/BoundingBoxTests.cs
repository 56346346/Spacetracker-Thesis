using Autodesk.Revit.DB;
using InstantSync.Core.Geometry;
using Xunit;

namespace InstantSync.Tests
{
    public class BoundingBoxTests
    {
        [Fact]
        public void GetDimensions_ReturnsExpectedValues()
        {
            var box = new BoundingBox(new XYZ(1, 2, 3), new XYZ(4, 6, 8));
            var (w, h, d) = box.GetDimensions();

            Assert.Equal(3, w);
            Assert.Equal(4, h);
            Assert.Equal(5, d);
        }
    }
}