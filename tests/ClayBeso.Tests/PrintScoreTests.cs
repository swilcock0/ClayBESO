using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Printing;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class PrintScoreTests
{
    private readonly ITestOutputHelper _out;
    public PrintScoreTests(ITestOutputHelper output) => _out = output;

    private static ShellModel Clamped(TriMesh mesh, ClayMaterial mat, double t)
    {
        int n = mesh.VertexCount;
        var fix = new bool[6 * n];
        double zMin = mesh.Vertices.Min(p => p.Z);
        for (int i = 0; i < n; i++)
            if (mesh.Vertices[i].Z < zMin + 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
        return new ShellModel(mesh, mat, new[] { t }, fix, null, new Vec3(0, 0, -1));
    }

    private static PrintPlan Plan(ShellModel model) =>
        PrintPlanner.Plan(ShellDesign.FromModel(model), new PrintSettings { BaseTolerance = 1e-6 });

    [Fact]
    public void ThreeMillimetreNozzleGivesThePluginDefaults()
    {
        var p = PrinterProfile.Presets[0];
        Assert.Equal(3.0, p.Nozzle);
        Assert.Equal(3.5, p.MinWall);
        Assert.Equal(7.0, p.MaxWall);
        Assert.Equal(1.0, p.MinLayer);
        Assert.Equal(2.25, p.MaxLayer);
        Assert.All(PrinterProfile.Presets, q =>
        {
            Assert.InRange(q.MinLayer / q.Nozzle, 0.25, 0.75);   // 25-75 % of the nozzle
            Assert.InRange(q.MaxLayer / q.Nozzle, 0.25, 0.75);
        });
    }

    [Fact]
    public void RampScoresBetweenGoodAndBad()
    {
        Assert.Equal(100, PrintScore.Ramp(0, 0, 0.1));
        Assert.Equal(50, PrintScore.Ramp(0.05, 0, 0.1), 9);
        Assert.Equal(0, PrintScore.Ramp(0.2, 0, 0.1));
        Assert.Equal(100, PrintScore.Ramp(10, 6, 1));   // larger is better works too
    }

    [Fact]
    public void UprightCylinderScoresHigherThanAnOverhangingVase()
    {
        var cylinder = PrintScore.Evaluate(Plan(ForceFlowTests.Cylinder(48, 32)));
        var ellipse = PrintScore.Evaluate(Plan(Clamped(TestMeshes.EllipseLoftVase(60, 50), new ClayMaterial(1, 0.35, 1900), 5)));
        _out.WriteLine(cylinder.Report());
        _out.WriteLine(ellipse.Report());
        Assert.InRange(cylinder.Total, 80, 100);
        Assert.True(ellipse.Total < cylinder.Total);
        Assert.Equal("Overhang", ellipse.Weakest.Name);
    }

    [Fact]
    public void PredictedBucklingCapsTheScore()
    {
        // Very soft clay: the cylinder buckles under its own weight (factor < 1).
        var stiff = ForceFlowTests.Cylinder(32, 24);
        var soft = new ShellModel(stiff.Mesh, new ClayMaterial(1e-4, 0.35, 1900), stiff.Thickness, stiff.Fixed, stiff.FixedLoads, stiff.Gravity);
        var plan = Plan(soft);
        var check = PrintChecker.Check(plan, new PrintCheckSettings { Stages = 2, Orthotropic = false });
        var score = PrintScore.Evaluate(plan, check);
        _out.WriteLine(score.Report());
        Assert.True(check.MinBucklingFactor < 1);
        Assert.Contains(score.Failing, i => i.Name == "Buckling");
        Assert.InRange(score.Total, 1, (int)PrintScore.FailCap);
        Assert.Same(plan, check.Plan);
    }
}
