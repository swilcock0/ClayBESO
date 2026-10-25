using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Printing;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class OverhangFixTests
{
    private readonly ITestOutputHelper _out;
    public OverhangFixTests(ITestOutputHelper output) => _out = output;

    /// <summary>95th percentile of the wall lean along the planned layers [deg].</summary>
    private static double Lean95(TriMesh mesh)
    {
        var model = ClayPrint.BedModel(mesh, new ClayMaterial(1, 0.35, 1900), 5, 1e-6);
        var plan = PrintPlanner.Plan(ShellDesign.FromModel(model), new PrintSettings { BaseTolerance = 1e-6 });
        var l = plan.Overhangs.SelectMany(o => o.Lean).Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        return l[(int)(0.95 * (l.Length - 1))];
    }

    [Fact]
    public void OverhangingVaseIsPulledBackToTheLimit()
    {
        var mesh = TestMeshes.EllipseLoftVase(60, 50);
        double before = Lean95(mesh);
        var fix = OverhangFix.Apply(mesh, maxLean: 45, baseTolerance: 1e-6);
        double after = Lean95(fix.Mesh);
        _out.WriteLine($"lean (95 %) {before:F1}° -> {after:F1}°, {fix.MovedCount} vertices moved, max {fix.MaxMove:F1} mm");
        Assert.True(before > 60);
        Assert.InRange(after, 0, 46);
        // Base and heights untouched; only horizontal moves.
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            Assert.Equal(mesh.Vertices[i].Z, fix.Mesh.Vertices[i].Z, 12);
            if (mesh.Vertices[i].Z < 1e-9) Assert.Equal(0, fix.Moved[i], 12);
        }
    }

    [Fact]
    public void PrintableShapeIsLeftAlone()
    {
        var mesh = TestMeshes.Vase(40, 30);       // leans at most ~20°
        var fix = OverhangFix.Apply(mesh, maxLean: 45);
        _out.WriteLine($"{fix.MovedCount} vertices moved, max {fix.MaxMove:G3} mm");
        Assert.True(fix.MaxMove < 1e-6);
    }
}
