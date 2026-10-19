using System;
using System.Collections.Generic;
using ClayBeso.Geometry;

namespace ClayBeso.Printing
{
    /// <summary>
    /// Fast marching on a triangle mesh: solves |grad T| = F (slowness) with T = 0 on the seeds.
    /// Uses the planar-wavefront triangle update, falling back to edge (Dijkstra) updates
    /// when the upwind direction leaves the triangle.
    /// </summary>
    public static class FastMarching
    {
        public static double[] Solve(TriMesh mesh, IList<double> slowness, IList<int> seeds)
        {
            int n = mesh.VertexCount;
            var t = new double[n];
            var known = new bool[n];
            for (int i = 0; i < n; i++) t[i] = double.PositiveInfinity;

            var heap = new MinHeap();
            foreach (int s in seeds)
            {
                t[s] = 0;
                heap.Push(0, s);
            }

            var vf = mesh.VertexFaces;
            while (heap.Count > 0)
            {
                var (tv, v) = heap.Pop();
                if (known[v] || tv > t[v]) continue;
                known[v] = true;

                foreach (int f in vf[v])
                {
                    var tri = mesh.Faces[f];
                    for (int k = 0; k < 3; k++)
                    {
                        int c = tri[k];
                        if (known[c]) continue;
                        int a = tri[(k + 1) % 3], b = tri[(k + 2) % 3];
                        double cand = Update(mesh, slowness, t, known, c, a, b);
                        if (cand < t[c])
                        {
                            t[c] = cand;
                            heap.Push(cand, c);
                        }
                    }
                }
            }
            return t;
        }

        private static double Update(TriMesh mesh, IList<double> s, double[] t, bool[] known, int c, int a, int b)
        {
            bool ka = known[a], kb = known[b];
            var pc = mesh.Vertices[c];
            var va = mesh.Vertices[a] - pc;
            var vb = mesh.Vertices[b] - pc;
            double best = double.PositiveInfinity;
            if (ka) best = Math.Min(best, t[a] + 0.5 * (s[a] + s[c]) * va.Length);
            if (kb) best = Math.Min(best, t[b] + 0.5 * (s[b] + s[c]) * vb.Length);
            if (!(ka && kb)) return best;

            double F = (s[a] + s[b] + s[c]) / 3.0;

            // Plane wave T(p) = Tc + g.(p - c), |g| = F, through T(a), T(b).
            double aa = Vec3.Dot(va, va), ab = Vec3.Dot(va, vb), bb = Vec3.Dot(vb, vb);
            double det = aa * bb - ab * ab;
            if (det <= 1e-20 * aa * bb) return best;
            // Q = inverse Gram matrix
            double q11 = bb / det, q12 = -ab / det, q22 = aa / det;
            double ta = t[a], tb = t[b];

            double A = q11 + 2 * q12 + q22;                       // 1'Q1
            double B = q11 * ta + q12 * (ta + tb) + q22 * tb;     // 1'Qt
            double C = q11 * ta * ta + 2 * q12 * ta * tb + q22 * tb * tb - F * F; // t'Qt - F^2
            double disc = B * B - A * C;
            if (disc < 0) return best;
            double tc = (B + Math.Sqrt(disc)) / A;

            // Upwind check: the characteristic into c must come from inside the triangle.
            double ra = tc - ta, rb = tc - tb;
            double ca = q11 * ra + q12 * rb, cb = q12 * ra + q22 * rb;
            if (ca >= 0 && cb >= 0 && tc >= Math.Max(ta, tb)) best = Math.Min(best, tc);
            return best;
        }

        /// <summary>Binary heap with lazy deletion (netstandard2.0 has no PriorityQueue).</summary>
        private sealed class MinHeap
        {
            private readonly List<(double key, int id)> _a = new List<(double, int)>();
            public int Count => _a.Count;

            public void Push(double key, int id)
            {
                _a.Add((key, id));
                int i = _a.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_a[p].key <= _a[i].key) break;
                    (_a[p], _a[i]) = (_a[i], _a[p]);
                    i = p;
                }
            }

            public (double key, int id) Pop()
            {
                var top = _a[0];
                int last = _a.Count - 1;
                _a[0] = _a[last];
                _a.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < _a.Count && _a[l].key < _a[m].key) m = l;
                    if (r < _a.Count && _a[r].key < _a[m].key) m = r;
                    if (m == i) break;
                    (_a[m], _a[i]) = (_a[i], _a[m]);
                    i = m;
                }
                return top;
            }
        }
    }
}
