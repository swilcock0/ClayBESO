using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Geometry;

namespace ClayBeso.Printing
{
    /// <summary>Where the nozzle is at one moment of a print.</summary>
    public sealed class PrintMoment
    {
        /// <summary>Index into <see cref="PrintTimeline.Paths"/> of the path being printed.</summary>
        public int Path { get; internal set; }

        /// <summary>Layer number (1 = first layer) being printed.</summary>
        public int Layer { get; internal set; }

        /// <summary>Segment of the path being printed: between point Segment and Segment + 1 (wrapping on closed paths).</summary>
        public int Segment { get; internal set; }

        /// <summary>How far along that segment, 0-1.</summary>
        public double T { get; internal set; }

        /// <summary>Nozzle position [mm].</summary>
        public Vec3 Nozzle { get; internal set; }

        /// <summary>Path length deposited so far [mm].</summary>
        public double Length { get; internal set; }

        /// <summary>Print time so far [s] at the timeline's speed.</summary>
        public double Time { get; internal set; }
    }

    /// <summary>A triangle mesh with one value per vertex, for previews.</summary>
    public sealed class ValueMesh
    {
        public List<Vec3> Vertices { get; } = new List<Vec3>();
        public List<int[]> Faces { get; } = new List<int[]>();

        /// <summary>One value per vertex (what the preview colours by).</summary>
        public List<double> Values { get; } = new List<double>();
    }

    /// <summary>
    /// The order and timing of a print: the layer paths one after another, at constant nozzle speed.
    /// Travel between layers is ignored (the next layer starts where the last one ends, at the seam).
    /// </summary>
    public sealed class PrintTimeline
    {
        private readonly double[] _start;      // deposited length at the start of each path
        private readonly double[][] _along;    // per path: length at each point (closed: plus the closing point)

        public PrintPlan Plan { get; }

        /// <summary>Paths in print order (by layer).</summary>
        public IReadOnlyList<LayerPath> Paths { get; }

        /// <summary>Nozzle speed along the path [mm/s].</summary>
        public double Speed { get; }

        /// <summary>Total deposited path length [mm].</summary>
        public double TotalLength { get; }

        /// <summary>Total print time [s] at <see cref="Speed"/>.</summary>
        public double TotalTime => TotalLength / Speed;

        public PrintTimeline(PrintPlan plan, double speed = 25)
        {
            if (speed <= 0) throw new ArgumentException("Print speed must be positive.");
            Plan = plan;
            Speed = speed;
            Paths = plan.Paths.Where(p => p.Points.Count > 1).OrderBy(p => p.Layer).ToList();
            _start = new double[Paths.Count];
            _along = new double[Paths.Count][];
            double total = 0;
            for (int i = 0; i < Paths.Count; i++)
            {
                var p = Paths[i];
                int n = p.Points.Count, m = p.Closed ? n + 1 : n;
                var a = new double[m];
                for (int k = 1; k < m; k++) a[k] = a[k - 1] + (p.Points[k % n] - p.Points[k - 1]).Length;
                _along[i] = a;
                _start[i] = total;
                total += a[m - 1];
            }
            TotalLength = total;
        }

        /// <summary>The moment a <paramref name="fraction"/> (0-1) of the print time has passed.</summary>
        public PrintMoment At(double fraction)
        {
            if (Paths.Count == 0) throw new InvalidOperationException("The print has no paths.");
            double f = double.IsNaN(fraction) ? 1 : Math.Max(0, Math.Min(1, fraction));
            double length = f * TotalLength;

            // Last path that starts at or before this length.
            int i = Array.BinarySearch(_start, length);
            if (i < 0) i = ~i - 1;
            i = Math.Max(0, Math.Min(Paths.Count - 1, i));
            // At an exact boundary, a path that has not started is not being printed: stay on the earlier one.
            while (i > 0 && _start[i] >= length && length > 0) i--;

            var a = _along[i];
            double local = Math.Min(length - _start[i], a[a.Length - 1]);
            int seg = Array.BinarySearch(a, local);
            if (seg < 0) seg = ~seg - 1;
            seg = Math.Max(0, Math.Min(a.Length - 2, seg));
            double segLen = a[seg + 1] - a[seg];
            double t = segLen > 0 ? (local - a[seg]) / segLen : 0;

            var pts = Paths[i].Points;
            var p0 = pts[seg % pts.Count];
            var p1 = pts[(seg + 1) % pts.Count];
            return new PrintMoment
            {
                Path = i,
                Layer = Paths[i].Layer,
                Segment = seg,
                T = t,
                Nozzle = p0 + (p1 - p0) * t,
                Length = length,
                Time = length / Speed,
            };
        }

        /// <summary>
        /// The beads deposited up to <paramref name="fraction"/> of the print: every path's bead (wall
        /// thickness wide, realised layer height tall, stadium cross-section) swept along it, below the
        /// toolpath, with the path being printed cut at the nozzle.
        /// </summary>
        /// <param name="value">Value per (path index, point index) stored on the vertices; null = layer number.</param>
        /// <param name="segments">Segments per rounded bead side (more = smoother, heavier).</param>
        public ValueMesh Beads(double fraction, Func<int, int, double> value = null, int segments = 4)
        {
            var now = At(fraction);
            var mesh = new ValueMesh();
            for (int i = 0; i <= now.Path; i++)
            {
                bool current = i == now.Path;
                var p = Paths[i];
                int n = p.Points.Count;
                // Stations along the path: point indices, plus the nozzle position on the current path.
                var stations = new List<(int k, double t)>();
                int last = current ? now.Segment : (p.Closed ? n : n - 1);
                for (int k = 0; k <= last; k++) stations.Add((k, 0));
                if (current && now.T > 0) stations.Add((now.Segment, now.T));
                if (current && stations.Count < 2) continue;   // nothing of this path printed yet
                bool loop = p.Closed && !current;              // full closed loop: last station = first point
                Sweep(mesh, p, i, stations, loop, value, segments);
            }
            return mesh;
        }

        private static void Sweep(ValueMesh mesh, LayerPath p, int pathIndex, List<(int k, double t)> stations,
            bool loop, Func<int, int, double> value, int segments)
        {
            int n = p.Points.Count;
            Vec3 Pt(int k) => p.Points[((k % n) + n) % n];
            double Lerp(IList<double> v, int k, double t, double fallback)
            {
                if (v == null || v.Count != n) return fallback;
                double a = v[k % n], b = v[(k + 1) % n];
                return a + (b - a) * t;
            }

            int ring = 0, firstRing = -1, prevRing = -1;
            int stationCount = loop ? stations.Count - 1 : stations.Count;   // loop: the closing station reuses ring 0
            for (int s = 0; s < stationCount; s++)
            {
                var (k, t) = stations[s];
                var pos = Pt(k) + (Pt(k + 1) - Pt(k)) * t;

                // Tangent from the neighbouring points (one-sided at the ends of an open path).
                bool open = !p.Closed;
                var ahead = open && k >= n - 1 ? Pt(k) : Pt(k + 1);
                var behind = open && k <= 0 ? Pt(k) : Pt(k - 1);
                var tangent = (ahead - behind).Unit();
                if (tangent.Length == 0) tangent = new Vec3(1, 0, 0);

                // Width across the wall (normal), height across the layers (in the wall, perpendicular to the path).
                var normal = Lerp3(p.Normals, k, t, n);
                normal = (normal - tangent * Vec3.Dot(normal, tangent)).Unit();
                if (normal.Length == 0) normal = Vec3.Cross(Vec3.UnitZ, tangent).Unit();
                // (normal, up, tangent) right-handed, with up pointing upwards (flip the normal to keep handedness).
                if (Vec3.Cross(tangent, normal).Z < 0) normal = -normal;
                var up = Vec3.Cross(tangent, normal).Unit();

                double w = Lerp(p.Width, k, t, 1.0);
                double h = Lerp(p.RealisedHeight, k, t, 1.0);
                if (!(h > 0)) h = Lerp(p.TargetHeight, k, t, 1.0);
                if (!(w > 0)) w = h;
                double val = value == null ? p.Layer : value(pathIndex, k % n) + ((value(pathIndex, (k + 1) % n) - value(pathIndex, k % n)) * t);

                // The bead sits below the toolpath: from the previous layer (pos - h up) to the path.
                var centre = pos - up * (h / 2);
                ring = mesh.Vertices.Count;
                foreach (var (u, v) in Profile(w, h, segments))
                {
                    mesh.Vertices.Add(centre + normal * u + up * v);
                    mesh.Values.Add(val);
                }
                if (firstRing < 0) firstRing = ring;
                if (prevRing >= 0) Bridge(mesh, prevRing, ring, segments);
                prevRing = ring;
            }
            if (loop && prevRing >= 0 && firstRing >= 0 && prevRing != firstRing)
                Bridge(mesh, prevRing, firstRing, segments);
            else if (firstRing >= 0 && prevRing != firstRing)
            {
                Cap(mesh, firstRing, segments, flip: true);
                Cap(mesh, prevRing, segments, flip: false);
            }
        }

        private static Vec3 Lerp3(IList<Vec3> v, int k, double t, int n)
        {
            if (v == null || v.Count != n) return Vec3.Zero;
            var a = v[k % n];
            var b = v[(k + 1) % n];
            return a + (b - a) * t;
        }

        /// <summary>
        /// Stadium cross-section of a bead, as (across the wall, across the layers) offsets from its centre [mm]:
        /// a rectangle w - h wide with semicircular sides of radius h/2, counter-clockwise, 2 (segments + 1) points.
        /// A bead narrower than it is tall is drawn as a rectangle, as in <see cref="Bead.Area"/>.
        /// </summary>
        public static List<(double u, double v)> Profile(double width, double height, int segments = 4)
        {
            segments = Math.Max(1, segments);
            var pts = new List<(double, double)>();
            double r = height / 2;
            if (width >= height)
            {
                double half = (width - height) / 2;
                for (int s = 0; s <= segments; s++)          // right side, bottom to top
                {
                    double a = -Math.PI / 2 + Math.PI * s / segments;
                    pts.Add((half + r * Math.Cos(a), r * Math.Sin(a)));
                }
                for (int s = 0; s <= segments; s++)          // left side, top to bottom
                {
                    double a = Math.PI / 2 + Math.PI * s / segments;
                    pts.Add((-half + r * Math.Cos(a), r * Math.Sin(a)));
                }
            }
            else
            {
                double hw = width / 2;
                for (int s = 0; s <= segments; s++) pts.Add((hw, -r + height * s / segments));
                for (int s = 0; s <= segments; s++) pts.Add((-hw, r - height * s / segments));
            }
            return pts;
        }

        private static void Bridge(ValueMesh mesh, int a, int b, int segments)
        {
            int m = 2 * (segments + 1);
            for (int j = 0; j < m; j++)
            {
                int j1 = (j + 1) % m;
                // Outward normals: the profile runs counter-clockwise looking back along the path.
                mesh.Faces.Add(new[] { a + j, b + j1, b + j });
                mesh.Faces.Add(new[] { a + j, a + j1, b + j1 });
            }
        }

        private static void Cap(ValueMesh mesh, int ring, int segments, bool flip)
        {
            int m = 2 * (segments + 1);
            for (int j = 1; j < m - 1; j++)
                mesh.Faces.Add(flip ? new[] { ring, ring + j + 1, ring + j } : new[] { ring, ring + j, ring + j + 1 });
        }
    }
}
