using ClayBeso.Fem;
using ClayBeso.Optimisation;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class ThicknessFilterTests
{
    private readonly ITestOutputHelper _out;
    public ThicknessFilterTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void DesignSensitivityMatchesFiniteDifferenceThroughTheFilter()
    {
        var model = BesoTests.VaseModel(24, 16);
        var opt = new BesoOptimizer(model, new BesoSettings { MinThickness = 3.5, MaxThickness = 9, FilterRadius = 25 });
        var rng = new Random(7);
        for (int e = 0; e < opt.Design.Length; e++) opt.Design[e] = 3.6 + 5.3 * rng.NextDouble();

        var solver = new ShellSolver(model) { ReuseFactorisation = false };
        var alpha = opt.DesignSensitivity(solver.Solve(opt.ScalingFor(opt.Design)), out var w);
        foreach (int j in new[] { 5, 77, 190, 333 })
        {
            double h = 1e-5 * opt.Design[j];
            var xp = (double[])opt.Design.Clone(); xp[j] += h;
            var xm = (double[])opt.Design.Clone(); xm[j] -= h;
            double dc = (solver.Solve(opt.ScalingFor(xp)).Compliance - solver.Solve(opt.ScalingFor(xm)).Compliance) / (2 * h);
            double analytic = -alpha[j] * w[j];
            _out.WriteLine($"x{j}: FD {dc:E5} analytic {analytic:E5}");
            Assert.True(Math.Abs(dc - analytic) <= 1e-4 * Math.Abs(dc) + 1e-12, $"x{j}: FD {dc} vs {analytic}");
        }
    }

    [Fact]
    public void AutomaticFilterRadiusIsThreeAverageEdges()
    {
        // 10 x 10 unit squares, each split by one diagonal: 220 unit edges and 100 diagonals of sqrt 2.
        var mesh = TestMeshes.Grid(10, 10, (u, v) => new Geometry.Vec3(10 * u, 10 * v, 0));
        double mean = (220 + 100 * Math.Sqrt(2)) / 320;
        Assert.Equal(3 * mean, SensitivityFilter.AutoRadius(mesh), 12);

        // A finer mesh of the same surface gets a proportionally smaller radius.
        var fine = TestMeshes.Grid(20, 20, (u, v) => new Geometry.Vec3(10 * u, 10 * v, 0));
        Assert.Equal(0.5, SensitivityFilter.AutoRadius(fine) / SensitivityFilter.AutoRadius(mesh), 2);
    }

    private static double LargeNeighbourJump(ShellModel model, double[] t)
    {
        var faces = model.Mesh.Faces;
        var byEdge = new Dictionary<(int, int), List<int>>();
        for (int f = 0; f < faces.Length; f++)
            for (int k = 0; k < 3; k++)
            {
                int a = faces[f][k], b = faces[f][(k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (!byEdge.TryGetValue(key, out var l)) byEdge[key] = l = new List<int>();
                l.Add(f);
            }
        // The stair-steps are the large jumps between neighbours: look at the top 1 % of them.
        var jumps = byEdge.Values.Where(l => l.Count == 2).Select(l => Math.Abs(t[l[0]] - t[l[1]])).OrderBy(x => x).ToArray();
        return jumps[(int)(0.99 * (jumps.Length - 1))];
    }

    [Fact]
    public void FilteredThicknessIsSmoothAndAsStiff()
    {
        (double jump, double compliance, double volume, BesoOptimizer opt) Run(bool filter)
        {
            var model = BesoTests.VaseModel(48, 40, lateral: 5);
            var o = new BesoOptimizer(model, new BesoSettings
            {
                MinThickness = 3.5, MaxThickness = 9, FilterRadius = 20, ThicknessFilter = filter, MaxIterations = 80,
            });
            while (o.Step()) { }
            var t = o.EffectiveThickness();
            return (LargeNeighbourJump(model, t), o.Solver.Solve(o.Scaling).Compliance, o.DesignVolume, o);
        }
        var plain = Run(false);
        var smooth = Run(true);
        _out.WriteLine($"sensitivity filter only: 99th-percentile neighbour jump {plain.jump:F3} mm, C {plain.compliance:E4}, {plain.opt.Iteration} it");
        _out.WriteLine($"thickness filter:        99th-percentile neighbour jump {smooth.jump:F3} mm, C {smooth.compliance:E4}, {smooth.opt.Iteration} it, converged {smooth.opt.Converged}");

        Assert.True(smooth.jump < 0.5 * plain.jump, "the thickness filter should remove the stair-steps");
        Assert.InRange(smooth.compliance / plain.compliance, 0.9, 1.1);
        Assert.Equal(plain.volume, smooth.volume, 3);
        Assert.All(smooth.opt.EffectiveThickness(), t => Assert.InRange(t, 3.5 - 1e-9, 9 + 1e-9));
    }
}
