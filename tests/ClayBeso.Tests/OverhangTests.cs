using ClayBeso.Geometry;
using ClayBeso.Printing;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class OverhangTests
{
    private readonly ITestOutputHelper _out;
    public OverhangTests(ITestOutputHelper output) => _out = output;

    private static List<LayerPath> Slice(TriMesh mesh, Func<Vec3, double> h, LayerMethod method, double width)
    {
        var (bottom, top) = LayerField.ExtremeVertices(mesh, Vec3.UnitZ, 1e-6);
        var field = LayerField.Compute(mesh, mesh.Vertices.Select(h).ToArray(), bottom, top, Vec3.UnitZ, true, method);
        return LayerSlicer.Slice(field, Enumerable.Repeat(width, mesh.VertexCount).ToArray(), Vec3.UnitZ, new Vec3(1, 0, 0));
    }

    [Fact]
    public void ConeLeanOffsetAndRatioMatchGeometry()
    {
        double lean = 30, tan = Math.Tan(lean * Math.PI / 180), height = 80;
        var cone = TestMeshes.Grid(96, 40, (u, v) =>
        {
            double z = height * v, r = 40 + z * tan;   // widening upwards: a 30 degree overhang
            return new Vec3(r * Math.Cos(2 * Math.PI * u), r * Math.Sin(2 * Math.PI * u), z);
        }, closedU: true);
        var paths = Slice(cone, _ => 2.0, LayerMethod.Conforming, 6.0);

        var mid = paths.Where(p => p.Layer > 5 && p.Layer < paths.Max(q => q.Layer) - 5).ToList();
        var res = mid.Select(p => OverhangCheck.Evaluate(p, Vec3.UnitZ, true)).ToList();
        double meanLean = res.SelectMany(r => r.Lean).Average();
        double meanTilt = res.SelectMany(r => r.Tilt).Average();
        double meanOffset = res.SelectMany(r => r.Offset).Average();
        double meanRatio = res.SelectMany(r => r.Ratio).Average();
        _out.WriteLine($"cone: lean {meanLean:F2} deg, tilt {meanTilt:F3} deg, offset {meanOffset:F3} mm (expected {2 * tan:F3}), ratio {meanRatio:F3}");

        Assert.InRange(meanLean, lean - 0.5, lean + 0.5);
        Assert.InRange(meanTilt, 0, 0.5);
        Assert.InRange(meanOffset, 2 * tan * 0.98, 2 * tan * 1.02);
        Assert.InRange(meanRatio, 2 * tan / 6 * 0.98, 2 * tan / 6 * 1.02);
    }

    [Fact]
    public void TiltedLayersOnAVerticalWallTiltButDoNotOverhang()
    {
        var cylinder = TestMeshes.Grid(96, 60, (u, v) =>
            new Vec3(50 * Math.Cos(2 * Math.PI * u), 50 * Math.Sin(2 * Math.PI * u), 100 * v), closedU: true);
        var paths = Slice(cylinder, p => 2.5 + 0.5 * p.X / 50.0, LayerMethod.Eikonal, 6.0);
        var res = paths.Where(p => p.Layer > 3 && p.Layer < 30).Select(p => OverhangCheck.Evaluate(p, Vec3.UnitZ, true)).ToList();
        double maxLean = res.SelectMany(r => r.Lean).Max();
        double maxTilt = res.SelectMany(r => r.Tilt).Max();
        _out.WriteLine($"tilted layers on a cylinder: max lean {maxLean:F2} deg, max tilt {maxTilt:F2} deg");
        Assert.InRange(maxLean, 0, 1.0);
        Assert.True(maxTilt > 5);
    }
}
