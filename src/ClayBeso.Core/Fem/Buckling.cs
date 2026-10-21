using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Geometry;

namespace ClayBeso.Fem
{
    public sealed class BucklingSettings
    {
        /// <summary>Number of buckling modes to return (lowest load factors first).</summary>
        public int Modes { get; set; } = 1;

        /// <summary>Lanczos vectors per restart; 0 = automatic (max(60, 4 Modes + 20)).</summary>
        public int Steps { get; set; }

        /// <summary>Restarts before giving up (the result then reports Converged = false).</summary>
        public int MaxRestarts { get; set; } = 10;

        /// <summary>Relative eigen-residual |K^-1 M x - mu x| / |mu| that counts as converged.</summary>
        public double Tolerance { get; set; } = 1e-5;
    }

    public sealed class BucklingResult
    {
        /// <summary>The linear analysis whose membrane forces were used (the pre-buckling state).</summary>
        public AnalysisResult Analysis { get; internal set; }

        /// <summary>
        /// Buckling load factors, lowest first: the loads (self-weight and point loads together) times this
        /// factor make the shell buckle. Below 1 = it buckles under the loads as given. Empty when nothing
        /// is in compression (no buckling possible).
        /// </summary>
        public double[] Factors { get; internal set; } = new double[0];

        /// <summary>Buckling shapes per mode: translation per node, scaled so the largest is 1.</summary>
        public Vec3[][] Modes { get; internal set; } = new Vec3[0][];

        public bool Converged { get; internal set; }

        /// <summary>Linear solves used (one per Lanczos step).</summary>
        public int Iterations { get; internal set; }

        /// <summary>Lowest load factor estimate as the iteration went on (for checking convergence).</summary>
        public List<double> History { get; } = new List<double>();

        /// <summary>Lowest load factor, +infinity when there is no buckling.</summary>
        public double Critical => Factors.Length > 0 ? Factors[0] : double.PositiveInfinity;
    }

    /// <summary>
    /// Linear buckling (eigenvalue) analysis: find the smallest lambda with (K + lambda Kg) phi = 0, where
    /// Kg is the geometric stiffness of the membrane forces of a linear analysis. Compression (N &lt; 0)
    /// makes Kg negative, i.e. softens the wall; lambda is how many times the loads it takes until the
    /// softened stiffness reaches zero.
    ///
    /// Kg uses the classic flat-shell form with linear interpolation of all three translations:
    /// Kg_ab = A * grad(N_a)^T [Nx Nxy; Nxy Ny] grad(N_b) * I3. It is the same for each of the three
    /// translation components, so it needs no rotation to global axes.
    ///
    /// Solved with restarted Lanczos on K^-1 M, M = -Kg, which is symmetric in the K inner product, with
    /// full re-orthogonalisation. M is indefinite (shells usually have tension somewhere too); the largest
    /// positive eigenvalues mu give the smallest positive load factors lambda = 1/mu. Lanczos copes well
    /// with the clustered buckling loads of shells (e.g. cylinders), where subspace iteration crawls.
    ///
    /// Linear buckling assumes a perfect shape and small deflections before buckling. Real shells,
    /// and wet clay especially, buckle at a fraction of it (imperfections), so treat a factor of a few as
    /// the safe side, not 1.
    /// </summary>
    public static class Buckling
    {
        public static BucklingResult Compute(ShellSolver solver, ElementScaling s, BucklingSettings o = null)
        {
            o = o ?? new BucklingSettings();
            var model = solver.Model;
            var r = solver.Solve(s);
            var res = new BucklingResult { Analysis = r };

            var h = GeometricBlocks(model, r, s);
            int n = 6 * model.NodeCount;
            var free = model.Fixed.Select(f => !f).ToArray();
            int neq = free.Count(f => f);
            int k = Math.Max(1, o.Modes);
            int m = Math.Min(o.Steps > 0 ? o.Steps : Math.Max(60, 4 * k + 20), neq);
            if (h.All(x => x == null)) { res.Converged = true; return res; }

            // M = -Kg: positive where the wall is compressed.
            double[] MulM(double[] x)
            {
                var y = new double[n];
                var els = model.Elements;
                for (int e = 0; e < els.Length; e++)
                {
                    var he = h[e];
                    if (he == null) continue;
                    var nd = els[e].Nodes;
                    for (int a = 0; a < 3; a++)
                    for (int b = 0; b < 3; b++)
                    {
                        double v = -he[3 * a + b];
                        if (v == 0) continue;
                        for (int c = 0; c < 3; c++) y[6 * nd[a] + c] += v * x[6 * nd[b] + c];
                    }
                }
                for (int d = 0; d < n; d++) if (!free[d]) y[d] = 0;
                return y;
            }

            // Start: the deflected shape plus some noise so every mode is present.
            var rng = new Random(12345);
            var start = (double[])r.U.Clone();
            double umax = start.Max(Math.Abs);
            if (umax == 0) umax = 1;
            for (int d = 0; d < n; d++) start[d] = free[d] ? start[d] + 0.1 * umax * (rng.NextDouble() - 0.5) : 0;

            double[] mu = new double[0];
            double[][] ritzVectors = new double[0][];
            for (int restart = 0; restart <= o.MaxRestarts; restart++)
            {
                var q = new List<double[]>();
                var kq = new List<double[]>();
                var alpha = new List<double>();
                var beta = new List<double>();

                var kv = solver.MultiplyK(start);
                double norm = Math.Sqrt(Dot(start, kv));
                q.Add(Scale(start, 1 / norm));
                kq.Add(Scale(kv, 1 / norm));

                bool done = false;
                for (int j = 0; j < m; j++)
                {
                    var mq = MulM(q[j]);
                    var w = solver.SolveExact(mq);
                    res.Iterations++;
                    alpha.Add(Dot(q[j], mq));
                    // Full re-orthogonalisation in the K inner product (twice is enough).
                    for (int pass = 0; pass < 2; pass++)
                        for (int i = 0; i < q.Count; i++)
                        {
                            double c = Dot(kq[i], w);
                            var qi = q[i];
                            for (int d = 0; d < n; d++) w[d] -= c * qi[d];
                        }
                    var kw = solver.MultiplyK(w);
                    double b = Math.Sqrt(Math.Max(0, Dot(w, kw)));

                    bool last = j == m - 1;
                    bool breakdown = b <= 1e-12 * Math.Max(1e-300, alpha.Max(Math.Abs));
                    if (j % 5 == 4 || last || breakdown)
                    {
                        // Ritz values of the tridiagonal T; the residual of each is |b * last component|.
                        int size = alpha.Count;
                        var t = new double[size, size];
                        for (int i = 0; i < size; i++)
                        {
                            t[i, i] = alpha[i];
                            if (i + 1 < size) t[i, i + 1] = t[i + 1, i] = beta[i];
                        }
                        Jacobi(t, out var vals, out var z);
                        var order = Enumerable.Range(0, size).OrderByDescending(i => vals[i]).ToArray();
                        mu = order.Select(i => vals[i]).ToArray();
                        double scale = mu.Max(Math.Abs);
                        res.History.Add(mu[0] > 0 ? 1 / mu[0] : double.PositiveInfinity);
                        bool ok = true;
                        for (int i = 0; i < Math.Min(k, size); i++)
                        {
                            double resid = Math.Abs(b * z[size - 1, order[i]]);
                            if (resid > o.Tolerance * Math.Max(Math.Abs(mu[i]), 1e-8 * scale)) { ok = false; break; }
                        }
                        ritzVectors = order.Take(Math.Min(k, size)).Select(c =>
                        {
                            var v = new double[n];
                            for (int i = 0; i < size; i++)
                            {
                                double zi = z[i, c];
                                var qi = q[i];
                                for (int d = 0; d < n; d++) v[d] += zi * qi[d];
                            }
                            return v;
                        }).ToArray();
                        if (ok || breakdown) { done = true; res.Converged = true; break; }
                    }
                    beta.Add(b);
                    q.Add(Scale(w, 1 / b));
                    kq.Add(Scale(kw, 1 / b));
                }
                if (done) break;
                // Restart from the wanted Ritz vectors.
                start = new double[n];
                foreach (var v in ritzVectors)
                    for (int d = 0; d < n; d++) start[d] += v[d];
            }

            // Positive mu only (compression); ignore values that are round-off of a tension-only state.
            double top = mu.Length > 0 ? mu.Max(Math.Abs) : 0;
            var factors = new List<double>();
            var modes = new List<Vec3[]>();
            for (int i = 0; i < ritzVectors.Length; i++)
            {
                if (mu[i] <= 1e-12 * top || mu[i] <= 0) break;
                factors.Add(1.0 / mu[i]);
                modes.Add(Shape(ritzVectors[i], model.NodeCount));
            }
            res.Factors = factors.ToArray();
            res.Modes = modes.ToArray();
            return res;
        }

        private static double[] Scale(double[] v, double f)
        {
            var r = new double[v.Length];
            for (int i = 0; i < v.Length; i++) r[i] = v[i] * f;
            return r;
        }

        /// <summary>
        /// Per element, the 3x3 node-node blocks of the geometric stiffness (same for x, y and z), from the
        /// membrane forces of the analysis. Null for elements without membrane force.
        /// </summary>
        public static double[][] GeometricBlocks(ShellModel model, AnalysisResult r, ElementScaling s)
        {
            var els = model.Elements;
            var h = new double[els.Length][];
            var ue = new double[18];
            for (int e = 0; e < els.Length; e++)
            {
                var el = els[e];
                ShellSolver.Gather(el, r.U, ue);
                el.UnitResultants(ue, out var nu, out _, s.Across[e]);
                double nx = s.Membrane[e] * nu[0], ny = s.Membrane[e] * nu[1], nxy = s.Membrane[e] * nu[2];
                if (nx == 0 && ny == 0 && nxy == 0) continue;
                h[e] = ElementBlocks(el.X, el.Y, el.Area, nx, ny, nxy);
            }
            return h;
        }

        /// <summary>Kg node-node blocks A * grad(N_a)^T S grad(N_b) for one flat triangle (local coordinates).</summary>
        public static double[] ElementBlocks(double[] x, double[] y, double area, double nx, double ny, double nxy)
        {
            var gx = new double[3];
            var gy = new double[3];
            for (int a = 0; a < 3; a++)
            {
                int b = (a + 1) % 3, c = (a + 2) % 3;
                gx[a] = (y[b] - y[c]) / (2 * area);
                gy[a] = (x[c] - x[b]) / (2 * area);
            }
            var h = new double[9];
            for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
                h[3 * a + b] = area * (gx[a] * (nx * gx[b] + nxy * gy[b]) + gy[a] * (nxy * gx[b] + ny * gy[b]));
            return h;
        }

        private static Vec3[] Shape(double[] v, int nodes)
        {
            var m = new Vec3[nodes];
            double big = 0;
            int at = 0;
            for (int i = 0; i < nodes; i++)
            {
                m[i] = new Vec3(v[6 * i], v[6 * i + 1], v[6 * i + 2]);
                if (m[i].Length > big) { big = m[i].Length; at = i; }
            }
            if (big == 0) return m;
            // Sign: the largest movement points the way of its biggest component (deterministic output).
            var w = m[at];
            double ax = Math.Abs(w.X), ay = Math.Abs(w.Y), az = Math.Abs(w.Z);
            double lead = ax >= ay && ax >= az ? w.X : (ay >= az ? w.Y : w.Z);
            double f = (lead < 0 ? -1 : 1) / big;
            for (int i = 0; i < nodes; i++) m[i] = m[i] * f;
            return m;
        }

        private static double Dot(double[] a, double[] b)
        {
            double s = 0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        /// <summary>Cyclic Jacobi eigen-decomposition of a small symmetric matrix (destroys a).</summary>
        private static void Jacobi(double[,] a, out double[] values, out double[,] v)
        {
            int p = a.GetLength(0);
            v = new double[p, p];
            for (int i = 0; i < p; i++) v[i, i] = 1;
            for (int sweep = 0; sweep < 100; sweep++)
            {
                double off = 0, diag = 0;
                for (int i = 0; i < p; i++)
                {
                    diag += a[i, i] * a[i, i];
                    for (int j = i + 1; j < p; j++) off += a[i, j] * a[i, j];
                }
                if (off <= 1e-30 * Math.Max(diag, 1e-300)) break;
                for (int i = 0; i < p - 1; i++)
                for (int j = i + 1; j < p; j++)
                {
                    if (a[i, j] == 0) continue;
                    double theta = (a[j, j] - a[i, i]) / (2 * a[i, j]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < p; k++)
                    {
                        double aki = a[k, i], akj = a[k, j];
                        a[k, i] = c * aki - s * akj;
                        a[k, j] = s * aki + c * akj;
                    }
                    for (int k = 0; k < p; k++)
                    {
                        double aik = a[i, k], ajk = a[j, k];
                        a[i, k] = c * aik - s * ajk;
                        a[j, k] = s * aik + c * ajk;
                    }
                    for (int k = 0; k < p; k++)
                    {
                        double vki = v[k, i], vkj = v[k, j];
                        v[k, i] = c * vki - s * vkj;
                        v[k, j] = s * vki + c * vkj;
                    }
                }
            }
            values = new double[p];
            for (int i = 0; i < p; i++) values[i] = a[i, i];
        }
    }
}
