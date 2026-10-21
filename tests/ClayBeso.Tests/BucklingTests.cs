using System.Diagnostics;
using ClayBeso.Fem;
using ClayBeso.Geometry;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class BucklingTests
{
    private readonly ITestOutputHelper _out;
    public BucklingTests(ITestOutputHelper output) => _out = output;

    /// <summary>A strip in the XZ plane, clamped at z = 0, width b along x, height l along z.</summary>
    private static ShellModel UprightStrip(double l, double b, double t, double e, double density, double tipLoad, int nUp = 40, int nAcross = 4)
    {
        var mesh = TestMeshes.Grid(nAcross, nUp, (u, v) => new Vec3(b * u, 0, l * v));
        int n = mesh.VertexCount;
        var fix = new bool[6 * n];
        var load = new double[6 * n];
        var top = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var p = mesh.Vertices[i];
            if (p.Z < 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
            if (p.Z > l - 1e-9) top.Add(i);
        }
        // Consistent edge load (half shares on the corners), pointing down.
        double share = tipLoad / (top.Count - 1);
        foreach (int i in top)
        {
            bool corner = mesh.Vertices[i].X < 1e-9 || mesh.Vertices[i].X > b - 1e-9;
            load[6 * i + 2] = -(corner ? share / 2 : share);
        }
        return new ShellModel(mesh, new ClayMaterial(e, 0.0, density), new[] { t }, fix, load, new Vec3(0, 0, -1));
    }

    [Fact]
    public void GeometricStiffnessHasNoForceForARigidTranslation()
    {
        var x = new[] { 0.0, 3.0, 0.5 };
        var y = new[] { 0.0, 0.2, 2.0 };
        double area = 0.5 * ((x[1] - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (y[1] - y[0]));
        var h = Buckling.ElementBlocks(x, y, area, -2.0, 0.7, 0.3);
        for (int a = 0; a < 3; a++)
        {
            Assert.Equal(0, h[3 * a] + h[3 * a + 1] + h[3 * a + 2], 12);
            for (int b = 0; b < 3; b++) Assert.Equal(h[3 * a + b], h[3 * b + a], 12);
        }
    }

    [Fact]
    public void CantileverStripUnderEndLoadMatchesEuler()
    {
        // Clamped-free column: P_cr = pi^2 E I / (4 L^2), I = b t^3 / 12 (weak axis, out of the strip's plane).
        double l = 100, b = 10, t = 1, e = 1000;
        var model = UprightStrip(l, b, t, e, 0, tipLoad: 1.0);
        var res = Buckling.Compute(new ShellSolver(model), ElementScaling.FromThickness(model.Thickness));
        double exact = Math.PI * Math.PI * e * (b * t * t * t / 12) / (4 * l * l);
        _out.WriteLine($"Euler cantilever: lambda {res.Critical:G6}, theory {exact:G6} ({res.Iterations} iterations, converged {res.Converged})");
        Assert.True(res.Converged);
        Assert.InRange(res.Critical / exact, 0.98, 1.03);

        // The mode sways out of the plane (y) and is largest at the free end.
        var mode = res.Modes[0];
        int tip = Enumerable.Range(0, model.NodeCount).OrderByDescending(i => model.Mesh.Vertices[i].Z).First();
        Assert.True(Math.Abs(mode[tip].Y) > 0.95);
    }

    [Fact]
    public void CantileverStripUnderSelfWeightMatchesGreenhill()
    {
        // Greenhill: a clamped-free column buckles under its own weight at q L^3 = 7.837 E I.
        double l = 100, b = 10, t = 1, e = 1.0, rho = 1900;
        var model = UprightStrip(l, b, t, e, rho, tipLoad: 0);
        var res = Buckling.Compute(new ShellSolver(model), ElementScaling.FromThickness(model.Thickness));
        double q = model.Material.UnitWeight * b * t;
        double exact = 7.837 * e * (b * t * t * t / 12) / (q * l * l * l);
        _out.WriteLine($"Greenhill: lambda {res.Critical:G6}, theory {exact:G6} ({res.Iterations} iterations)");
        Assert.InRange(res.Critical / exact, 0.97, 1.03);
    }

    [Fact]
    public void ShellInTensionDoesNotBuckle()
    {
        // The same strip pulled at its free end: no compression anywhere, so no buckling factor.
        var model = UprightStrip(100, 10, 1, 1000, 0, tipLoad: -1.0);
        var res = Buckling.Compute(new ShellSolver(model), ElementScaling.FromThickness(model.Thickness));
        Assert.Empty(res.Factors);
        Assert.True(double.IsPositiveInfinity(res.Critical));
    }

    [Fact]
    public void LoadFactorScalesInverselyWithTheLoad()
    {
        var one = Buckling.Compute(new ShellSolver(UprightStrip(100, 10, 1, 1000, 0, 1.0)), ElementScaling.FromThickness(new[] { 1.0 }.Concat(Enumerable.Repeat(1.0, 319)).ToArray()));
        var two = Buckling.Compute(new ShellSolver(UprightStrip(100, 10, 1, 1000, 0, 2.0)), ElementScaling.FromThickness(Enumerable.Repeat(1.0, 320).ToArray()));
        Assert.Equal(one.Critical / 2, two.Critical, 6);
    }

    [Fact]
    public void TallerPrintStagesBuckleSooner()
    {
        // A clay cylinder under its own weight: each extra layer adds weight and height, so the
        // buckling factor drops as the print grows, and the last stage is the finished piece.
        var model = ForceFlowTests.Cylinder(48, 32);
        var phi = model.Mesh.Vertices.Select(p => p.Z / 3.75).ToArray();
        var stages = PrintStages.Run(model, model.Thickness, phi, PrintStages.EvenStages(phi, 4),
            new PrintStageOptions { Orthotropic = false, Buckling = true });
        var full = Buckling.Compute(new ShellSolver(model), ElementScaling.FromThickness(model.Thickness));
        _out.WriteLine("stages " + string.Join(", ", stages.Select(s => $"z {s.TopZ:F0}: {s.BucklingFactor:G4}")) +
                       $"; finished piece {full.Critical:G4}");

        for (int i = 1; i < stages.Count; i++) Assert.True(stages[i].BucklingFactor < stages[i - 1].BucklingFactor);
        Assert.Equal(full.Critical, stages[^1].BucklingFactor, 4);
        Assert.Equal(model.NodeCount, stages[0].BucklingMode.Length);
    }

    [Fact]
    public void AxiallyCompressedCylinderIsNearTheClassicalValue()
    {
        // Classical axial buckling stress of a cylinder: sigma = E t / (R sqrt(3 (1 - nu^2))).
        // Clamped base, top held round (radial and tangential fixed) and loaded down evenly.
        double rad = 50, len = 60, t = 1, e = 1000, nu = 0.3;
        int around = 128, up = 30;
        var mesh = TestMeshes.Grid(around, up, (u, v) =>
            new Vec3(rad * Math.Cos(2 * Math.PI * u), rad * Math.Sin(2 * Math.PI * u), len * v), closedU: true);
        int n = mesh.VertexCount;
        var fix = new bool[6 * n];
        var load = new double[6 * n];
        for (int i = 0; i < n; i++)
        {
            var p = mesh.Vertices[i];
            if (p.Z < 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
            if (p.Z > len - 1e-9)
            {
                fix[6 * i] = fix[6 * i + 1] = true;
                load[6 * i + 2] = -1.0 / around;
            }
        }
        var model = new ShellModel(mesh, new ClayMaterial(e, nu, 0), new[] { t }, fix, load, Vec3.Zero);
        var sw = Stopwatch.StartNew();
        var res = Buckling.Compute(new ShellSolver(model), ElementScaling.FromThickness(model.Thickness));
        _out.WriteLine(string.Join(" ", res.History.Select(x => x.ToString("G5"))));
        double classical = e * t / (rad * Math.Sqrt(3 * (1 - nu * nu))) * t * 2 * Math.PI * rad;
        _out.WriteLine($"cylinder: lambda {res.Critical:G6}, classical {classical:G6}, ratio {res.Critical / classical:F3} " +
                       $"({res.Iterations} solves, converged {res.Converged}, {sw.ElapsedMilliseconds} ms, {6 * n} dofs)");
        Assert.InRange(res.Critical / classical, 0.9, 1.05);
    }
}
