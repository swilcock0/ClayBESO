using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Optimisation;
using ClayBeso.Printing;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class OrthotropicTests
{
    private readonly ITestOutputHelper _out;
    public OrthotropicTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void RotatedStiffnessGivesTheSameEnergyAsWorkingInLayerAxes()
    {
        double q11 = 2.0, q22 = 0.7, q12 = 0.3, q66 = 0.45;
        var rng = new Random(5);
        for (int trial = 0; trial < 20; trial++)
        {
            double th = rng.NextDouble() * 2 * Math.PI, c = Math.Cos(th), s = Math.Sin(th);
            double ex = rng.NextDouble() - 0.5, ey = rng.NextDouble() - 0.5, gxy = rng.NextDouble() - 0.5;

            // strain in material axes a = (c, s), s-axis = (-s, c); engineering shear
            double ea = ex * c * c + ey * s * s + gxy * c * s;
            double es = ex * s * s + ey * c * c - gxy * c * s;
            double gas = 2 * (ey - ex) * c * s + gxy * (c * c - s * s);
            double eMat = q11 * ea * ea + 2 * q12 * ea * es + q22 * es * es + q66 * gas * gas;

            var q = ShellElement.Rotate(q11, q22, q12, q66, c, s);
            var e = new[] { ex, ey, gxy };
            double eRot = 0;
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                eRot += e[i] * q[i, j] * e[j];
            Assert.Equal(eMat, eRot, 12);
        }
    }

    private static ShellModel Cylinder(double nu, double axialLoad, double lateral = 0)
    {
        double r = 50, height = 120;
        var mesh = TestMeshes.Grid(48, 32, (u, v) =>
            new Vec3(r * Math.Cos(2 * Math.PI * u), r * Math.Sin(2 * Math.PI * u), height * v), closedU: true);
        int n = mesh.VertexCount;
        var fix = new bool[6 * n];
        var load = new double[6 * n];
        var top = Enumerable.Range(0, n).Where(i => mesh.Vertices[i].Z > height - 1e-9).ToArray();
        for (int i = 0; i < n; i++)
            if (mesh.Vertices[i].Z < 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
        foreach (int i in top)
        {
            load[6 * i + 2] = -axialLoad / top.Length;
            if (mesh.Vertices[i].X > r - 1e-9) load[6 * i] = lateral;
        }
        return new ShellModel(mesh, new ClayMaterial(1.0, nu, 0), new[] { 5.0 }, fix, load, Vec3.Zero);
    }

    [Fact]
    public void LayeredModelWithKappaOneMatchesIsotropic()
    {
        var iso = Cylinder(0.35, 10, lateral: 2);
        var layered = Cylinder(0.35, 10, lateral: 2);
        layered.EnableLayerAnisotropy(Vec3.UnitZ);
        var s = ElementScaling.FromThickness(iso.Thickness);
        var a = new ShellSolver(iso).Solve(s);
        var b = new ShellSolver(layered).Solve(s);
        Assert.Equal(a.Compliance, b.Compliance, 10);
        Assert.All(layered.Elements, el => Assert.NotNull(el.Km1)); // a cylinder's faces are all upright
    }

    [Theory]
    [InlineData(BesoMode.Thickness, LayerMapping.Proportional)]
    [InlineData(BesoMode.Thickness, LayerMapping.ConstantBeadArea)]
    [InlineData(BesoMode.Void, LayerMapping.Proportional)]
    public void LayeredSensitivityMatchesFiniteDifference(BesoMode mode, LayerMapping mapping)
    {
        var model = BesoTests.VaseModel(12, 8);
        var settings = new BesoSettings
        {
            Mode = mode, VolumeFraction = mode == BesoMode.Void ? 0.5 : 0, MinThickness = 3.5, MaxThickness = 9, FilterRadius = 0,
            LayerRule = new LayerRule(mapping, 1.0, 2.5), Bond = new BondModel(0.8),
        };
        var opt = new BesoOptimizer(model, settings);
        var rng = new Random(11);
        for (int e = 0; e < opt.Design.Length; e++)
            opt.Design[e] = mode == BesoMode.Thickness ? 3.6 + 5.3 * rng.NextDouble() : 0.5 + 0.5 * rng.NextDouble();

        // Direct solves: the reused-factorisation CG stops at a 1e-10 residual, which finite differences amplify.
        var solver = new ShellSolver(model) { ReuseFactorisation = false };
        var alpha = opt.RawSensitivity(solver.Solve(opt.ScalingFor(opt.Design)));
        foreach (int e in new[] { 3, 40, 99, 170 })
        {
            double h = 1e-5 * opt.Design[e];
            var xp = (double[])opt.Design.Clone(); xp[e] += h;
            var xm = (double[])opt.Design.Clone(); xm[e] -= h;
            double dc = (solver.Solve(opt.ScalingFor(xp)).Compliance - solver.Solve(opt.ScalingFor(xm)).Compliance) / (2 * h);
            double dv = model.Elements[e].Area * (mode == BesoMode.Thickness ? 1 : model.Thickness[e]);
            double analytic = -alpha[e] * dv;
            _out.WriteLine($"{mode}/{mapping} e{e}: kappa {opt.Kappa(mode == BesoMode.Thickness ? opt.Design[e] : model.Thickness[e]):F3}, FD {dc:E5}, analytic {analytic:E5}");
            Assert.True(Math.Abs(dc - analytic) <= 1e-4 * Math.Abs(dc) + 1e-12, $"e{e}: FD {dc} vs {analytic}");
        }
    }

    [Fact]
    public void LayerAwareDesignIsStifferWhenLayersAreAccountedFor()
    {
        BesoOptimizer Run(LayerRule rule)
        {
            var model = BesoTests.VaseModel(32, 24, lateral: 5);
            var o = new BesoOptimizer(model, new BesoSettings
            {
                MinThickness = 3.5, MaxThickness = 9, FilterRadius = 20, LayerRule = rule, Bond = new BondModel(0.5),
            });
            while (o.Step()) { }
            return o;
        }
        var rule = new LayerRule(LayerMapping.Proportional, 1.0, 2.5);
        var aware = Run(rule);
        var naive = Run(null);

        // Judge both designs in the layered model.
        double cAware = aware.Solver.Solve(aware.ScalingFor(aware.Design)).Compliance;
        double cNaive = aware.Solver.Solve(aware.ScalingFor(naive.Design)).Compliance;
        double diff = aware.Design.Zip(naive.Design, (x, y) => Math.Abs(x - y)).Average();
        _out.WriteLine($"layered compliance: layer-aware {cAware:E4}, layer-naive {cNaive:E4} ({cNaive / cAware - 1:P1} worse); " +
                       $"mean |dt| {diff:F2} mm; converged {aware.Converged}/{naive.Converged}, kappa {aware.ScalingFor(aware.Design).Across.Min():F2}-{aware.ScalingFor(aware.Design).Across.Max():F2}");
        Assert.True(aware.Converged);
        Assert.Equal(naive.DesignVolume, aware.DesignVolume, 3);
        Assert.True(cAware <= cNaive * 1.001);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.25)]
    public void AxialShorteningScalesWithOneOverKappa(double kappa)
    {
        // nu = 0: no Poisson coupling, so the axial (across-layer) stiffness is exactly kappa E.
        var model = Cylinder(0.0, 10);
        model.EnableLayerAnisotropy(Vec3.UnitZ);
        var solver = new ShellSolver(model);
        var s = ElementScaling.FromThickness(model.Thickness);
        var full = solver.Solve(s);
        for (int e = 0; e < model.ElementCount; e++) s.Across[e] = kappa;
        var weak = solver.Solve(s);
        double ratio = weak.Compliance / full.Compliance;
        _out.WriteLine($"kappa {kappa}: compliance ratio {ratio:F5} (expected {1 / kappa:F5})");
        Assert.Equal(1 / kappa, ratio, 3);
    }
}
