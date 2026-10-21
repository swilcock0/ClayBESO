using System;
using System.Collections.Generic;
using ClayBeso.Geometry;

namespace ClayBeso.Optimisation
{
    /// <summary>
    /// Linear-hat sensitivity filter over element centroids (Sigmund / Huang &amp; Xie style):
    /// a_i' = sum_j w_ij A_j a_j / sum_j w_ij A_j,  w_ij = max(0, r - |c_i - c_j|).
    /// Removes checkerboarding and makes the result independent of mesh density.
    /// Distances are Euclidean, which is fine as long as r is small compared with the
    /// gap between parts of the shell that are close in space but far apart along the surface.
    /// </summary>
    public sealed class SensitivityFilter
    {
        private readonly int[] _start;
        private readonly int[] _index;
        private readonly double[] _weight;

        public double Radius { get; }

        public SensitivityFilter(TriMesh mesh, double radius)
        {
            Radius = radius;
            int m = mesh.FaceCount;
            var c = new Vec3[m];
            for (int i = 0; i < m; i++) c[i] = mesh.FaceCentroid(i);
            var areas = mesh.FaceAreas;

            _start = new int[m + 1];
            var idx = new List<int>(m * 8);
            var wts = new List<double>(m * 8);

            if (radius <= 0)
            {
                for (int i = 0; i < m; i++) { _start[i] = i; idx.Add(i); wts.Add(1); }
                _start[m] = m;
                _index = idx.ToArray();
                _weight = wts.ToArray();
                return;
            }

            // Uniform spatial hash with cell size = radius.
            var grid = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Key(Vec3 p) =>
                ((int)Math.Floor(p.X / radius), (int)Math.Floor(p.Y / radius), (int)Math.Floor(p.Z / radius));
            for (int i = 0; i < m; i++)
            {
                var k = Key(c[i]);
                if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
                list.Add(i);
            }

            for (int i = 0; i < m; i++)
            {
                _start[i] = idx.Count;
                var (kx, ky, kz) = Key(c[i]);
                double sum = 0;
                int first = idx.Count;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                    foreach (int j in list)
                    {
                        double d = (c[i] - c[j]).Length;
                        if (d >= radius) continue;
                        double w = (radius - d) * areas[j];
                        idx.Add(j);
                        wts.Add(w);
                        sum += w;
                    }
                }
                for (int k = first; k < idx.Count; k++) wts[k] /= sum;
            }
            _start[m] = idx.Count;
            _index = idx.ToArray();
            _weight = wts.ToArray();
        }

        /// <summary>
        /// A filter radius that suits the mesh: <paramref name="edges"/> times the average edge length (in
        /// the mesh's units). About 3 edges removes checkerboards and stair-steps while keeping detail, and
        /// keeps the result about the same when the mesh is refined.
        /// </summary>
        public static double AutoRadius(TriMesh mesh, double edges = 3.0)
        {
            var seen = new HashSet<(int, int)>();
            double sum = 0;
            foreach (var f in mesh.Faces)
                for (int k = 0; k < 3; k++)
                {
                    int a = f[k], b = f[(k + 1) % 3];
                    if (!seen.Add(a < b ? (a, b) : (b, a))) continue;
                    sum += (mesh.Vertices[a] - mesh.Vertices[b]).Length;
                }
            return seen.Count > 0 ? edges * sum / seen.Count : 0;
        }

        public double[] Apply(double[] values)
        {
            int m = _start.Length - 1;
            var r = new double[m];
            for (int i = 0; i < m; i++)
            {
                double s = 0;
                for (int k = _start[i]; k < _start[i + 1]; k++) s += _weight[k] * values[_index[k]];
                r[i] = s;
            }
            return r;
        }

        /// <summary>
        /// Transpose: r_j = sum_i W_ij v_i. Used for the chain rule when the filter is applied to the
        /// design itself (density filter): dC/dx = W^T dC/dt.
        /// </summary>
        public double[] ApplyTranspose(double[] values)
        {
            int m = _start.Length - 1;
            var r = new double[m];
            for (int i = 0; i < m; i++)
            {
                double v = values[i];
                if (v == 0) continue;
                for (int k = _start[i]; k < _start[i + 1]; k++) r[_index[k]] += _weight[k] * v;
            }
            return r;
        }
    }
}
