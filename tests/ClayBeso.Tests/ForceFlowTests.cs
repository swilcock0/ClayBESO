using ClayBeso.Fem;
using ClayBeso.Geometry;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class ForceFlowTests
{
    private readonly ITestOutputHelper _out;
    public ForceFlowTests(ITestOutputHelper output) => _out = output;

    internal const double R = 50, H = 120;

    /// <summary>Clamped cylinder under self-weight (6 mm wall).</summary>
    internal static ShellModel Cylinder(int around = 64, int up = 48, double lateral = 0)
    {
        var mesh = TestMeshes.Grid(around, up, (u, v) =>
            new Vec3(R * Math.Cos(2 * Math.PI * u), R * Math.Sin(2 * Math.PI * u), H * v), closedU: true);
        int n = mesh.VertexCount;
        var fix = new bool[6 * n];
        var load = new double[6 * n];
        for (int i = 0; i < n; i++)
        {
            var p = mesh.Vertices[i];
            if (p.Z < 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
            if (p.Z > H - 1e-9 && p.X > R - 1e-9) load[6 * i] = lateral;
        }
        return new ShellModel(mesh, new ClayMaterial(1.0, 0.35, 1900), new[] { 6.0 }, fix, load, new Vec3(0, 0, -1));
    }

    [Fact]
    public void PrincipalDecompositionIsCorrect()
    {
        ForceFlow.Principal(3, -1, 2, out double n1, out double n2, out double c, out double s);
        // eigenvalues of [[3,2],[2,-1]]: 1 +- sqrt(8)
        Assert.Equal(1 + Math.Sqrt(8), n1, 12);
        Assert.Equal(1 - Math.Sqrt(8), n2, 12);
        // (c, s) is the eigenvector of n1: A v = n1 v
        Assert.Equal(n1 * c, 3 * c + 2 * s, 12);
        Assert.Equal(n1 * s, 2 * c - 1 * s, 12);
    }

    [Fact]
    public void SelfWeightFlowsVerticallyAndPlanarLayersAreSquareToIt()
    {
        var model = Cylinder();
        var s = ElementScaling.FromThickness(model.Thickness);
        var r = new ShellSolver(model).Solve(s);
        var phi = model.Mesh.Vertices.Select(p => p.Z / 2.0).ToArray(); // planar 2 mm layers
        var check = InterlayerCheck.Compute(model, r, s, phi, Vec3.UnitZ, true, 0.01, new BondModel());

        var mid = Enumerable.Range(0, model.ElementCount)
            .Where(e => Math.Abs(model.Mesh.FaceCentroid(e).Z - H / 2) < 10).ToArray();
        _out.WriteLine($"mid-height: max flow angle {mid.Max(e => check.FlowAngle[e]):F3} deg, " +
                       $"dominant force {mid.Average(e => check.Flow.Dominant[e]):E3} N/mm");
        Assert.All(mid, e => Assert.True(Math.Abs(Vec3.Dot(check.Flow.Direction[e], Vec3.UnitZ)) > 0.999));
        Assert.All(mid, e => Assert.True(check.Flow.Dominant[e] < 0, "self-weight is compression"));
        Assert.All(mid, e => Assert.InRange(check.FlowAngle[e], 0, 2));
    }

    [Fact]
    public void TiltedLayersShowTheirTiltAsInterfaceAngle()
    {
        var model = Cylinder();
        var s = ElementScaling.FromThickness(model.Thickness);
        var r = new ShellSolver(model).Solve(s);
        // layers tilted in x: on the faces facing +y the layer tangent is x, so they lean by atan(0.4)
        var phi = model.Mesh.Vertices.Select(p => (p.Z + 0.4 * p.X) / 2.0).ToArray();
        var check = InterlayerCheck.Compute(model, r, s, phi, Vec3.UnitZ, false, 0.01, new BondModel());

        int[] Near(double x, double y) => Enumerable.Range(0, model.ElementCount).Where(e =>
        {
            var c = model.Mesh.FaceCentroid(e);
            return (new Vec3(c.X, c.Y, 0) - new Vec3(x, y, 0)).Length < 6 && Math.Abs(c.Z - H / 2) < 10;
        }).ToArray();

        double side = Near(0, R).Average(e => check.FlowAngle[e]);
        double front = Near(R, 0).Average(e => check.FlowAngle[e]);
        double expected = Math.Atan(0.4) * 180 / Math.PI;
        _out.WriteLine($"interface angle: facing +y {side:F2} deg (expected {expected:F2}), facing +x {front:F2} deg (expected 0)");
        Assert.InRange(side, expected - 1.5, expected + 1.5);
        Assert.InRange(front, 0, 1.5);
    }
}

public class ForceAlignedLayerTests
{
    private readonly ITestOutputHelper _out;
    public ForceAlignedLayerTests(ITestOutputHelper output) => _out = output;

    private static (ShellModel model, AnalysisResult r, ElementScaling s, Vec3[] flow) Analyse(double lateral)
    {
        var model = ForceFlowTests.Cylinder(64, 48, lateral);
        var s = ElementScaling.FromThickness(model.Thickness);
        var r = new ShellSolver(model).Solve(s);
        var ff = ForceFlow.Compute(model, r, s);
        var flow = ff.Direction.Select((d, e) => d * ff.Anisotropy[e]).ToArray();
        return (model, r, s, flow);
    }

    private static Printing.LayerField Layers(ShellModel model, Vec3[] flow, Printing.LayerMethod method, double maxDev = 30)
    {
        var mesh = model.Mesh;
        var (bottom, top) = Printing.LayerField.ExtremeVertices(mesh, Vec3.UnitZ, 1e-6);
        return Printing.LayerField.Compute(mesh, Enumerable.Repeat(2.0, mesh.VertexCount).ToArray(), bottom, top,
            Vec3.UnitZ, true, method, flow, maxDev);
    }

    [Fact]
    public void VerticalSelfWeightFlowKeepsLayersPlanar()
    {
        var (model, _, _, flow) = Analyse(0);
        var field = Layers(model, flow, Printing.LayerMethod.ForceAligned);
        var paths = Printing.LayerSlicer.Slice(field, null, Vec3.UnitZ, new Vec3(1, 0, 0));
        double spread = paths.Where(p => p.Layer > 2 && p.Layer < field.LayerCount - 2)
            .Max(p => p.Points.Max(q => q.Z) - p.Points.Min(q => q.Z));
        _out.WriteLine($"self-weight, force-aligned: {field.LayerCount} layers, max in-layer z spread {spread:F3} mm");
        Assert.Equal(60, field.LayerCount);
        Assert.InRange(spread, 0, 0.5);
    }

    [Fact]
    public void LayersTurnAcrossTheForceUnderLateralLoad()
    {
        var (model, r, s, flow) = Analyse(lateral: 40);
        double MeanAngle(Printing.LayerField f)
        {
            var c = InterlayerCheck.Compute(model, r, s, f.Phi, Vec3.UnitZ, true, 0.01, new BondModel());
            var w = Enumerable.Range(0, model.ElementCount).Where(c.IsValid).ToArray();
            return w.Sum(e => c.FlowAngle[e] * Math.Abs(c.Flow.Dominant[e])) / w.Sum(e => Math.Abs(c.Flow.Dominant[e]));
        }
        var conforming = Layers(model, flow, Printing.LayerMethod.Conforming);
        var aligned = Layers(model, flow, Printing.LayerMethod.ForceAligned);
        double a0 = MeanAngle(conforming), a1 = MeanAngle(aligned);
        var paths = Printing.LayerSlicer.Slice(aligned, null, Vec3.UnitZ, new Vec3(1, 0, 0));
        _out.WriteLine($"lateral load: force-weighted interface angle conforming {a0:F2} deg -> force-aligned {a1:F2} deg; " +
                       $"{aligned.LayerCount} layers, {paths.Count(p => !p.Closed)} open");
        Assert.True(a1 < 0.8 * a0, "force-aligned layers should sit more squarely across the force");
        Assert.All(paths, p => Assert.True(p.Closed));
    }
}
