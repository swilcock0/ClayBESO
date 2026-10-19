using ClayBeso.Geometry;
using ClayBeso.Optimisation;
using ClayBeso.Printing;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

/// <summary>Runs the whole chain the Grasshopper components use, on a vase.</summary>
public class PipelineTests
{
    private readonly ITestOutputHelper _out;
    public PipelineTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void VaseFromBesoToNonPlanarLayers()
    {
        var model = BesoTests.VaseModel(48, 40, lateral: 5.0);
        var opt = new BesoOptimizer(model, new BesoSettings
        {
            Mode = BesoMode.Thickness, VolumeFraction = 0.55, MinThickness = 4, MaxThickness = 10,
            FilterRadius = 20, MaxIterations = 60,
        });
        while (opt.Step()) { }
        var t = opt.EffectiveThickness();
        _out.WriteLine($"BESO: {opt.Iteration} it, converged {opt.Converged}, t {t.Min():F2}-{t.Max():F2} mm");

        var h = LayerHeightField.Compute(model.Mesh, t, 1.5, 4.0, LayerMapping.ConstantBeadArea, 0, 5, out double area);
        Assert.All(h, x => Assert.InRange(x, 1.5 - 1e-9, 4.0 + 1e-9));

        foreach (var method in new[] { LayerMethod.Conforming, LayerMethod.Eikonal })
        {
            var (bottom, top) = LayerField.ExtremeVertices(model.Mesh, Vec3.UnitZ, 1e-6);
            var field = LayerField.Compute(model.Mesh, h, bottom, top, Vec3.UnitZ, true, method);
            var paths = LayerSlicer.Slice(field, model.Mesh.FaceToVertex(t), Vec3.UnitZ, new Vec3(1, 0, 0));
            var zSpread = paths.Select(p => p.Points.Max(q => q.Z) - p.Points.Min(q => q.Z)).Max();
            _out.WriteLine($"{method}: bead area {area:F1} mm2, {field.LayerCount} layers, {paths.Count} paths, " +
                           $"{paths.Count(p => !p.Closed)} open, max in-layer Z variation {zSpread:F2} mm");

            Assert.True(field.LayerCount > 40);
            if (method == LayerMethod.Conforming)
                Assert.All(paths, p => Assert.True(p.Closed));
            Assert.All(paths.SelectMany(p => p.Width), w => Assert.InRange(w, 4 - 1e-6, 10 + 1e-6));
        }
    }
}
