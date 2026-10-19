using ClayBeso.Fem;
using ClayBeso.Geometry;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class PrintStageTests
{
    private readonly ITestOutputHelper _out;
    public PrintStageTests(ITestOutputHelper output) => _out = output;

    // Cylinder rows are 120/48 = 2.5 mm apart, so phi = z/2.5 puts one layer per row of faces.
    private static double[] RowPhi(ShellModel m) => m.Mesh.Vertices.Select(p => p.Z / 2.5).ToArray();

    private static readonly PrintStageOptions Plain = new PrintStageOptions { Orthotropic = false };

    [Fact]
    public void LastStageIsTheFinishedPiece()
    {
        var model = ForceFlowTests.Cylinder();
        var full = new ShellSolver(model).Solve(ElementScaling.FromThickness(model.Thickness));
        var stages = PrintStages.Run(model, model.Thickness, RowPhi(model), new[] { 48 }, Plain);
        Assert.Null(stages[0].Skipped);
        Assert.Equal(model.ElementCount, stages[0].Faces.Length);
        Assert.Equal(full.MaxDisplacement, stages[0].MaxDisplacement, 9);
    }

    [Fact]
    public void HalfwayStageEqualsAHalfHeightCylinder()
    {
        var model = ForceFlowTests.Cylinder();
        var stage = PrintStages.Run(model, model.Thickness, RowPhi(model), new[] { 24 }, Plain)[0];

        // The same mesh, built directly 60 mm tall.
        var mesh = TestMeshes.Grid(64, 24, (u, v) => new Vec3(
            ForceFlowTests.R * Math.Cos(2 * Math.PI * u), ForceFlowTests.R * Math.Sin(2 * Math.PI * u), 60 * v), closedU: true);
        var fix = new bool[6 * mesh.VertexCount];
        for (int i = 0; i < mesh.VertexCount; i++)
            if (mesh.Vertices[i].Z < 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
        var half = new ShellModel(mesh, model.Material, new[] { 6.0 }, fix, null, new Vec3(0, 0, -1));
        var direct = new ShellSolver(half).Solve(ElementScaling.FromThickness(half.Thickness));

        _out.WriteLine($"stage 24: {stage.Faces.Length} faces, top {stage.TopZ:F1} mm, max disp {stage.MaxDisplacement:E6}; direct {direct.MaxDisplacement:E6}");
        Assert.Equal(half.ElementCount, stage.Faces.Length);
        Assert.Equal(60, stage.TopZ, 9);
        Assert.Equal(direct.MaxDisplacement, stage.MaxDisplacement, 12);
    }

    [Fact]
    public void InterlayerUtilisationOnlyWithAStrength()
    {
        var model = ForceFlowTests.Cylinder(32, 24);
        var phi = model.Mesh.Vertices.Select(p => p.Z / 5.0).ToArray();
        var layers = PrintStages.EvenStages(phi, 4);
        var none = PrintStages.Run(model, model.Thickness, phi, layers, new PrintStageOptions());
        var withF = PrintStages.Run(model, model.Thickness, phi, layers, new PrintStageOptions { Strength = 0.01 });
        _out.WriteLine("stages " + string.Join(", ", withF.Select(s => $"L{s.Layer}: z {s.TopZ:F0} U {s.MaxUtilisation:F3}")));
        Assert.Equal(new[] { 6, 12, 18, 24 }, layers);
        Assert.All(none, s => Assert.True(double.IsNaN(s.MaxUtilisation)));
        Assert.All(withF, s => Assert.True(s.MaxUtilisation > 0));
        Assert.True(withF.Last().MaxUtilisation > withF.First().MaxUtilisation, "more clay above = more load on the base layers");
    }
}
