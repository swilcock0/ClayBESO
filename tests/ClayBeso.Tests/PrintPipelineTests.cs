using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Optimisation;
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
        var field = LayerField.Compute(model.Mesh, h, bottom, top, Vec3.UnitZ, true, LayerMethod.ForceAligned, flow, 30,
            maxHeight: plan.Rule.HMax);

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
    public void CheckerReportsTheLowestBucklingFactor()
    {
        var design = Graded();
        var plan = PrintPlanner.Plan(design, new PrintSettings { BaseTolerance = 1e-6 });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var off = PrintChecker.Check(plan, new PrintCheckSettings { Buckling = false });
        long msOff = sw.ElapsedMilliseconds;
        sw.Restart();
        var on = PrintChecker.Check(plan, new PrintCheckSettings());
        long msOn = sw.ElapsedMilliseconds;
        _out.WriteLine($"{design.Model.NodeCount} nodes, 8 stages: {msOff} ms without buckling, {msOn} ms with; " +
                       $"finished {on.FinalBuckling.Critical:G4}, stages " + string.Join(", ", on.Stages.Select(x => $"{x.BucklingFactor:G4}")));

        Assert.True(double.IsNaN(off.MinBucklingFactor));
        Assert.True(on.FinalBuckling.Converged);
        double lowest = on.Stages.Select(x => x.BucklingFactor).Append(on.FinalBuckling.Critical).Min();
        Assert.Equal(lowest, on.MinBucklingFactor, 12);
        Assert.Equal(on.Stages.Select(x => x.BucklingFactor).Min(), on.Stages[on.BucklingStage].BucklingFactor, 12);
        // Buckling changes nothing else.
        Assert.Equal(off.Final.Compliance, on.Final.Compliance, 9);
    }

    [Fact]
    public void ForceAlignedLayersStayWithinTheMaxLayerHeight()
    {
        // Regression for the example vase: thin BESO walls with linear-inverse layers (tallest layers on
        // the thinnest walls), sliced force-aligned. Turning the layers pulled them apart to up to 7 mm on
        // 3.5 mm walls, so beads barely touched and the interlayer utilisation reached ~19.
        var mesh = TestMeshes.EllipseLoftVase();
        int n = mesh.VertexCount;
        var fix = new bool[6 * n];
        for (int i = 0; i < n; i++)
            if (mesh.Vertices[i].Z <= 0.5) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
        var model = new ShellModel(mesh, new ClayMaterial(1, 0.35, 1900, 0.03), new[] { 5.0 }, fix, null, new Vec3(0, 0, -1));
        var rule = new LayerRule(LayerMapping.LinearInverse, 1.0, 2.25);
        var opt = new BesoOptimizer(model, new BesoSettings
        {
            MinThickness = 3.5, MaxThickness = 7, FilterRadius = SensitivityFilter.AutoRadius(mesh), LayerRule = rule, Bond = new BondModel(1),
        });
        while (opt.Step()) { }
        var design = new ShellDesign(model, opt.EffectiveThickness(), opt.Rule, opt.Settings.Bond, "test");

        var aligned = PrintPlanner.Plan(design, new PrintSettings { Method = LayerMethod.ForceAligned });
        var conforming = PrintPlanner.Plan(design, new PrintSettings { Method = LayerMethod.Conforming });
        double hMax = aligned.Rule.HMax;

        // The same field without the limit, for comparison.
        var flow = aligned.Flow.Direction.Select((d, e) => d * aligned.Flow.Anisotropy[e]).ToArray();
        var unlimited = LayerField.Compute(mesh, aligned.VertexHeight, aligned.Field.BaseVertices, aligned.Field.TopVertices,
            Vec3.UnitZ, true, LayerMethod.ForceAligned, flow, 30, 5);

        double Max(double[] h) => h.Where(x => !double.IsInfinity(x) && !double.IsNaN(x)).Max();
        var hAligned = aligned.Field.RealisedFaceHeight();
        var hConf = conforming.Field.RealisedFaceHeight();
        var check = PrintChecker.Check(aligned, new PrintCheckSettings { Strength = 0.03, Buckling = false });
        _out.WriteLine($"max realised h: unlimited {Max(unlimited.RealisedFaceHeight()):F2}, limited {Max(hAligned):F2}, conforming {Max(hConf):F2} (hMax {hMax}); " +
                       $"reduced turn on {aligned.Field.ReducedTurnFaces} faces, overall turn {aligned.Field.TurnFraction:P0}; poor-contact points {aligned.PoorContactFlags} (conforming {conforming.PoorContactFlags}); " +
                       $"max interlayer U {check.MaxUtilisation:G3}");

        Assert.True(Max(unlimited.RealisedFaceHeight()) > 1.5 * hMax, "the unlimited field should show the problem");
        Assert.True(aligned.Field.ReducedTurnFaces > 0);
        for (int f = 0; f < hAligned.Length; f++)
            if (!double.IsInfinity(hAligned[f]) && !double.IsInfinity(hConf[f]))
                Assert.True(hAligned[f] <= 1.02 * Math.Max(hMax, hConf[f]), $"face {f}: {hAligned[f]:F2} mm");
        Assert.True(check.MaxUtilisation < 2, $"interlayer utilisation {check.MaxUtilisation:G3}");
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
