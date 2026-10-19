using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Optimisation;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class BesoTests
{
    private readonly ITestOutputHelper _out;
    public BesoTests(ITestOutputHelper output) => _out = output;

    /// <summary>Vase clamped at its bottom ring, under self-weight plus a lateral point load at the rim.</summary>
    internal static ShellModel VaseModel(int nAround = 24, int nUp = 16, double lateral = 2.0)
    {
        var mesh = TestMeshes.Vase(nAround, nUp);
        int n = mesh.VertexCount;
        var fix = new bool[6 * n];
        var load = new double[6 * n];
        double zTop = mesh.Vertices.Max(p => p.Z);
        int rimNode = -1;
        for (int i = 0; i < n; i++)
        {
            var p = mesh.Vertices[i];
            if (p.Z < 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
            if (p.Z > zTop - 1e-9 && (rimNode < 0 || p.X > mesh.Vertices[rimNode].X)) rimNode = i;
        }
        load[6 * rimNode] = lateral;
        var clay = new ClayMaterial(2.0, 0.35, 1900);
        return new ShellModel(mesh, clay, new[] { 6.0 }, fix, load, new Vec3(0, 0, -1));
    }

    [Theory]
    [InlineData(BesoMode.Thickness)]
    [InlineData(BesoMode.Void)]
    public void SensitivityMatchesFiniteDifference(BesoMode mode)
    {
        var model = VaseModel(12, 8);
        var settings = new BesoSettings { Mode = mode, MinThickness = 2, MaxThickness = 10, FilterRadius = 0 };
        var opt = new BesoOptimizer(model, settings);
        // Use a non-uniform design so the check is not trivial.
        var rng = new Random(3);
        if (mode == BesoMode.Thickness)
            for (int e = 0; e < opt.Design.Length; e++) opt.Design[e] = 3 + 5 * rng.NextDouble();
        else
            for (int e = 0; e < opt.Design.Length; e++) opt.Design[e] = 0.5 + 0.5 * rng.NextDouble();

        ElementScaling Scale(double[] x)
        {
            var s = new ElementScaling(x.Length);
            for (int e = 0; e < x.Length; e++)
            {
                if (mode == BesoMode.Thickness) s.SetThickness(e, x[e]);
                else
                {
                    double t = model.Thickness[e], xp = Math.Pow(x[e], 3);
                    s.Membrane[e] = xp * t; s.Bending[e] = xp * t * t * t; s.Weight[e] = x[e] * t; s.Thickness[e] = t;
                }
            }
            return s;
        }

        var solver = opt.Solver;
        var r0 = solver.Solve(Scale(opt.Design));
        var alpha = opt.RawSensitivity(r0);

        foreach (int e in new[] { 0, 37, 101, 150 })
        {
            double h = 1e-5 * opt.Design[e];
            var xp = (double[])opt.Design.Clone(); xp[e] += h;
            var xm = (double[])opt.Design.Clone(); xm[e] -= h;
            double dc = (solver.Solve(Scale(xp)).Compliance - solver.Solve(Scale(xm)).Compliance) / (2 * h);
            double dv = model.Elements[e].Area * (mode == BesoMode.Thickness ? 1 : model.Thickness[e]);
            double analytic = -alpha[e] * dv;
            _out.WriteLine($"{mode} e{e}: FD {dc:E5}  analytic {analytic:E5}");
            Assert.True(Math.Abs(dc - analytic) <= 1e-4 * Math.Abs(dc) + 1e-12, $"e{e}: FD {dc} vs {analytic}");
        }
    }

    [Fact]
    public void ThicknessModeReachesTargetAndStiffensPerUnitVolume()
    {
        var model = VaseModel();
        var settings = new BesoSettings
        {
            Mode = BesoMode.Thickness, VolumeFraction = 0.6, MinThickness = 3, MaxThickness = 10,
            FilterRadius = 25, MaxIterations = 80,
        };
        var opt = new BesoOptimizer(model, settings);

        // Uniform reference design with the same final volume.
        double tUniform = 0.6 * 10;
        var uniform = new ShellSolver(model).Solve(ElementScaling.FromThickness(Enumerable.Repeat(tUniform, model.ElementCount).ToArray()));

        while (opt.Step()) { }
        var final = opt.Solver.Solve(opt.Scaling);
        _out.WriteLine($"iterations {opt.Iteration}, converged {opt.Converged}, vf {opt.CurrentVolumeFraction:F4}");
        _out.WriteLine($"compliance: uniform {uniform.Compliance:E4}, optimised {final.Compliance:E4}");
        _out.WriteLine($"t range {opt.Design.Min():F2} .. {opt.Design.Max():F2}");

        Assert.InRange(opt.CurrentVolumeFraction, 0.599, 0.601);
        Assert.True(final.Compliance < uniform.Compliance, "optimised design should beat the uniform wall");
        Assert.All(opt.Design, t => Assert.InRange(t, 3 - 1e-9, 10 + 1e-9));
    }

    [Fact]
    public void VoidModeRemovesMaterial()
    {
        var model = VaseModel();
        var settings = new BesoSettings
        {
            Mode = BesoMode.Void, VolumeFraction = 0.7, FilterRadius = 25, MaxIterations = 80, EvolutionRate = 0.03,
        };
        var opt = new BesoOptimizer(model, settings);
        while (opt.Step()) { }
        int voids = opt.Solid().Count(s => !s);
        _out.WriteLine($"iterations {opt.Iteration}, converged {opt.Converged}, vf {opt.CurrentVolumeFraction:F4}, voids {voids}/{model.ElementCount}");
        Assert.InRange(opt.CurrentVolumeFraction, 0.68, 0.72);
        Assert.True(voids > 0);
    }
}
