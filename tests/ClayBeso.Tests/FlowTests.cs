using ClayBeso.Geometry;
using ClayBeso.Printing;

namespace ClayBeso.Tests;

public class FlowTests
{
    [Theory]
    [InlineData(4.0, 1.5)]
    [InlineData(6.0, 2.4)]
    [InlineData(3.0, 3.0)]
    public void BeadAreaAndWidthAreInverse(double w, double h)
    {
        double a = Bead.Area(w, h);
        Assert.Equal(w, Bead.Width(a, h), 9);
        // stadium is between the inscribed and the bounding rectangle
        Assert.InRange(a, (w - h) * h, w * h + 1e-12);
    }

    private static TriMesh Strip(out double[] t)
    {
        var mesh = TestMeshes.Grid(32, 10, (u, v) =>
            new Vec3(50 * Math.Cos(2 * Math.PI * u), 50 * Math.Sin(2 * Math.PI * u), 60 * v), closedU: true);
        t = Enumerable.Range(0, mesh.FaceCount).Select(f => 3.5 + 3.5 * mesh.FaceCentroid(f).Z / 60).ToArray();
        return mesh;
    }

    [Fact]
    public void ConstantBeadAreaMappingKeepsTheAreaWhereUnclamped()
    {
        var mesh = Strip(out var t);
        var h = LayerHeightField.Compute(mesh, t, 0.1, 10, LayerMapping.ConstantBeadArea, 8.0, 0, out double a);
        var tv = mesh.FaceToVertex(t);
        for (int v = 0; v < h.Length; v++)
            Assert.Equal(8.0, Bead.Area(tv[v], h[v]), 6);
    }

    [Fact]
    public void AspectRatioMappingScalesWithThickness()
    {
        var mesh = Strip(out var t);
        var h = LayerHeightField.Compute(mesh, t, 0.5, 5, LayerMapping.ConstantAspectRatio, 0, 0, out _, 0.4);
        var tv = mesh.FaceToVertex(t);
        for (int v = 0; v < h.Length; v++)
            Assert.Equal(0.4 * tv[v], h[v], 9);
    }

    [Fact]
    public void SlicedBeadAreaUsesWidthAndRealisedHeight()
    {
        var mesh = Strip(out var t);
        var tv = mesh.FaceToVertex(t);
        var h = LayerHeightField.Compute(mesh, t, 0.5, 5, LayerMapping.ConstantAspectRatio, 0, 2, out _, 0.4);
        var (bottom, top) = LayerField.ExtremeVertices(mesh, Vec3.UnitZ, 1e-6);
        var field = LayerField.Compute(mesh, h, bottom, top, Vec3.UnitZ, true, LayerMethod.Conforming);
        var paths = LayerSlicer.Slice(field, tv, Vec3.UnitZ, new Vec3(1, 0, 0));

        Assert.NotEmpty(paths);
        foreach (var p in paths)
        {
            Assert.Equal(p.Points.Count, p.BeadArea.Count);
            for (int i = 0; i < p.Points.Count; i++)
                Assert.Equal(Bead.Area(p.Width[i], p.RealisedHeight[i]), p.BeadArea[i], 9);
        }
        // Thicker wall higher up -> more clay per mm of path higher up.
        var low = paths.First(p => p.Layer == 2).BeadArea.Average();
        var high = paths.Last().BeadArea.Average();
        Assert.True(high > 1.5 * low, $"bead area {low:F2} -> {high:F2}");
    }
}
