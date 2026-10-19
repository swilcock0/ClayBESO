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
    public void ProportionalMappingRaisesLayerHeightWithThickness()
    {
        var mesh = Strip(out var t); // 3.5 mm at the bottom to 7 mm at the top
        var tv = mesh.FaceToVertex(t);
        var h = LayerHeightField.Compute(mesh, t, 1.0, 2.5, LayerMapping.Proportional, 0, 0, out _);

        int thin = Array.IndexOf(tv, tv.Min()), thick = Array.IndexOf(tv, tv.Max());
        Assert.Equal(1.0, h[thin], 9);
        Assert.Equal(2.5, h[thick], 9);
        for (int v = 0; v < h.Length; v++)
            Assert.Equal(1.0 + 1.5 * (tv[v] - tv.Min()) / (tv.Max() - tv.Min()), h[v], 9);
    }

    [Fact]
    public void ThicknessRangeAnchorsTheLinearMappings()
    {
        var mesh = Strip(out _);
        var nearlyUniform = Enumerable.Range(0, mesh.FaceCount).Select(f => 5.0 + 0.01 * (f % 2)).ToArray();

        // Without a range a 0.01 mm variation would use the whole height range...
        var free = LayerHeightField.Compute(mesh, nearlyUniform, 1.0, 2.5, LayerMapping.Proportional, 0, 0, out _);
        Assert.True(free.Max() - free.Min() > 1.0);

        // ...anchored to 3.5-7 mm it stays near the middle.
        var anchored = LayerHeightField.Compute(mesh, nearlyUniform, 1.0, 2.5, LayerMapping.Proportional, 0, 0, out _,
            thicknessRange: (3.5, 7.0));
        Assert.All(anchored, x => Assert.InRange(x, 1.0 + 1.5 * 1.5 / 3.5 - 1e-9, 1.0 + 1.5 * 1.51 / 3.5 + 1e-9));
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
