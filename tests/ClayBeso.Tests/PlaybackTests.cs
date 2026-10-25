using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Printing;
using Xunit.Abstractions;

namespace ClayBeso.Tests;

public class PlaybackTests
{
    private readonly ITestOutputHelper _out;
    public PlaybackTests(ITestOutputHelper output) => _out = output;

    private static PrintPlan CylinderPlan()
    {
        var model = ForceFlowTests.Cylinder(48, 32);
        var t = Enumerable.Range(0, model.ElementCount).Select(f => 4 + 3 * model.Mesh.FaceCentroid(f).Z / ForceFlowTests.H).ToArray();
        return PrintPlanner.Plan(new ShellDesign(model, t), new PrintSettings { BaseTolerance = 1e-6 });
    }

    private static double PolygonArea(List<(double u, double v)> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var (u0, v0) = p[i];
            var (u1, v1) = p[(i + 1) % p.Count];
            a += u0 * v1 - u1 * v0;
        }
        return a / 2;
    }

    /// <summary>Signed volume (divergence theorem): positive when the faces point outwards.</summary>
    private static double Volume(ValueMesh m) => m.Faces.Sum(f =>
        Vec3.Dot(m.Vertices[f[0]], Vec3.Cross(m.Vertices[f[1]], m.Vertices[f[2]])) / 6);

    [Theory]
    [InlineData(5.0, 2.0)]
    [InlineData(3.5, 2.25)]
    [InlineData(1.5, 2.0)]   // narrower than tall: rectangle
    public void BeadProfileHasTheBeadModelArea(double w, double h)
    {
        var p = PrintTimeline.Profile(w, h, 32);
        double area = PolygonArea(p);
        Assert.True(area > 0, "profile must run counter-clockwise");
        Assert.InRange(area / Bead.Area(w, h), 0.995, 1.0 + 1e-12);
        Assert.Equal(w, p.Max(q => q.u) - p.Min(q => q.u), 9);
        Assert.Equal(h, p.Max(q => q.v) - p.Min(q => q.v), 9);
    }

    [Fact]
    public void TimelineRunsThroughEveryPathInLayerOrder()
    {
        var plan = CylinderPlan();
        var tl = new PrintTimeline(plan, speed: 20);
        double expected = plan.Paths.Sum(p =>
        {
            double l = 0;
            for (int k = 1; k < p.Points.Count; k++) l += (p.Points[k] - p.Points[k - 1]).Length;
            return l + (p.Closed ? (p.Points[0] - p.Points[^1]).Length : 0);
        });
        Assert.Equal(expected, tl.TotalLength, 6);
        Assert.Equal(tl.TotalLength / 20, tl.TotalTime, 9);

        var first = tl.At(0);
        Assert.Equal(1, first.Layer);
        Assert.Equal(0, (first.Nozzle - tl.Paths[0].Points[0]).Length, 9);
        var end = tl.At(1);
        Assert.Equal(tl.Paths[^1].Layer, end.Layer);
        Assert.Equal(tl.TotalLength, end.Length, 6);

        int layer = 0;
        double time = -1;
        for (int i = 0; i <= 200; i++)
        {
            var m = tl.At(i / 200.0);
            Assert.True(m.Layer >= layer);
            Assert.True(m.Time >= time);
            layer = m.Layer;
            time = m.Time;
        }
        _out.WriteLine($"{tl.Paths.Count} paths, {tl.TotalLength:F0} mm, {tl.TotalTime / 60:F1} min at 20 mm/s");
    }

    [Fact]
    public void FinishedBeadsAreClosedTubesWithTheDepositedVolume()
    {
        var plan = CylinderPlan();
        var tl = new PrintTimeline(plan);
        var beads = tl.Beads(1, segments: 8);
        Assert.Equal(beads.Vertices.Count, beads.Values.Count);

        // Every closed loop is a closed tube: each edge is shared by exactly two faces.
        var edges = new Dictionary<(int, int), int>();
        foreach (var f in beads.Faces)
            for (int j = 0; j < 3; j++)
            {
                int a = f[j], b = f[(j + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                edges[key] = edges.TryGetValue(key, out int c) ? c + 1 : 1;
            }
        Assert.All(edges.Values, c => Assert.Equal(2, c));

        // Volume = deposited clay: sum of bead area x path length (the flow the plan asks for).
        double deposited = tl.Paths.Sum(p =>
        {
            int n = p.Points.Count;
            double v = 0;
            for (int k = 0; k < (p.Closed ? n : n - 1); k++)
            {
                int k1 = (k + 1) % n;
                v += (p.Points[k1] - p.Points[k]).Length * 0.5 * (p.BeadArea[k] + p.BeadArea[k1]);
            }
            return v;
        });
        double volume = Volume(beads);
        _out.WriteLine($"bead mesh volume {volume:F0} mm3, deposited {deposited:F0} mm3 ({volume / deposited - 1:P2})");
        Assert.InRange(volume / deposited, 0.97, 1.02);   // positive = outward faces; polygon sides lose ~1 %
    }

    [Fact]
    public void BeadsGrowAsThePrintProceeds()
    {
        var tl = new PrintTimeline(CylinderPlan());
        double previous = 0;
        foreach (double f in new[] { 0.1, 0.25, 0.5, 0.75, 1.0 })
        {
            var beads = tl.Beads(f);
            double v = Volume(beads);
            Assert.True(v > previous, $"volume should grow at {f}");
            previous = v;
        }
        double half = Volume(tl.Beads(0.5)), full = Volume(tl.Beads(1));
        // Half the time is less than half the clay here: the test wall thickens towards the top.
        Assert.InRange(half / full, 0.25, 0.5);
        Assert.Empty(tl.Beads(0).Faces);
    }

    [Fact]
    public void ValuesFollowTheChosenField()
    {
        var tl = new PrintTimeline(CylinderPlan());
        var byLayer = tl.Beads(1);
        Assert.Equal(1, byLayer.Values.Min());
        Assert.Equal(tl.Paths[^1].Layer, byLayer.Values.Max());
        var byWidth = tl.Beads(1, (i, k) => tl.Paths[i].Width[k]);
        Assert.InRange(byWidth.Values.Min(), 3.9, 5.0);
        Assert.InRange(byWidth.Values.Max(), 6.0, 7.1);
    }
}
