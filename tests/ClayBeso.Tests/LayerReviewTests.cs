using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Printing;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class LayerReviewTests
{
    private readonly ITestOutputHelper _out;
    public LayerReviewTests(ITestOutputHelper output) => _out = output;

    private static readonly ClayMaterial Clay = new(1.0, 0.35, 1900, 0.03);
    private static readonly PrinterProfile Printer = PrinterProfile.Presets[0];   // 3 mm nozzle: layers 1-2.25 mm

    /// <summary>Circular layers stacked with the given heights; radius as a function of z [mm].</summary>
    private static List<IList<Vec3>> Rings(IList<double> heights, int points, Func<double, double> radius)
    {
        var layers = new List<IList<Vec3>>();
        double z = 0;
        for (int k = 0; k < heights.Count; k++)
        {
            if (k > 0) z += heights[k];
            double r = radius(z);
            layers.Add(Enumerable.Range(0, points)
                .Select(i => new Vec3(r * Math.Cos(2 * Math.PI * i / points), r * Math.Sin(2 * Math.PI * i / points), z))
                .ToList());
        }
        return layers;
    }

    private static readonly IssueKind[] PerPoint =
        { IssueKind.TallLayer, IssueKind.LowLayer, IssueKind.PoorContact, IssueKind.Overhang, IssueKind.Unsupported, IssueKind.Tilt };

    private void Print(LayerReviewResult r)
    {
        _out.WriteLine($"{r.LayerCount} layers, wall {r.Mesh.FaceCount} faces, analysis {r.AnalysisMesh.FaceCount} faces");
        foreach (var w in r.Warnings) _out.WriteLine("! " + w);
        foreach (var i in r.Issues) _out.WriteLine($"{i.Severity:F2}  {i}");
        _out.WriteLine(r.Score.Report());
    }

    [Fact]
    public void StraightCylinderHasNoProblems()
    {
        var heights = Enumerable.Repeat(2.0, 30).ToArray();
        var r = LayerReview.Run(Rings(heights, 40, _ => 50), null, Clay, Printer);
        Print(r);

        Assert.Equal(30, r.LayerCount);
        Assert.Equal(2 * 40 * 29, r.Mesh.FaceCount);   // two triangles per point per strip
        Assert.Same(r.Mesh, r.AnalysisMesh);
        Assert.All(r.Paths.SelectMany(p => p.RealisedHeight), h => Assert.Equal(2.0, h, 6));   // from the spacing
        Assert.Empty(r.Issues);
        Assert.Empty(r.Warnings);
        Assert.All(r.Lean.Where(x => !double.IsNaN(x)), x => Assert.InRange(x, 0, 1));
        Assert.True(r.Score.Total > 70);
    }

    [Fact]
    public void TallLayersAreFoundWhereTheyAre()
    {
        var heights = Enumerable.Range(0, 40).Select(k => k >= 20 && k < 25 ? 2.8 : 2.0).ToArray();
        var r = LayerReview.Run(Rings(heights, 40, _ => 50), null, Clay, Printer);
        Print(r);

        var tall = Assert.Single(r.Issues, i => i.Kind == IssueKind.TallLayer);
        Assert.Equal(20, tall.FirstLayer);
        Assert.Equal(24, tall.LastLayer);
        Assert.Equal(2.8 / Printer.MaxLayer, tall.Severity, 6);
        Assert.Contains("2.80 mm", tall.Reason);
        Assert.DoesNotContain(r.Issues, i => i.Kind == IssueKind.LowLayer || i.Kind == IssueKind.Overhang);
        Assert.Contains(r.Score.Items, i => i.Name == "Layer height range" && i.Score < 100);
    }

    [Fact]
    public void LeaningWallIsAnOverhangOnlyWhereItLeans()
    {
        // Vertical up to z = 30, then leaning out 1.5 mm per mm of height (56° from vertical).
        var heights = Enumerable.Repeat(2.0, 30).ToArray();
        var r = LayerReview.Run(Rings(heights, 48, z => 40 + 1.5 * Math.Max(0, z - 30)), null, Clay, Printer,
            new LayerReviewOptions { Buckling = false, Stages = 2 });
        Print(r);

        var over = r.Issues.Where(i => i.Kind == IssueKind.Overhang).ToList();
        Assert.NotEmpty(over);
        Assert.All(over, i => Assert.True(i.FirstLayer >= 15, $"overhang from layer {i.FirstLayer}"));
        Assert.All(over, i => Assert.True(i.Location.Z > 28));
        Assert.Contains(r.Issues, i => i.Kind == IssueKind.Unsupported);
        Assert.Contains(r.Score.Items, i => i.Name == "Overhang" && i.Score < 50);
    }

    [Fact]
    public void LayersWithDifferentPointCountsMakeOneWall()
    {
        // Drawn by hand: point counts change from layer to layer and every layer starts somewhere else.
        var layers = new List<IList<Vec3>>();
        for (int k = 0; k < 20; k++)
        {
            int m = 30 + 7 * (k % 3);
            double start = 0.37 * k;
            layers.Add(Enumerable.Range(0, m).Select(i =>
            {
                double a = start + 2 * Math.PI * i / m;
                return new Vec3(50 * Math.Cos(a), 50 * Math.Sin(a), 2.0 * k);
            }).ToList());
        }
        var r = LayerReview.Run(layers, null, Clay, Printer, new LayerReviewOptions { Buckling = false, Stages = 0 });
        Print(r);

        int points = layers.Sum(l => l.Count);
        Assert.Equal(points, r.Mesh.VertexCount);
        // Each strip has as many triangles as points on its two layers.
        Assert.Equal(Enumerable.Range(0, 19).Sum(k => layers[k].Count + layers[k + 1].Count), r.Mesh.FaceCount);
        Assert.All(r.Paths.Skip(1).SelectMany(p => p.RealisedHeight), h => Assert.Equal(2.0, h, 6));
        // Polygons with different corner counts: the faces between them tilt a little, but the wall points outwards.
        Assert.All(Enumerable.Range(0, r.Mesh.FaceCount), f =>
        {
            var n = r.Mesh.FaceNormals[f];
            var c = r.Mesh.FaceCentroid(f);
            Assert.True(Math.Abs(n.Z) < 0.35, $"face {f} normal {n}");   // corners 0.3 mm off the next ring's edges, 2 mm apart
            Assert.True(n.X * c.X + n.Y * c.Y > 0, $"face {f} points inwards");
        });
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void WeakBondIsReportedWhereTheLayersAreLoaded()
    {
        // A very weak clay: the layers near the clamped base carry the most and fail first.
        var weak = new ClayMaterial(1.0, 0.35, 1900, 0.0015);
        var heights = Enumerable.Repeat(2.0, 30).ToArray();
        var r = LayerReview.Run(Rings(heights, 40, z => 50 + 0.8 * z), null, weak, Printer,
            new LayerReviewOptions { Buckling = false, Stages = 3 });
        Print(r);

        var bond = r.Issues.Where(i => i.Kind == IssueKind.LayerBond).ToList();
        Assert.NotEmpty(bond);
        Assert.All(bond, i => Assert.True(i.Severity > 1));
        Assert.All(bond.SelectMany(i => i.Faces), f => Assert.True(r.Interlayer[f] > 1));
        Assert.Contains(r.Issues, i => i.Kind == IssueKind.PrintStage && i.Reason.Contains("printed up to layer"));
        Assert.Contains(r.Score.Items, i => i.Name == "Layer bond" && i.Fails);
    }

    [Fact]
    public void GivenHeightsThatDontMatchTheLayersGiveAWarning()
    {
        var spacing = Enumerable.Repeat(2.0, 20).ToArray();
        var given = Enumerable.Range(0, 20).Select(_ => (IList<double>)new[] { 3.0 }).ToList();
        var r = LayerReview.Run(Rings(spacing, 32, _ => 50), given, Clay, Printer,
            new LayerReviewOptions { Buckling = false, Stages = 0 });
        Print(r);

        Assert.Equal(1.0, r.HeightMismatch, 6);
        Assert.Single(r.Warnings);
        Assert.Contains(r.Issues, i => i.Kind == IssueKind.TallLayer);   // the per-point checks use the given heights
        Assert.All(r.Paths.SelectMany(p => p.RealisedHeight), h => Assert.Equal(3.0, h));
    }

    [Fact]
    public void OneLayerWithHeightsThatDontFitItsSpaceIsAGapAndASquash()
    {
        // As in a student's design: every height matches the spacing except on one layer, where it is too low on one
        // side (the bead doesn't reach the layer below) and too high on the other.
        var spacing = Enumerable.Repeat(1.8, 20).ToArray();
        var layers = Rings(spacing, 36, _ => 50);
        var heights = layers.Select((l, k) => (IList<double>)l.Select(p => k == 10 ? (p.X > 0 ? 1.4 : 2.2) : 1.8).ToList()).ToList();
        var r = LayerReview.Run(layers, heights, Clay, Printer, new LayerReviewOptions { Buckling = false, Stages = 0 });
        Print(r);

        Assert.Empty(r.Warnings);   // only one layer of 20 is off: no general mismatch warning
        var gap = Assert.Single(r.Issues);   // the squashed side is only counted, not a problem (some squash is normal)
        Assert.Equal(IssueKind.LayerGap, gap.Kind);
        Assert.Equal((10, 10), (gap.FirstLayer, gap.LastLayer));
        Assert.True(gap.Location.X > 0);
        Assert.Equal(18.0, gap.Location.Z, 9);   // at a bead of layer 10
        Assert.Contains("stops 0.40 mm short", gap.Reason);
        Assert.Equal(0.4 / (LayerReview.SpacingTolerance * 1.8), gap.Severity, 6);
        Assert.InRange(r.SquashedPoints, 15, 19);
        Assert.All(Enumerable.Range(0, r.Mesh.FaceCount).Where(f => r.HeightVsSpacing[f] > 0), f => Assert.True(r.Severity[f] < 1));
        Assert.Equal(-0.4, r.HeightVsSpacing.Where(x => !double.IsNaN(x)).Min(), 9);
        Assert.Equal(0.4, r.HeightVsSpacing.Where(x => !double.IsNaN(x)).Max(), 9);

        // Asked for, the squashed side is a problem area too.
        var all = LayerReview.Run(layers, heights, Clay, Printer, new LayerReviewOptions { Buckling = false, Stages = 0, ReportSquashed = true });
        var squash = Assert.Single(all.Issues, i => i.Kind == IssueKind.Squashed);
        Assert.Equal((10, 10), (squash.FirstLayer, squash.LastLayer));
        Assert.True(squash.Location.X < 0);
        Assert.Contains("pushes 0.40 mm into the bead below", squash.Reason);
    }

    [Fact]
    public void HeightsInTheWrongUnitAreSuspected()
    {
        // Heights given in cm (0.2) for layers 2 mm apart.
        var layers = Rings(Enumerable.Repeat(2.0, 10).ToArray(), 24, _ => 50);
        var r = LayerReview.Run(layers, layers.Select(_ => (IList<double>)new[] { 0.2 }).ToList(), Clay, Printer,
            new LayerReviewOptions { Buckling = false, Stages = 0 });
        var w = Assert.Single(r.Warnings);
        Assert.Contains("10 × the given height: are the heights in mm?", w);
    }

    [Fact]
    public void WrongNumberOfHeightsIsExplained()
    {
        var layers = Rings(Enumerable.Repeat(2.0, 5).ToArray(), 20, _ => 50);
        var ex = Assert.Throws<ArgumentException>(() =>
            LayerReview.Run(layers, layers.Select(_ => (IList<double>)new[] { 2.0, 2.0 }).ToList(), Clay, Printer));
        Assert.Contains("20 points but 2 heights", ex.Message);
        Assert.Throws<ArgumentException>(() =>
            LayerReview.Run(layers, new List<IList<double>> { new[] { 2.0 } }, Clay, Printer));
    }

    [Fact]
    public void SpiralIsSplitIntoTurnsWithoutASeam()
    {
        // 6.5 turns, 2 mm per turn, 60 points per turn.
        int perTurn = 60;
        var pts = Enumerable.Range(0, (int)(6.5 * perTurn) + 1).Select(i =>
        {
            double a = 2 * Math.PI * i / perTurn;
            return new Vec3(50 * Math.Cos(a), 50 * Math.Sin(a), 2.0 * i / perTurn);
        }).ToList();
        var r = LayerReview.Run(new List<IList<Vec3>> { pts }, null, Clay, Printer, new LayerReviewOptions { Stages = 3 });
        Print(r);

        Assert.True(r.Spiral);
        Assert.Equal(7, r.LayerCount);   // six whole turns and a half one
        Assert.True(r.Plan.Spiral);
        Assert.DoesNotContain(r.Score.Items, i => i.Name == "Closed layers");
        Assert.DoesNotContain(r.Issues, i => PerPoint.Contains(i.Kind));
        Assert.All(r.Paths.Take(6).SelectMany(p => p.RealisedHeight), h => Assert.Equal(2.0, h, 3));

        // One continuous helical strip: edges used by a single face lie only along the bottom turn, the top
        // turn(s) and the two ends, never along a seam.
        var count = new Dictionary<(int, int), int>();
        foreach (var f in r.Mesh.Faces)
            for (int e = 0; e < 3; e++)
            {
                int a = f[e], b = f[(e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                count[key] = count.TryGetValue(key, out int c) ? c + 1 : 1;
            }
        Assert.DoesNotContain(count.Values, c => c > 2);
        int boundary = count.Values.Count(c => c == 1);
        Assert.True(boundary <= 2 * perTurn + perTurn / 2 + 4, $"{boundary} boundary edges");
        Assert.All(r.Mesh.FaceNormals, n => Assert.True(Math.Abs(n.Z) < 0.2));
    }

    [Fact]
    public void TallPieceIsAnalysedOnACoarserCopy()
    {
        var heights = Enumerable.Repeat(2.0, 150).ToArray();
        var r = LayerReview.Run(Rings(heights, 80, z => 60 + 10 * Math.Sin(z / 60)), null, Clay, Printer,
            new LayerReviewOptions { Buckling = false, Stages = 2 });
        Print(r);

        Assert.Equal(2 * 80 * 149, r.Mesh.FaceCount);
        Assert.InRange(r.AnalysisMesh.FaceCount, 1000, 6000);
        Assert.Contains(r.Warnings, w => w.Contains("coarser copy"));
        // Every face of the wall gets the structural results.
        Assert.All(r.Interlayer, u => Assert.False(double.IsNaN(u)));
        Assert.All(r.Thickness, t => Assert.Equal(Printer.BeadWidth, t, 9));
        Assert.Equal(149, r.Plan.Field.LayerCount);
    }

    [Fact]
    public void IdealWallKeepsTheClayWithinThePrintersRange()
    {
        var heights = Enumerable.Repeat(2.0, 30).ToArray();
        var r = LayerReview.Run(Rings(heights, 40, z => 50 + 0.6 * z), null, Clay, Printer,
            new LayerReviewOptions { Wall = WallMode.Ideal, Buckling = false, Stages = 0 });
        Print(r);
        _out.WriteLine($"{r.Iterations} iterations, compliance ratio {r.ComplianceRatio:F3}");

        Assert.All(r.Design.Thickness, t => Assert.InRange(t, Printer.MinWall - 1e-9, Printer.MaxWall + 1e-9));
        double v0 = r.Model.TotalVolume(Enumerable.Repeat(Printer.BeadWidth, r.Model.ElementCount).ToArray());
        Assert.Equal(1.0, r.Model.TotalVolume(r.Design.Thickness) / v0, 6);
        Assert.True(r.ComplianceRatio < 1);
        Assert.True(r.Thickness.Max() - r.Thickness.Min() > 0.5, "the ideal wall should not be uniform");
    }
}
