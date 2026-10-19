using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Geometry;

namespace ClayBeso.Printing
{
    /// <summary>One non-planar layer toolpath on the shell mid-surface.</summary>
    public sealed class LayerPath
    {
        public int Layer { get; internal set; }
        public bool Closed { get; internal set; }
        public List<Vec3> Points { get; } = new List<Vec3>();

        /// <summary>Target layer height at each point [mm].</summary>
        public List<double> TargetHeight { get; } = new List<double>();

        /// <summary>Layer height the field actually produces at each point [mm].</summary>
        public List<double> RealisedHeight { get; } = new List<double>();

        /// <summary>Wall thickness (= bead width) at each point [mm].</summary>
        public List<double> Width { get; } = new List<double>();

        /// <summary>
        /// Bead cross-section at each point [mm^2] for the wall thickness and the realised layer
        /// height. Extruded volume per mm of path equals this area, so flow = area x feed.
        /// Empty if no thickness was given.
        /// </summary>
        public List<double> BeadArea { get; } = new List<double>();

        /// <summary>Unit wall normal (mid-surface) at each point.</summary>
        public List<Vec3> Normals { get; } = new List<Vec3>();
    }

    /// <summary>Extracts iso-curves of a <see cref="LayerField"/> as ordered polylines.</summary>
    public static class LayerSlicer
    {
        /// <param name="vertexThickness">Wall thickness per vertex (bead width); may be null.</param>
        /// <param name="up">Build direction, used to orient closed loops consistently.</param>
        /// <param name="seamDirection">Closed loops start at the point furthest along this direction.</param>
        public static List<LayerPath> Slice(LayerField field, IList<double> vertexThickness, Vec3 up, Vec3 seamDirection)
        {
            var mesh = field.Mesh;
            var phi = field.Phi;
            var realised = field.RealisedFaceHeight();
            var paths = new List<LayerPath>();

            double maxPhi = phi.Where(p => !double.IsInfinity(p)).Max();
            for (int k = 1; k <= field.LayerCount; k++)
            {
                // The top rim sits exactly on phi = LayerCount in conforming mode; nudge inside.
                double level = Math.Min(k, maxPhi - 1e-6);
                foreach (var p in Contour(mesh, phi, level, field.TargetHeight, vertexThickness, realised))
                {
                    p.Layer = k;
                    if (p.Closed) OrientLoop(p, up, seamDirection);
                    paths.Add(p);
                }
            }
            return paths;
        }

        private struct Crossing
        {
            public Vec3 Point;
            public double H, T;
            public Vec3 N;
        }

        private static IEnumerable<LayerPath> Contour(
            TriMesh mesh, double[] phi, double level, double[] h, IList<double> thick, double[] realisedFace)
        {
            int n = mesh.VertexCount;
            var crossings = new Dictionary<long, Crossing>();
            var segFaces = new Dictionary<long, List<int>>();   // edge key -> faces using it
            var faceSeg = new Dictionary<int, (long, long)>();

            long Key(int a, int b) => a < b ? (long)a * n + b : (long)b * n + a;
            bool Above(int v) => phi[v] >= level; // ties count as above -> no degenerate segments

            for (int f = 0; f < mesh.FaceCount; f++)
            {
                var t = mesh.Faces[f];
                if (double.IsInfinity(phi[t[0]]) || double.IsInfinity(phi[t[1]]) || double.IsInfinity(phi[t[2]])) continue;
                var keys = new List<long>(2);
                for (int e = 0; e < 3; e++)
                {
                    int a = t[e], b = t[(e + 1) % 3];
                    if (Above(a) == Above(b)) continue;
                    long key = Key(a, b);
                    if (!crossings.ContainsKey(key))
                    {
                        double s = (level - phi[a]) / (phi[b] - phi[a]);
                        crossings[key] = new Crossing
                        {
                            Point = mesh.Vertices[a] + (mesh.Vertices[b] - mesh.Vertices[a]) * s,
                            H = h[a] + (h[b] - h[a]) * s,
                            T = thick == null ? 0 : thick[a] + (thick[b] - thick[a]) * s,
                            N = (mesh.VertexNormals[a] * (1 - s) + mesh.VertexNormals[b] * s).Unit(),
                        };
                    }
                    keys.Add(key);
                }
                if (keys.Count != 2) continue;
                faceSeg[f] = (keys[0], keys[1]);
                foreach (var key in keys)
                {
                    if (!segFaces.TryGetValue(key, out var list)) segFaces[key] = list = new List<int>(2);
                    list.Add(f);
                }
            }

            // Realised height at a crossing: mean over the (up to two) faces sharing the edge.
            double EdgeHeight(long key)
            {
                double s = 0; int c = 0;
                foreach (int f in segFaces[key])
                    if (!double.IsInfinity(realisedFace[f])) { s += realisedFace[f]; c++; }
                return c > 0 ? s / c : double.NaN;
            }

            var used = new HashSet<int>();
            // Start open chains from endpoints (edge used by a single segment) first, then loops.
            var starts = segFaces.Where(kv => kv.Value.Count == 1).Select(kv => kv.Value[0])
                .Concat(faceSeg.Keys).ToList();

            foreach (int f0 in starts)
            {
                if (used.Contains(f0)) continue;
                var path = new LayerPath();
                var (k0, k1) = faceSeg[f0];
                // If starting from an open end, begin at the dangling edge.
                long startKey = segFaces[k0].Count == 1 ? k0 : (segFaces[k1].Count == 1 ? k1 : k0);
                long curKey = startKey;
                int face = f0;
                Add(path, crossings[curKey], EdgeHeight(curKey));
                while (true)
                {
                    used.Add(face);
                    var (a, b) = faceSeg[face];
                    long next = a == curKey ? b : a;
                    if (next == startKey) { path.Closed = true; break; }
                    Add(path, crossings[next], EdgeHeight(next));
                    curKey = next;
                    int nextFace = -1;
                    foreach (int g in segFaces[curKey]) if (!used.Contains(g)) { nextFace = g; break; }
                    if (nextFace < 0) break;
                    face = nextFace;
                }
                if (thick != null)
                    for (int i = 0; i < path.Points.Count; i++)
                        path.BeadArea.Add(Bead.Area(path.Width[i], path.RealisedHeight[i]));
                if (path.Points.Count >= 2) yield return path;
            }
        }

        private static void Add(LayerPath p, Crossing c, double realised)
        {
            p.Points.Add(c.Point);
            p.Normals.Add(c.N);
            p.TargetHeight.Add(c.H);
            p.Width.Add(c.T);
            p.RealisedHeight.Add(realised);
        }

        /// <summary>Counter-clockwise about the build axis; start at the seam direction.</summary>
        private static void OrientLoop(LayerPath p, Vec3 up, Vec3 seam)
        {
            int m = p.Points.Count;
            var centre = Vec3.Zero;
            foreach (var q in p.Points) centre += q;
            centre /= m;

            var area = Vec3.Zero;
            for (int i = 0; i < m; i++)
                area += Vec3.Cross(p.Points[i] - centre, p.Points[(i + 1) % m] - centre);
            if (Vec3.Dot(area, up) < 0) ReverseAll(p);

            int start = 0;
            double best = double.NegativeInfinity;
            for (int i = 0; i < m; i++)
            {
                double d = Vec3.Dot(p.Points[i] - centre, seam);
                if (d > best) { best = d; start = i; }
            }
            if (start > 0) RotateAll(p, start);
        }

        private static void ReverseAll(LayerPath p)
        {
            p.Points.Reverse(); p.TargetHeight.Reverse(); p.Width.Reverse(); p.RealisedHeight.Reverse(); p.BeadArea.Reverse(); p.Normals.Reverse();
        }

        private static void RotateAll(LayerPath p, int start)
        {
            Rotate(p.Points, start); Rotate(p.TargetHeight, start); Rotate(p.Width, start); Rotate(p.RealisedHeight, start); Rotate(p.BeadArea, start); Rotate(p.Normals, start);
        }

        private static void Rotate<T>(List<T> list, int start)
        {
            var copy = list.Skip(start).Concat(list.Take(start)).ToList();
            list.Clear();
            list.AddRange(copy);
        }
    }
}
