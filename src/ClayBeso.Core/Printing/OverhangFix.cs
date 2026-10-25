using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Geometry;

namespace ClayBeso.Printing
{
    /// <summary>Result of <see cref="OverhangFix.Apply"/>.</summary>
    public sealed class OverhangFixResult
    {
        /// <summary>The corrected mid-surface (same vertices and faces, moved).</summary>
        public TriMesh Mesh { get; internal set; }

        /// <summary>How far each vertex moved [mm] (horizontal).</summary>
        public double[] Moved { get; internal set; }

        public double MaxMove => Moved.Length > 0 ? Moved.Max() : 0;
        public int MovedCount => Moved.Count(d => d > 1e-6);
    }

    /// <summary>
    /// Pulls overhanging parts of a vase-like wall back until it leans no more than a limit from vertical,
    /// so it can be printed without support. Vertices move only horizontally, along the radius from the
    /// vase axis at their height, and only as far as needed; the base and every part that is already
    /// printable stay where they are.
    /// <para>
    /// The rule is the usual self-supporting one: a point of the wall can be printed if it is within
    /// tan(max lean) x the height of one step (about one mesh row) of the wall one step below it. Sweeping
    /// upwards, the wall around each vertex is cut at the height one step down, and the vertex is compared
    /// with where its radius crosses that cut; if it sticks out (or in) too far, it is moved along the
    /// radius onto the limit. Moving along the radius keeps neighbouring vertices from crossing on sharp
    /// or thin parts. The corrections are then smoothed and the sweep repeated, so the result has no kinks.
    /// </para>
    /// <para>
    /// Made for vases: closed rings around a vertical axis, open top, base on the bed, no floor. Where the
    /// wall does not face away from the axis (the flat sides of a thin oval), the radial distance is larger
    /// than the true overhang, so the correction there is on the safe side.
    /// </para>
    /// </summary>
    public static class OverhangFix
    {
        /// <param name="maxLean">Largest allowed wall lean from vertical [deg].</param>
        /// <param name="margin">Aim this many degrees below <paramref name="maxLean"/> (the mesh only approximates the wall) [deg].</param>
        /// <param name="baseTolerance">Vertices within this height of the lowest point are the base and stay put [mm].</param>
        public static OverhangFixResult Apply(TriMesh mesh, double maxLean = 45, double margin = 3,
            double baseTolerance = 0.5, int passes = 3, int smoothing = 5)
        {
            if (maxLean <= 0 || maxLean >= 89) throw new ArgumentException("Max lean must be between 0 and 89°.");
            int n = mesh.VertexCount;
            var original = mesh.Vertices;
            var p = (Vec3[])original.Clone();
            double tan = Math.Tan(Math.Max(1, maxLean - margin) * Math.PI / 180);
            double zMin = original.Min(v => v.Z);
            var fixedBase = original.Select(v => v.Z <= zMin + baseTolerance).ToArray();
            var nb = mesh.VertexNeighbours();
            var vf = mesh.VertexFaces;

            // The step: a typical rise of an edge going up the wall (about one row of the mesh).
            var rises = new List<double>();
            foreach (var c in mesh.Faces)
                for (int k = 0; k < 3; k++)
                {
                    var e = original[c[(k + 1) % 3]] - original[c[k]];
                    if (Math.Abs(e.Z) > 0.3 * e.Length) rises.Add(Math.Abs(e.Z));
                }
            double step = rises.Count > 0 ? rises.OrderBy(x => x).ElementAt(rises.Count / 2) : 1.0;

            // Faces within two rings of each vertex: where to cut the wall one step down.
            var near = new int[n][];
            for (int i = 0; i < n; i++)
            {
                var set = new HashSet<int>();
                foreach (int j in nb[i].Append(i))
                    foreach (int f in vf[j]) set.Add(f);
                near[i] = set.ToArray();
            }

            // The vase axis at each vertex's height: the mean of the vertices within half a step of it.
            var centre = Centres(original, 0.5 * step);

            var order = Enumerable.Range(0, n).OrderBy(i => original[i].Z).ToArray();
            for (int pass = 0; pass < passes; pass++)
            {
                Sweep(p, order, mesh.Faces, near, centre, fixedBase, tan, step, zMin);
                if (pass == passes - 1) break;
                // Smooth the correction field (not the shape), keeping the base still.
                for (int it = 0; it < smoothing; it++)
                {
                    var d = new Vec3[n];
                    for (int i = 0; i < n; i++)
                    {
                        if (fixedBase[i] || nb[i].Count == 0) { d[i] = p[i] - original[i]; continue; }
                        var sum = p[i] - original[i];
                        foreach (int j in nb[i]) sum += p[j] - original[j];
                        d[i] = sum / (nb[i].Count + 1);
                    }
                    for (int i = 0; i < n; i++)
                        if (!fixedBase[i]) p[i] = original[i] + new Vec3(d[i].X, d[i].Y, 0);
                }
            }

            return new OverhangFixResult
            {
                Mesh = new TriMesh(p, mesh.Faces),
                Moved = Enumerable.Range(0, n).Select(i => (p[i] - original[i]).Length).ToArray(),
            };
        }

        /// <summary>Mean x, y of the vertices within <paramref name="band"/> of each vertex's height.</summary>
        private static Vec3[] Centres(Vec3[] v, double band)
        {
            int n = v.Length;
            var byZ = Enumerable.Range(0, n).OrderBy(i => v[i].Z).ToArray();
            var c = new Vec3[n];
            int lo = 0, hi = 0;
            double sx = 0, sy = 0;
            foreach (int i in byZ)
            {
                double z = v[i].Z;
                while (hi < n && v[byZ[hi]].Z <= z + band) { sx += v[byZ[hi]].X; sy += v[byZ[hi]].Y; hi++; }
                while (v[byZ[lo]].Z < z - band) { sx -= v[byZ[lo]].X; sy -= v[byZ[lo]].Y; lo++; }
                int count = hi - lo;
                c[i] = new Vec3(sx / count, sy / count, z);
            }
            return c;
        }

        /// <summary>Moves each vertex (bottom to top) along its radius until the wall one step below supports it.</summary>
        private static void Sweep(Vec3[] p, int[] order, int[][] faces, int[][] near, Vec3[] centre, bool[] fixedBase,
            double tan, double step, double zMin)
        {
            double eps = 1e-9 * step;
            var cut = new List<Vec3>(3);
            foreach (int v in order)
            {
                if (fixedBase[v]) continue;
                var pv = p[v];
                double zc = Math.Max(pv.Z - step, zMin);      // one step down, but not below the bed
                double dz = pv.Z - zc;
                if (dz <= eps) continue;
                var c = centre[v];
                var radial = new Vec3(pv.X - c.X, pv.Y - c.Y, 0);
                double r = radial.Length;
                if (r <= eps) continue;                         // on the axis: no radius to move along
                radial = radial / r;

                // Where the radius crosses the wall at height zc: the crossing nearest to the vertex.
                double best = double.NaN;
                foreach (int f in near[v])
                {
                    if (!CutFace(p, faces[f], zc, eps, cut)) continue;
                    double s = RayHit(c, radial, cut[0], cut[1]);
                    if (!double.IsNaN(s) && (double.IsNaN(best) || Math.Abs(s - r) < Math.Abs(best - r))) best = s;
                }
                if (double.IsNaN(best)) continue;

                double off = r - best;                          // > 0: sticks out, < 0: leans in
                double limit = tan * dz;
                if (Math.Abs(off) <= limit) continue;
                double rNew = best + Math.Sign(off) * limit;
                p[v] = new Vec3(c.X + radial.X * rNew, c.Y + radial.Y * rNew, pv.Z);
            }
        }

        /// <summary>The segment where a triangle crosses the plane z = <paramref name="zc"/> (false if it doesn't).</summary>
        private static bool CutFace(Vec3[] p, int[] f, double zc, double eps, List<Vec3> cut)
        {
            cut.Clear();
            for (int k = 0; k < 3; k++)
            {
                var a = p[f[k]];
                var b = p[f[(k + 1) % 3]];
                double da = a.Z - zc, db = b.Z - zc;
                if (Math.Abs(da) <= eps) { cut.Add(a); continue; }
                if (da * db < 0 && Math.Abs(db) > eps) cut.Add(a + (b - a) * (da / (da - db)));
            }
            return cut.Count >= 2;
        }

        /// <summary>Distance s along the ray c + s d (in plan) to segment ab, or NaN if it misses.</summary>
        private static double RayHit(Vec3 c, Vec3 d, Vec3 a, Vec3 b)
        {
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double den = d.X * ey - d.Y * ex;
            if (Math.Abs(den) < 1e-12) return double.NaN;
            double ax = a.X - c.X, ay = a.Y - c.Y;
            double s = (ax * ey - ay * ex) / den;
            double t = (ax * d.Y - ay * d.X) / den;
            return t >= -1e-9 && t <= 1 + 1e-9 && s > 0 ? s : double.NaN;
        }
    }
}
