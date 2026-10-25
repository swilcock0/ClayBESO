using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Optimisation;
using ClayBeso.Tests;

namespace KarambaCheck;

/// <summary>
/// Runs the same shells through ClayBeso and Karamba3D and prints the two side by side.
/// Lines marked CHECK differ by more than the tolerance stated on them; the exit code is the number of CHECKs.
/// Usage: KarambaCheck [static] [buckling] [thickness] [void] [convergence]   (default: all but convergence)
/// </summary>
internal static class Program
{
    private static int _checks;

    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    private static int Main(string[] args)
    {
        // Karamba's native solver (karambafeb.dll) and its native dependencies sit next to this program.
        // Load them from here explicitly; relying on the default search order failed intermittently.
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        SetDllDirectory(AppContext.BaseDirectory);
        System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "karambafeb.dll"));
        var groups = args.Length > 0 ? args.Select(a => a.ToLowerInvariant()).ToHashSet()
            : new HashSet<string> { "static", "buckling", "thickness", "void" };
        Console.WriteLine("ClayBeso vs " + Karamba.Licenses.License.version());

        if (groups.Contains("static")) Static();
        if (groups.Contains("convergence")) Convergence();
        if (groups.Contains("buckling")) BucklingCases();
        if (groups.Contains("thickness")) ThicknessMode();
        if (groups.Contains("void")) VoidMode();

        Console.WriteLine(_checks == 0 ? "\nAll comparisons within tolerance." : $"\n{_checks} comparison(s) marked CHECK.");
        return _checks;
    }

    private static string Pct(double x) => (x < 0 ? "-" : "+") + Math.Abs(x).ToString("0.0%");

    private static string Flag(double ratio, double tol)
    {
        if (Math.Abs(ratio - 1) <= tol) return "ok";
        _checks++;
        return $"CHECK (> {tol:P0})";
    }

    // ------------------------------------------------------------------ helpers

    private static (double C, Vec3[] U) Ours(ShellModel m, IList<double> t = null)
    {
        var r = new ShellSolver(m).Solve(ElementScaling.FromThickness(t ?? m.Thickness));
        return (r.Compliance, Enumerable.Range(0, m.NodeCount).Select(r.Displacement).ToArray());
    }

    /// <summary>Compliance, largest displacement and the RMS nodal difference (relative to Karamba's largest).</summary>
    private static void Compare(string name, ShellModel m, double tol, Func<Vec3[], double> probe = null, double? exact = null)
    {
        var o = Ours(m);
        var k = new KarambaModel(m).Analyze();
        double maxO = o.U.Max(v => v.Length), maxK = k.U.Max(v => v.Length);
        double rms = Math.Sqrt(Enumerable.Range(0, m.NodeCount).Average(i => Math.Pow((o.U[i] - k.U[i]).Length, 2))) / maxK;
        Console.WriteLine($"{name,-32} compliance ours {o.C,10:G5} karamba {k.Compliance,10:G5} ({Pct(o.C / k.Compliance - 1),6}) " +
                          $"max|u| {maxO,9:G4} / {maxK,9:G4} mm, nodal rms diff {rms,5:P1}  {Flag(o.C / k.Compliance, tol)}");
        if (probe == null) return;
        double po = probe(o.U), pk = probe(k.U);
        Console.WriteLine($"{"",32} probe ours {po:G5} karamba {pk:G5}" +
                          (exact.HasValue ? $", reference {exact:G5} (ours {Pct(po / exact.Value - 1)}, karamba {Pct(pk / exact.Value - 1)})" : ""));
    }

    private static readonly ClayMaterial Clay = new ClayMaterial(1.0, 0.35, 1900);

    /// <summary>Base ring clamped; optional sideways point load [N, +x] at the rim node furthest along x.</summary>
    private static ShellModel Clamped(TriMesh mesh, double t, bool selfWeight, double rimLoad, ClayMaterial mat = null)
    {
        int n = mesh.VertexCount;
        var fix = new bool[6 * n];
        var load = new double[6 * n];
        double zmin = mesh.Vertices.Min(p => p.Z), zmax = mesh.Vertices.Max(p => p.Z);
        for (int i = 0; i < n; i++)
            if (mesh.Vertices[i].Z < zmin + 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
        if (rimLoad != 0)
        {
            int top = Enumerable.Range(0, n).Where(i => mesh.Vertices[i].Z > zmax - 1e-9).OrderByDescending(i => mesh.Vertices[i].X).First();
            load[6 * top] = rimLoad;
        }
        return new ShellModel(mesh, mat ?? Clay, new[] { t }, fix, load, selfWeight ? new Vec3(0, 0, -1) : Vec3.Zero);
    }

    /// <summary>TestMeshes.Vase (200 mm tall), clay E 1 MPa, nu 0.35, 1900 kg/m3.</summary>
    private static ShellModel Vase(int around, int up, bool selfWeight, double rimLoad, double t = 5) =>
        Clamped(TestMeshes.Vase(around, up), t, selfWeight, rimLoad);

    private static ShellModel EllipseVase(int around, int up, double t, ClayMaterial mat = null) =>
        Clamped(TestMeshes.EllipseLoftVase(around, up), t, true, 0, mat);

    /// <summary>A strip in the XZ plane clamped at z = 0, with a downward edge load at the top.</summary>
    private static ShellModel UprightStrip(double l, double b, double t, double e, double density, double tipLoad)
    {
        var mesh = TestMeshes.Grid(4, 40, (u, v) => new Vec3(b * u, 0, l * v));
        int n = mesh.VertexCount;
        var fix = new bool[6 * n];
        var load = new double[6 * n];
        var top = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var p = mesh.Vertices[i];
            if (p.Z < 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
            if (p.Z > l - 1e-9) top.Add(i);
        }
        double share = tipLoad / (top.Count - 1);
        foreach (int i in top)
        {
            bool corner = mesh.Vertices[i].X < 1e-9 || mesh.Vertices[i].X > b - 1e-9;
            load[6 * i + 2] = -(corner ? share / 2 : share);
        }
        return new ShellModel(mesh, new ClayMaterial(e, 0.0, density), new[] { t }, fix, load, new Vec3(0, 0, -1));
    }

    // ------------------------------------------------------------------ groups

    private static void Static()
    {
        Console.WriteLine("\n=== Linear static ===");
        {
            double l = 100, b = 10, t = 1, e = 1000, p = 1;
            var mesh = TestMeshes.Grid(20, 4, (u, v) => new Vec3(l * u, b * v, 0));
            int n = mesh.VertexCount;
            var fix = new bool[6 * n];
            var load = new double[6 * n];
            var tip = new List<int>();
            for (int i = 0; i < n; i++)
            {
                double x = mesh.Vertices[i].X;
                if (x < 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
                if (x > l - 1e-9) tip.Add(i);
            }
            foreach (int i in tip)
            {
                bool corner = mesh.Vertices[i].Y < 1e-9 || mesh.Vertices[i].Y > b - 1e-9;
                load[6 * i + 2] = p / (tip.Count - 1) * (corner ? 0.5 : 1);
            }
            var m = new ShellModel(mesh, new ClayMaterial(e, 0.0, 0), new[] { t }, fix, load, Vec3.Zero);
            Compare("cantilever strip (beam theory)", m, 0.01, u => tip.Average(i => u[i].Z), p * l * l * l / (3 * e * b * t * t * t / 12));
        }
        foreach (int div in new[] { 16, 48 })
        {
            // Unit-free benchmark: R 25, L 50, t 0.25, E 4.32e8, nu 0, 90 per unit area; reference 0.3024.
            double rad = 25, len = 50, t = 0.25, phi = 40 * Math.PI / 180;
            var mesh = TestMeshes.Grid(div, div, (u, v) =>
            {
                double th = -phi + 2 * phi * v;
                return new Vec3(-len / 2 + len * u, rad * Math.Sin(th), rad * Math.Cos(th));
            });
            int n = mesh.VertexCount, probe = -1;
            var fix = new bool[6 * n];
            for (int i = 0; i < n; i++)
            {
                var p = mesh.Vertices[i];
                if (Math.Abs(Math.Abs(p.X) - len / 2) < 1e-9) { fix[6 * i + 1] = true; fix[6 * i + 2] = true; }
                if (Math.Abs(p.X) < 1e-9) fix[6 * i] = true;
                if (Math.Abs(p.X) < 1e-9 && p.Y > rad * Math.Sin(phi) - 1e-9) probe = i;
            }
            var mat = new ClayMaterial(4.32e8, 0.0, 1.0);
            var m = new ShellModel(mesh, mat, new[] { t }, fix, null, new Vec3(0, 0, -90 / (t * mat.UnitWeight)));
            Compare($"Scordelis-Lo roof {div}x{div}", m, 0.03, u => -u[probe].Z, 0.3024);
        }
        Compare("vase 48x36, self-weight", Vase(48, 36, true, 0), 0.01);
        Compare("vase 48x36, rim point load", Vase(48, 36, false, 0.05), 0.01);
        // Strongly curved: Karamba's element is stiffer on coarse meshes (see 'convergence').
        Compare("ellipse vase 120x100, t 0.5 mm", EllipseVase(120, 100, 0.5), 0.05);
    }

    private static void Convergence()
    {
        Console.WriteLine("\n=== Mesh convergence, example ellipse vase under self-weight ===");
        foreach (double t in new[] { 5.0, 2.0, 0.5 })
        foreach (var (a, u) in new[] { (30, 25), (60, 50), (120, 100) })
        {
            var m = EllipseVase(a, u, t);
            double co = Ours(m).C, ck = new KarambaModel(m).Analyze().Compliance;
            Console.WriteLine($"t {t,3} mm, {a,3}x{u,-3}  compliance ours {co,9:G5} karamba {ck,9:G5} ({Pct(co / ck - 1)})");
        }
    }

    private static void BucklingCases()
    {
        Console.WriteLine("\n=== Linear buckling, first load factor ===");
        void Line(string name, ShellModel m, double tol, double? theory = null)
        {
            double o = Buckling.Compute(new ShellSolver(m), ElementScaling.FromThickness(m.Thickness)).Critical;
            double k = new KarambaModel(m).Buckling();
            Console.WriteLine($"{name,-32} ours {o,10:G5} karamba {k,10:G5} ({Pct(o / k - 1),6})" +
                              (theory.HasValue ? $", theory {theory:G5}" : "") + $"  {Flag(o / k, tol)}");
        }
        {
            double l = 100, b = 10, t = 1, e = 1000;
            Line("Euler cantilever strip", UprightStrip(l, b, t, e, 0, 1.0), 0.01, Math.PI * Math.PI * e * (b * t * t * t / 12) / (4 * l * l));
        }
        {
            double l = 100, b = 10, t = 1, e = 1.0, rho = 1900;
            var m = UprightStrip(l, b, t, e, rho, 0);
            double q = m.Material.UnitWeight * b * t;
            Line("Greenhill strip (self-weight)", m, 0.01, 7.837 * e * (b * t * t * t / 12) / (q * l * l * l));
        }
        {
            double rad = 50, len = 60, t = 1, e = 1000, nu = 0.3;
            int around = 128;
            var mesh = TestMeshes.Grid(around, 30, (u, v) =>
                new Vec3(rad * Math.Cos(2 * Math.PI * u), rad * Math.Sin(2 * Math.PI * u), len * v), closedU: true);
            int n = mesh.VertexCount;
            var fix = new bool[6 * n];
            var load = new double[6 * n];
            for (int i = 0; i < n; i++)
            {
                var p = mesh.Vertices[i];
                if (p.Z < 1e-9) for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
                if (p.Z > len - 1e-9) { fix[6 * i] = fix[6 * i + 1] = true; load[6 * i + 2] = -1.0 / around; }
            }
            var m = new ShellModel(mesh, new ClayMaterial(e, nu, 0), new[] { t }, fix, load, Vec3.Zero);
            Line("axially loaded cylinder", m, 0.02, e * t / (rad * Math.Sqrt(3 * (1 - nu * nu))) * t * 2 * Math.PI * rad);
        }
        Line("vase 48x36, t 5, self-weight", Vase(48, 36, true, 0), 0.02);
        Line("vase 48x36, t 2, self-weight", Vase(48, 36, true, 0, t: 2), 0.02);
        Line("ellipse vase 60x50, t 2", EllipseVase(60, 50, 2), 0.05);
    }

    private static void ThicknessMode()
    {
        Console.WriteLine("\n=== Thickness mode: our graded wall re-analysed in Karamba ===");
        Console.WriteLine("    (compliance of the graded wall / a uniform wall with the same clay; lower = stiffer)");
        foreach (var (name, m, tol) in new[]
                 {
                     ("vase, self-weight + rim load", Vase(48, 36, true, 0.05), 0.02),
                     ("vase, rim load", Vase(48, 36, false, 0.05), 0.02),
                     ("ellipse vase, self-weight", EllipseVase(60, 50, 5), 0.05),
                 })
        {
            var opt = new BesoOptimizer(m, new BesoSettings { Mode = BesoMode.Thickness, FilterRadius = SensitivityFilter.AutoRadius(m.Mesh) });
            while (!opt.Finished) opt.Step();
            var t = opt.EffectiveThickness();
            double mean = t.Select((x, e) => x * m.Elements[e].Area).Sum() / m.Elements.Sum(el => el.Area);
            var uniform = Enumerable.Repeat(mean, m.ElementCount).ToArray();
            double ro = Ours(m, t).C / Ours(m, uniform).C;
            double rk = new KarambaModel(m, t).Analyze().Compliance / new KarambaModel(m, uniform).Analyze().Compliance;
            Console.WriteLine($"{name,-32} {opt.Iteration,3} iterations, t {t.Min():F2}-{t.Max():F2} mm: ours {ro:F3} karamba {rk:F3}  {Flag(ro / rk, tol)}");
        }
    }

    private static void VoidMode()
    {
        const double softKill = 1e-3;   // Karamba's: removed faces keep 1/1000 of the thickness
        Console.WriteLine("\n=== Void mode, 50 % of the wall kept: ours vs Karamba's BESO for shells ===");
        Console.WriteLine("    (both designs analysed by both solvers with Karamba's soft-kill; compliance / full wall; lower = stiffer)");
        foreach (var (name, m) in new[]
                 {
                     ("vase, rim load", Vase(48, 36, false, 0.05)),
                     ("vase, self-weight + rim load", Vase(48, 36, true, 0.05)),
                 })
        {
            double r = SensitivityFilter.AutoRadius(m.Mesh), t0 = m.Thickness[0];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (keepK, itK) = new KarambaModel(m).Beso(m, 0.5, 0.02, 0.01, r, softKill);
            long msK = sw.ElapsedMilliseconds;
            sw.Restart();
            var opt = new BesoOptimizer(m, new BesoSettings
            {
                Mode = BesoMode.Void, VolumeFraction = 0.5, EvolutionRate = 0.02, MaxAdmissionRatio = 0.01, FilterRadius = r,
            });
            while (!opt.Finished) opt.Step();
            long msO = sw.ElapsedMilliseconds;
            var keepO = opt.Solid();

            double[] Wall(bool[] keep) => keep.Select(k => k ? t0 : softKill * t0).ToArray();
            double fullO = Ours(m).C, fullK = new KarambaModel(m).Analyze().Compliance;
            double oo = Ours(m, Wall(keepO)).C / fullO, ok = new KarambaModel(m, Wall(keepO)).Analyze().Compliance / fullK;
            double ko = Ours(m, Wall(keepK)).C / fullO, kk = new KarambaModel(m, Wall(keepK)).Analyze().Compliance / fullK;
            double same = keepO.Zip(keepK).Count(p => p.First == p.Second) / (double)m.ElementCount;
            Console.WriteLine($"{name}");
            Console.WriteLine($"   ours:    {opt.Iteration,3} iterations, {msO / 1000.0,5:F1} s, converged {opt.Converged,-5}  design rated {oo:F3} by ours, {ok:F3} by karamba");
            Console.WriteLine($"   karamba: {itK,3} iterations, {msK / 1000.0,5:F1} s                    design rated {ko:F3} by ours, {kk:F3} by karamba");
            // Different algorithms may land on different designs, but ours should not be clearly worse.
            Console.WriteLine($"   same keep/remove choice on {same:P0} of faces; ours vs karamba design {Pct(ok / kk - 1)}  " +
                              (ok <= kk * 1.10 ? "ok" : Flag(2, 0)));
        }
    }
}
