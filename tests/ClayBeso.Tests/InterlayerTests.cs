using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Printing;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class InterlayerTests
{
    private readonly ITestOutputHelper _out;
    public InterlayerTests(ITestOutputHelper output) => _out = output;

    private const double Radius = 50, Height = 120;

    /// <summary>Clamped cylinder, self-weight, optional lateral load at the rim, planar layers of height h.</summary>
    private static (ShellModel model, AnalysisResult r, ElementScaling s, double[] phi) Cylinder(
        double t, double h, double lateral = 0)
    {
        var mesh = TestMeshes.Grid(64, 48, (u, v) =>
            new Vec3(Radius * Math.Cos(2 * Math.PI * u), Radius * Math.Sin(2 * Math.PI * u), Height * v), closedU: true);
        int n = mesh.VertexCount;
        var fix = new bool[6 * n];
        var load = new double[6 * n];
        for (int i = 0; i < n; i++)
        {
            var p = mesh.Vertices[i];
            if (p.Z < 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
            if (p.Z > Height - 1e-9 && p.X > Radius - 1e-9) load[6 * i] = lateral;
        }
        var model = new ShellModel(mesh, new ClayMaterial(1.0, 0.35, 1900), new[] { t }, fix, load, new Vec3(0, 0, -1));
        var s = ElementScaling.FromThickness(model.Thickness);
        var r = new ShellSolver(model).Solve(s);

        var (bottom, top) = LayerField.ExtremeVertices(mesh, Vec3.UnitZ, 1e-6);
        var field = LayerField.Compute(mesh, Enumerable.Repeat(h, n).ToArray(), bottom, top, Vec3.UnitZ, true, LayerMethod.Conforming);
        return (model, r, s, field.Phi);
    }

    [Theory]
    [InlineData(6, 2, 2.0 / 3.0)]
    [InlineData(6, 0, 1.0)]
    [InlineData(4, 4, 0.05)]  // round bead: clamped at the minimum
    public void ContactFractionFollowsBeadGeometry(double w, double h, double expected)
    {
        Assert.Equal(expected, new BondModel().Contact(w, h), 12);
    }

    [Fact]
    public void SelfWeightGivesBearingStressOfTheWeightAboveOverTheContactStrip()
    {
        double t = 6, h = 2;
        var (model, r, s, phi) = Cylinder(t, h);
        var check = InterlayerCheck.Compute(model, r, s, phi, Vec3.UnitZ, true, 0.01, new BondModel());
        double gamma = model.Material.UnitWeight;
        double c = (t - h) / t;

        var mid = Enumerable.Range(0, model.ElementCount)
            .Where(e => Math.Abs(model.Mesh.FaceCentroid(e).Z - Height / 2) < Height / 48).ToArray();
        double z = mid.Average(e => model.Mesh.FaceCentroid(e).Z);
        double expected = gamma * (Height - z) / c; // N = gamma t (H - z) on a strip c t wide
        double comp = mid.Average(e => check.Compression[e]);
        _out.WriteLine($"mid-height bearing stress {comp:E4} MPa, hand calc {expected:E4} MPa, " +
                       $"contact {mid.Average(e => check.Contact[e]):F4}, h {mid.Average(e => check.LayerHeight[e]):F3} mm");

        Assert.All(mid, e => Assert.Equal(c, check.Contact[e], 3));
        Assert.All(mid, e => Assert.True(Math.Abs(Vec3.Dot(check.StackingDirection[e], Vec3.UnitZ)) > 0.999));
        Assert.InRange(comp / expected, 0.97, 1.03);
        Assert.All(mid, e => Assert.True(check.Tension[e] < 0.01 * check.Compression[e]));
    }

    [Fact]
    public void LateralLoadOpensInterfacesAndBondEfficiencyScalesIt()
    {
        var (model, r, s, phi) = Cylinder(6, 2, lateral: 5);
        var full = InterlayerCheck.Compute(model, r, s, phi, Vec3.UnitZ, true, 0.01, new BondModel(1.0));
        var half = InterlayerCheck.Compute(model, r, s, phi, Vec3.UnitZ, true, 0.01, new BondModel(0.5));

        int worst = Enumerable.Range(0, model.ElementCount).Where(full.IsValid).OrderByDescending(e => full.Tension[e]).First();
        _out.WriteLine($"max interface tension {full.Tension[worst]:E3} MPa at z {model.Mesh.FaceCentroid(worst).Z:F1} mm");
        Assert.True(full.Tension[worst] > 0);
        Assert.Equal(2 * full.BondUtilisation[worst], half.BondUtilisation[worst], 9);
        Assert.Equal(full.BearingUtilisation[worst], half.BearingUtilisation[worst], 12);
    }

    [Fact]
    public void TallerLayersOnTheSameWallRaiseUtilisation()
    {
        var (m1, r1, s1, phiLow) = Cylinder(6, 1.0, lateral: 5);
        var (m2, r2, s2, phiTall) = Cylinder(6, 3.0, lateral: 5);
        var low = InterlayerCheck.Compute(m1, r1, s1, phiLow, Vec3.UnitZ, true, 0.01, new BondModel());
        var tall = InterlayerCheck.Compute(m2, r2, s2, phiTall, Vec3.UnitZ, true, 0.01, new BondModel());
        double uLow = low.Utilisation.Where(x => !double.IsNaN(x)).Max();
        double uTall = tall.Utilisation.Where(x => !double.IsNaN(x)).Max();
        _out.WriteLine($"max utilisation: h 1 mm {uLow:F3}, h 3 mm {uTall:F3}");
        Assert.True(uTall > 1.5 * uLow);
    }
}
