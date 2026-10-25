using ClayBeso.Fem;
using ClayBeso.Printing;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class ClayPrintTests
{
    private readonly ITestOutputHelper _out;
    public ClayPrintTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void OneCallGivesAnOptimisedCheckedAndScoredPrint()
    {
        var mesh = TestMeshes.Vase(40, 30);
        var printer = PrinterProfile.Presets[0];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = ClayPrint.Run(mesh, new ClayMaterial(1.0, 0.35, 1900, 0.03), printer);
        _out.WriteLine($"{sw.ElapsedMilliseconds} ms, {r.Iterations} BESO iterations, compliance ratio {r.ComplianceRatio:F3}");
        _out.WriteLine(r.Score.Report());

        Assert.True(r.Converged);
        Assert.All(r.Design.Thickness, t => Assert.InRange(t, printer.MinWall - 1e-9, printer.MaxWall + 1e-9));
        // Same clay as the uniform starting wall, but stiffer.
        double v0 = r.Model.TotalVolume(r.Model.Thickness), v1 = r.Model.TotalVolume(r.Design.Thickness);
        Assert.Equal(1.0, v1 / v0, 9);
        Assert.True(r.ComplianceRatio < 1);
        Assert.InRange(r.Score.Total, 1, 100);
        Assert.Contains(r.Score.Items, i => i.Name == "Buckling");
        Assert.Contains(r.Score.Items, i => i.Name == "Layer bond");
        Assert.True(r.Timeline.TotalTime > 0);
        Assert.All(r.Plan.Paths.SelectMany(p => p.RealisedHeight).Where(h => !double.IsNaN(h)),
            h => Assert.InRange(h, 0.5 * printer.MinLayer, 1.5 * printer.MaxLayer));
    }

    [Fact]
    public void UniformWallSkipsTheOptimisation()
    {
        var r = ClayPrint.Run(TestMeshes.Vase(24, 16), new ClayMaterial(1.0, 0.35, 1900), PrinterProfile.Presets[1],
            new ClayPrintOptions { Optimise = false, Buckling = false });
        Assert.Equal(0, r.Iterations);
        Assert.Equal(1, r.ComplianceRatio);
        Assert.All(r.Design.Thickness, t => Assert.Equal(PrinterProfile.Presets[1].StartWall, t, 9));
        Assert.DoesNotContain(r.Score.Items, i => i.Name == "Buckling");
    }

    [Fact]
    public void FixingTheOverhangRaisesTheScore()
    {
        var mesh = TestMeshes.EllipseLoftVase(60, 50);
        var clay = new ClayMaterial(1.0, 0.35, 1900, 0.03);
        var o = new ClayPrintOptions { Optimise = false, Buckling = false, BaseTolerance = 1e-6 };
        var before = ClayPrint.Run(mesh, clay, PrinterProfile.Presets[0], o);
        o.FixOverhang = true;
        var after = ClayPrint.Run(mesh, clay, PrinterProfile.Presets[0], o);
        _out.WriteLine($"score {before.Score.Total} -> {after.Score.Total}, max move {after.Fix.MaxMove:F1} mm");
        _out.WriteLine(after.Score.Report());
        Assert.Null(before.Fix);
        Assert.NotSame(mesh, after.Shape);
        Assert.DoesNotContain(after.Score.Failing, i => i.Name == "Overhang");
        Assert.True(after.Score.Total > before.Score.Total + 30);
    }
}
