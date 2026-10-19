using ClayBeso.Geometry;
using ClayBeso.Printing;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class LayerTests
{
    private readonly ITestOutputHelper _out;
    public LayerTests(ITestOutputHelper output) => _out = output;

    private static TriMesh Cylinder(int around = 64, int up = 50, double r = 50, double height = 100) =>
        TestMeshes.Grid(around, up, (u, v) =>
            new Vec3(r * Math.Cos(2 * Math.PI * u), r * Math.Sin(2 * Math.PI * u), height * v), closedU: true);

    private static LayerField Field(TriMesh mesh, Func<Vec3, double> h, LayerMethod method)
    {
        var (bottom, top) = LayerField.ExtremeVertices(mesh, Vec3.UnitZ, 1e-6);
        var hv = mesh.Vertices.Select(h).ToArray();
        return LayerField.Compute(mesh, hv, bottom, top, Vec3.UnitZ, true, method);
    }

    [Theory]
    [InlineData(LayerMethod.Conforming)]
    [InlineData(LayerMethod.Eikonal)]
    public void UniformHeightGivesPlanarLayers(LayerMethod method)
    {
        var mesh = Cylinder();
        var field = Field(mesh, _ => 2.0, method);
        var paths = LayerSlicer.Slice(field, null, Vec3.UnitZ, new Vec3(1, 0, 0));
        _out.WriteLine($"{method}: {field.LayerCount} layers, {paths.Count} paths");

        Assert.Equal(50, field.LayerCount);
        Assert.All(paths, p => Assert.True(p.Closed));
        foreach (var p in paths.Where(p => p.Layer < 50))
        {
            var z = p.Points.Select(q => q.Z).ToArray();
            Assert.InRange(z.Max() - z.Min(), 0, 0.05);
            Assert.InRange(z.Average(), 2.0 * p.Layer - 0.05, 2.0 * p.Layer + 0.05);
        }
    }

    [Theory]
    [InlineData(LayerMethod.Conforming)]
    [InlineData(LayerMethod.Eikonal)]
    public void HeightGradedInZIsReproduced(LayerMethod method)
    {
        var mesh = Cylinder();
        var field = Field(mesh, p => 1.5 + 1.5 * p.Z / 100.0, method);
        var err = RelativeHeightError(field);
        _out.WriteLine($"{method}: {field.LayerCount} layers, mean |dh|/h = {err.mean:P2}, max {err.max:P2}");
        Assert.True(err.mean < 0.03);
    }

    [Fact]
    public void EikonalMatchesCircumferentialVariationLocally()
    {
        var mesh = Cylinder(96, 60);
        var field = Field(mesh, p => 2.5 + 0.5 * p.X / 50.0, LayerMethod.Eikonal);
        var err = RelativeHeightError(field);
        _out.WriteLine($"eikonal around: {field.LayerCount} layers, mean {err.mean:P2}, max {err.max:P2}");
        Assert.True(err.mean < 0.05);

        // Layers must actually be non-planar: thinner layers on the -X side.
        var paths = LayerSlicer.Slice(field, null, Vec3.UnitZ, new Vec3(1, 0, 0));
        var mid = paths.First(p => p.Layer == 20 && p.Closed);
        double zPlus = mid.Points.OrderByDescending(q => q.X).First().Z;
        double zMinus = mid.Points.OrderBy(q => q.X).First().Z;
        _out.WriteLine($"layer 20: z(+X) = {zPlus:F2}, z(-X) = {zMinus:F2}");
        Assert.True(zPlus > zMinus + 5);
    }

    [Fact]
    public void ConformingKeepsEveryLayerClosedEvenWhenTargetsConflict()
    {
        var mesh = Cylinder(96, 60);
        var field = Field(mesh, p => 2.5 + 0.5 * p.X / 50.0, LayerMethod.Conforming);
        var paths = LayerSlicer.Slice(field, null, Vec3.UnitZ, new Vec3(1, 0, 0));
        var err = RelativeHeightError(field);
        _out.WriteLine($"conforming around: {field.LayerCount} layers, {paths.Count} paths, mean err {err.mean:P2}, max {err.max:P2}");
        Assert.All(paths, p => Assert.True(p.Closed));
        Assert.Equal(field.LayerCount, paths.Count);
    }

    private static (double mean, double max) RelativeHeightError(LayerField field)
    {
        var real = field.RealisedFaceHeight();
        var mesh = field.Mesh;
        var errs = new List<double>();
        for (int f = 0; f < mesh.FaceCount; f++)
        {
            var t = mesh.Faces[f];
            if (t.Any(v => field.BaseVertices.Contains(v))) continue; // first ring is seeded
            double target = t.Average(v => field.TargetHeight[v]);
            errs.Add(Math.Abs(real[f] - target) / target);
        }
        return (errs.Average(), errs.Max());
    }
}
