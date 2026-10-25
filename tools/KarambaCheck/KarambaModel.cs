using ClayBeso.Fem;
using ClayBeso.Geometry;
using Karamba.Geometry;
using Karamba.Utilities;
using KarambaCommon;
using KCroSec = Karamba.CrossSections.CroSec;

namespace KarambaCheck;

/// <summary>
/// The same problem as a ClayBeso <see cref="ShellModel"/>, built in Karamba. ClayBeso works in N, mm, MPa;
/// Karamba's raw API in kN, m, kN/m2, so everything is converted on the way in and back on the way out.
/// </summary>
internal sealed class KarambaModel
{
    private static readonly Toolkit K = new Toolkit();
    private const double L = 1e-3;   // mm -> m
    private const double F = 1e-3;   // N -> kN
    private const double S = 1e3;    // MPa -> kN/m2
    private const double W = 1e6;    // N/mm3 -> kN/m3
    private const string LoadCase = "LC0";
    private const string WallId = "wall";

    public Karamba.Models.Model Model { get; private set; }
    private readonly int[] _node;    // ClayBeso node -> Karamba node (Karamba renumbers nodes)

    /// <param name="thickness">Per-face wall thickness [mm]; null = the model's own.</param>
    public KarambaModel(ShellModel m, IList<double> thickness = null)
    {
        var mesh = new Mesh3();
        foreach (var p in m.Mesh.Vertices) mesh.AddVertex(p.X * L, p.Y * L, p.Z * L);
        foreach (var f in m.Mesh.Faces) mesh.AddFace(f[0], f[1], f[2]);

        // Karamba takes shear moduli instead of Poisson's ratio.
        double e = m.Material.E * S, g = e / (2 * (1 + m.Material.Nu));
        var mat = K.Material.IsotropicMaterial("clay", "clay", e, g, g, m.Material.UnitWeight * W, 1, 1,
            Karamba.Materials.FemMaterial.FlowHypothesis.mises, 0, null);
        var t = thickness ?? m.Thickness;
        KCroSec cs = t.Distinct().Count() == 1
            ? K.CroSec.ShellConst(t[0] * L, 0, mat, "clay", WallId, "", null)
            : K.CroSec.ShellVariable(t.Select(x => x * L).ToList(), 0, new List<Karamba.Materials.FemMaterial> { mat }, "clay", WallId, "", null);
        var shells = K.Part.MeshToShell(new List<Mesh3> { mesh }, new List<string> { WallId }, new List<KCroSec> { cs },
            new MessageLogger(), out _, 1e-7, null, null);

        // Supports and loads by position: node indices change inside AssembleModel.
        var sups = new List<Karamba.Supports.Support>();
        var loads = new List<Karamba.Loads.Load>();
        var q = m.FixedLoads;
        for (int i = 0; i < m.NodeCount; i++)
        {
            var pt = At(m.Mesh.Vertices[i]);
            var fx = Enumerable.Range(0, 6).Select(d => m.Fixed[6 * i + d]).ToList();
            if (fx.Any(b => b)) sups.Add(K.Support.Support(pt, fx, Plane3.WorldXY));
            var force = new Vector3(q[6 * i] * F, q[6 * i + 1] * F, q[6 * i + 2] * F);
            var moment = new Vector3(q[6 * i + 3] * F * L, q[6 * i + 4] * F * L, q[6 * i + 5] * F * L);
            if (force.Length > 0 || moment.Length > 0) loads.Add(K.Load.PointLoad(pt, force, moment, LoadCase, false));
        }
        // ClayBeso's gravity vector is in multiples of g, like Karamba's.
        if (m.HasSelfWeight) loads.Add(K.Load.GravityLoad(new Vector3(m.Gravity.X, m.Gravity.Y, m.Gravity.Z), LoadCase));

        Model = K.Model.AssembleModel(shells, sups, loads, out _, out _, out _, out var msg, out _, null, null, null, 1e-7, null, null);
        if (!string.IsNullOrEmpty(msg)) Console.WriteLine("  karamba: " + msg);
        _node = m.Mesh.Vertices.Select(p => Model.NodeInd(At(p), 1e-9)).ToArray();
    }

    private static Point3 At(Vec3 p) => new Point3(p.X * L, p.Y * L, p.Z * L);

    /// <summary>First-order analysis: compliance f.u [N mm] and node translations [mm].</summary>
    public (double Compliance, Vec3[] U) Analyze()
    {
        Karamba.Algorithms.Analyze.solve(Model, new List<string> { LoadCase }, out _, out _, out var energy, out var analysed, out var warn);
        Model = analysed;
        // Soft-killed faces make Karamba warn about near-mechanisms; that is expected here.
        if (!string.IsNullOrEmpty(warn) && !warn.Contains("rigid body modes")) Console.WriteLine("  karamba: " + warn);
        Karamba.Results.NodeDisplacements.solve(Model, "0", _node.ToList(), out var tr, out _, out _, out _, out _);
        var u = tr.Select(v => new Vec3(v[0].X / L, v[0].Y / L, v[0].Z / L)).ToArray();
        return (2 * energy[0] / (F * L), u);   // Karamba reports elastic energy = compliance / 2
    }

    /// <summary>Smallest linear buckling load factor (Karamba needs a full Analyze for the normal forces first).</summary>
    public double Buckling()
    {
        Karamba.Algorithms.Analyze.solve(Model, new List<string> { LoadCase }, out _, out _, out _, out var analysed, out _);
        Karamba.Algorithms.Buckling.solve(analysed, 1, 1, 300, 1e-7, 1, out var factors, out _, out var msg);
        if (!string.IsNullOrEmpty(msg)) Console.WriteLine("  karamba: " + msg);
        return factors.Count > 0 ? factors[0] : double.NaN;
    }

    /// <summary>
    /// Karamba's soft-kill BESO for shells: which faces it keeps, and its iteration count.
    /// Removed faces get thickness x <paramref name="softKill"/>.
    /// </summary>
    public (bool[] Keep, int Iterations) Beso(ShellModel m, double target, double evolutionRate, double admission,
        double filterRadiusMm, double softKill, int maxIterations = 100)
    {
        string info = "";
        Karamba.Algorithms.BESOShell.solve(Model, new List<string> { WallId }, maxIterations, target, evolutionRate, admission,
            5, 1e-4, filterRadiusMm * L, 1, softKill, new List<string> { LoadCase }, ref info, _ => { }, CancellationToken.None,
            out var result, out _, out var cHist, out _);
        // Per-face heights; Karamba's list repeats its last value when it is shorter than the face count.
        var h = ((Karamba.CrossSections.CroSec_Shell)result.Elements().First().crosec).heights();
        double cut = 0.5 * m.Thickness.Min() * L;
        return (Enumerable.Range(0, m.ElementCount).Select(e => h[Math.Min(e, h.Count - 1)] > cut).ToArray(), cHist.Count);
    }
}
