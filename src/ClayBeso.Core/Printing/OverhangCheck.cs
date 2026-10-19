using System;
using System.Collections.Generic;
using ClayBeso.Geometry;

namespace ClayBeso.Printing
{
    /// <summary>Printability of one layer path, per point.</summary>
    public sealed class OverhangResult
    {
        /// <summary>
        /// Wall lean from vertical [deg], in the vertical plane across the bead (perpendicular to the
        /// path). 0 = vertical wall; grows with overhang (or underhang).
        /// </summary>
        public List<double> Lean { get; } = new List<double>();

        /// <summary>Slope of the layer along the path [deg]. Steep tilts make a non-tilting nozzle scrape.</summary>
        public List<double> Tilt { get; } = new List<double>();

        /// <summary>Horizontal offset of this bead past the one below [mm] = dz * tan(lean).</summary>
        public List<double> Offset { get; } = new List<double>();

        /// <summary>Offset / bead width: the unsupported fraction of the bead (NaN without a width).</summary>
        public List<double> Ratio { get; } = new List<double>();
    }

    /// <summary>
    /// Overhang and layer-tilt check of non-planar layer paths. Lowering the layer height where the
    /// wall leans (as non-planar layers can) reduces the bead offset dz * tan(lean), which is what
    /// lets a bead rest on the one below.
    /// </summary>
    public static class OverhangCheck
    {
        /// <param name="verticalHeights">True if the path's realised heights are vertical spacings (Vertical on).</param>
        public static OverhangResult Evaluate(LayerPath path, Vec3 up, bool verticalHeights)
        {
            up = up.Unit();
            var r = new OverhangResult();
            int n = path.Points.Count;
            for (int i = 0; i < n; i++)
            {
                var t = Tangent(path, i);
                double tilt = Math.Asin(Math.Min(1, Math.Abs(Vec3.Dot(t, up)))) * 180 / Math.PI;

                var q = Vec3.Cross(up, t);          // horizontal, across the path
                double lean = double.NaN;
                if (q.Length > 1e-9 && i < path.Normals.Count)
                {
                    q = q.Unit();
                    var nrm = path.Normals[i];
                    lean = Math.Atan2(Math.Abs(Vec3.Dot(nrm, up)), Math.Abs(Vec3.Dot(nrm, q))) * 180 / Math.PI;
                }

                double h = i < path.RealisedHeight.Count ? path.RealisedHeight[i] : double.NaN;
                double rad = lean * Math.PI / 180;
                double dz = verticalHeights ? h : h * Math.Cos(rad);
                double offset = dz * Math.Tan(rad);
                double w = i < path.Width.Count ? path.Width[i] : 0;

                r.Tilt.Add(tilt);
                r.Lean.Add(lean);
                r.Offset.Add(offset);
                r.Ratio.Add(w > 0 ? offset / w : double.NaN);
            }
            return r;
        }

        private static Vec3 Tangent(LayerPath p, int i)
        {
            int n = p.Points.Count;
            if (n < 2) return new Vec3(1, 0, 0);
            Vec3 a, b;
            if (p.Closed)
            {
                a = p.Points[(i - 1 + n) % n];
                b = p.Points[(i + 1) % n];
            }
            else
            {
                a = p.Points[Math.Max(0, i - 1)];
                b = p.Points[Math.Min(n - 1, i + 1)];
            }
            return (b - a).Unit();
        }
    }
}
