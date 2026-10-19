using ClayBeso.Fem;
using ClayBeso.Geometry;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class SolverBenchmarks
{
    private readonly ITestOutputHelper _out;
    public SolverBenchmarks(ITestOutputHelper output) => _out = output;

    [Fact]
    public void CantileverPlateStripMatchesBeamTheory()
    {
        double l = 100, b = 10, t = 1, e = 1000, p = 1;
        var mesh = TestMeshes.Grid(20, 4, (u, v) => new Vec3(l * u, b * v, 0));
        int n = mesh.VertexCount;

        var fix = new bool[6 * n];
        var load = new double[6 * n];
        var tip = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var x = mesh.Vertices[i].X;
            if (x < 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
            if (x > l - 1e-9) tip.Add(i);
        }
        // consistent end shear: half shares on the corner nodes
        double share = p / (tip.Count - 1);
        foreach (int i in tip)
        {
            bool corner = mesh.Vertices[i].Y < 1e-9 || mesh.Vertices[i].Y > b - 1e-9;
            load[6 * i + 2] = corner ? share / 2 : share;
        }

        var model = new ShellModel(mesh, new ClayMaterial(e, 0.0, 0), new[] { t }, fix, load, Vec3.Zero);
        var solver = new ShellSolver(model);
        var r = solver.Solve(ElementScaling.FromThickness(model.Thickness));

        double inertia = b * t * t * t / 12;
        double exact = p * l * l * l / (3 * e * inertia);
        double w = tip.Average(i => r.U[6 * i + 2]);
        _out.WriteLine($"cantilever tip: {w:G6}, beam theory {exact:G6}");
        Assert.InRange(w / exact, 0.98, 1.02);
        double work = 0;
        for (int d = 0; d < load.Length; d++) work += load[d] * r.U[d];
        Assert.Equal(work, r.Compliance, 9);
    }

    [Theory]
    // CST+DKT facets converge from below (CST membrane is stiff): 0.265 @16, 0.283 @24, 0.296 @48, 0.299 @96.
    [InlineData(16, 0.85)]
    [InlineData(48, 0.97)]
    public void ScordelisLoRoof(int div, double minRatio)
    {
        // R = 25, L = 50, t = 0.25, E = 4.32e8, nu = 0, self-weight 90 per unit area.
        double rad = 25, len = 50, t = 0.25, phi = 40 * Math.PI / 180;
        var mesh = TestMeshes.Grid(div, div, (u, v) =>
        {
            double x = -len / 2 + len * u;
            double th = -phi + 2 * phi * v;
            return new Vec3(x, rad * Math.Sin(th), rad * Math.Cos(th));
        });
        int n = mesh.VertexCount;
        var fix = new bool[6 * n];
        int probe = -1;
        for (int i = 0; i < n; i++)
        {
            var p = mesh.Vertices[i];
            if (Math.Abs(Math.Abs(p.X) - len / 2) < 1e-9) { fix[6 * i + 1] = true; fix[6 * i + 2] = true; }
            if (Math.Abs(p.X) < 1e-9) fix[6 * i] = true; // symmetry plane: kills axial rigid motion
            if (Math.Abs(p.X) < 1e-9 && p.Y > rad * Math.Sin(phi) - 1e-9) probe = i;
        }

        var mat = new ClayMaterial(4.32e8, 0.0, 1.0);
        var gravity = new Vec3(0, 0, -90 / (t * mat.UnitWeight)); // gives 90 per unit area
        var model = new ShellModel(mesh, mat, new[] { t }, fix, null, gravity);
        var r = new ShellSolver(model).Solve(ElementScaling.FromThickness(model.Thickness));

        double w = -r.U[6 * probe + 2];
        _out.WriteLine($"Scordelis-Lo {div}x{div}: w = {w:G5} (reference 0.3024)");
        Assert.InRange(w / 0.3024, minRatio, 1.01);
    }
}
