using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Printing;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

/// <summary>The one-wire pipeline (ShellDesign → PrintPlanner → PrintChecker) against the step-by-step chain.</summary>
public class PrintPipelineTests
{
    private readonly ITestOutputHelper _out;
    public PrintPipelineTests(ITestOutputHelper output) => _out = output;

    private static ShellDesign Graded(double lateral = 0)
    {
        var model = ForceFlowTests.Cylinder(48, 32, lateral);
        var t = Enumerable.Range(0, model.ElementCount).Select(f => 4 + 3 * model.Mesh.FaceCentroid(f).Z / ForceFlowTests.H).ToArray();
        return new ShellDesign(model, t, source: "test");
    }

    [Fact]
    public void PlannerReproducesTheManualChain()
    {
        var design = Graded();
        var settings = new PrintSettings { BaseTolerance = 1e-6 };
        var plan = PrintPlanner.Plan(design, settings);

        var mesh = design.Model.Mesh;
        var h = LayerHeightField.Compute(mesh, design.Thickness, PrintPlanner.DefaultRule(), 5, out _);
        var (bottom, top) = LayerField.ExtremeVertices(mesh, Vec3.UnitZ, 1e-6);
        var field = LayerField.Compute(mesh, h, bottom, top, Vec3.UnitZ, true, LayerMethod.Conforming);

        Assert.Equal(field.LayerCount, plan.Field.LayerCount);
        for (int v = 0; v < mesh.VertexCount; v++) Assert.Equal(field.Phi[v], plan.Field.Phi[v], 9);
        Assert.Equal(0, plan.OpenPaths);
        Assert.True(plan.NominalArea > 0);
    }

    [Fact]
    public void ForceAlignedPlanComputesItsOwnForceFlow()
    {
        var design = Graded(lateral: 30);
        var plan = PrintPlanner.Plan(design, new PrintSettings { Method = LayerMethod.ForceAligned, BaseTolerance = 1e-6 });

        // By hand: analyse, take the flow, slice force-aligned.
        var model = design.Model;
        var s = ElementScaling.FromThickness(design.Thickness);
        var ff = ForceFlow.Compute(model, new ShellSolver(model).Solve(s), s);
        var flow = ff.Direction.Select((d, e) => d * ff.Anisotropy[e]).ToArray();
        var h = LayerHeightField.Compute(model.Mesh, design.Thickness, PrintPlanner.DefaultRule(), 5, out _);
        var (bottom, top) = LayerField.ExtremeVertices(model.Mesh, Vec3.UnitZ, 1e-6);
        var field = LayerField.Compute(model.Mesh, h, bottom, top, Vec3.UnitZ, true, LayerMethod.ForceAligned, flow, 30);

        Assert.NotNull(plan.Flow);
        for (int v = 0; v < model.NodeCount; v++) Assert.Equal(field.Phi[v], plan.Field.Phi[v], 6);
    }

    [Fact]
    public void DesignRuleWinsOverTheDefault()
    {
        var model = ForceFlowTests.Cylinder(32, 24);
        var rule = new LayerRule(LayerMapping.ConstantAspectRatio, 0.5, 5, aspectRatio: 0.3);
        var design = new ShellDesign(model, model.Thickness, rule, new BondModel(0.8));
        var plan = PrintPlanner.Plan(design, new PrintSettings { Smoothing = 0, BaseTolerance = 1e-6 });
        Assert.Equal(LayerMapping.ConstantAspectRatio, plan.Rule.Mapping);
        Assert.All(plan.VertexHeight, h => Assert.Equal(0.3 * 6.0, h, 9));
    }

    [Fact]
    public void CheckerMatchesTheInterlayerCheckAndFindsAWorstStage()
    {
        var design = Graded(lateral: 2);
        var plan = PrintPlanner.Plan(design, new PrintSettings { BaseTolerance = 1e-6 });
        var check = PrintChecker.Check(plan, new PrintCheckSettings { Strength = 0.01, Bond = new BondModel(0.7), Stages = 4 });

        // Same thing by hand.
        var model = design.Model;
        var s = ElementScaling.FromThickness(design.Thickness);
        var hReal = plan.Field.RealisedFaceHeight();
        var bond = new BondModel(0.7);
        for (int e = 0; e < s.Across.Length; e++)
            if (!double.IsInfinity(hReal[e])) s.Across[e] = 0.7 * bond.Contact(design.Thickness[e], hReal[e]);
        var r = new ShellSolver(model).Solve(s);
        var direct = InterlayerCheck.Compute(model, r, s, plan.Field.Phi, Vec3.UnitZ, true, 0.01, bond);

        _out.WriteLine($"finished U {check.FinalMaxUtilisation:F4}, stages " +
                       string.Join(", ", check.Stages.Select(x => $"L{x.Layer} U {x.MaxUtilisation:F3}")) + $", worst {check.WorstStage}");
        Assert.Equal(direct.Utilisation.Where(u => !double.IsNaN(u)).Max(), check.FinalMaxUtilisation, 9);
        Assert.Equal(4, check.Stages.Count);
        Assert.InRange(check.WorstStage, 0, 3);
        Assert.True(check.MaxUtilisation >= check.FinalMaxUtilisation);
    }

    [Fact]
    public void VoidDesignsAreRefused()
    {
        var design = Graded();
        var t = design.Thickness.ToArray();
        t[10] = 0;
        var holey = new ShellDesign(design.Model, t);
        var plan = PrintPlanner.Plan(holey, new PrintSettings { BaseTolerance = 1e-6 });
        Assert.Throws<ArgumentException>(() => PrintChecker.Check(plan, new PrintCheckSettings()));
    }
}
