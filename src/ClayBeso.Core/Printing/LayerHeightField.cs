using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Geometry;

namespace ClayBeso.Printing
{
    public enum LayerMapping
    {
        /// <summary>
        /// Constant bead cross-section (constant flow and feed): h is chosen so that the
        /// bead of width t has area A (see <see cref="Bead.Area"/>), roughly h = A / t. Thick wall regions
        /// get lower, more squashed layers, so the bead spreads wider. Thin regions get taller,
        /// narrower layers. The toolpath keeps one feed and one flow and only the nozzle height changes.
        /// </summary>
        ConstantBeadArea = 0,

        /// <summary>Linear remap: thickest wall -> hMin, thinnest wall -> hMax.</summary>
        LinearInverse = 1,

        /// <summary>
        /// h = k * t: every bead keeps the same height/width ratio (the same squash), which keeps
        /// stacking stability uniform. Needs flow control, because the bead area varies with t^2.
        /// </summary>
        ConstantAspectRatio = 2,
    }

    /// <summary>Turns a wall-thickness field into a per-vertex target layer height.</summary>
    public static class LayerHeightField
    {
        /// <param name="mesh">Shell mid-surface.</param>
        /// <param name="faceThickness">Wall thickness per face [mm] (0 for voids).</param>
        /// <param name="hMin">Smallest printable layer height [mm].</param>
        /// <param name="hMax">Largest stable layer height [mm].</param>
        /// <param name="beadArea">Bead cross-section [mm^2] for <see cref="LayerMapping.ConstantBeadArea"/>; &lt;= 0 picks one automatically.</param>
        /// <param name="smoothing">Laplacian smoothing passes on the result.</param>
        /// <param name="aspectRatio">Height/width ratio k for <see cref="LayerMapping.ConstantAspectRatio"/>.</param>
        public static double[] Compute(
            TriMesh mesh,
            IList<double> faceThickness,
            double hMin,
            double hMax,
            LayerMapping mapping,
            double beadArea,
            int smoothing,
            out double usedBeadArea,
            double aspectRatio = 0.4)
        {
            if (hMin <= 0 || hMax < hMin) throw new ArgumentException("Need 0 < hMin <= hMax.");
            var tv = mesh.FaceToVertex(faceThickness);
            var solid = tv.Where(t => t > 0).ToArray();
            if (solid.Length == 0) throw new ArgumentException("Thickness field is empty.");
            double tMin = solid.Min(), tMax = solid.Max();

            usedBeadArea = beadArea;
            var h = new double[mesh.VertexCount];
            if (mapping == LayerMapping.ConstantBeadArea)
            {
                if (beadArea <= 0)
                {
                    // Centre the range: the median wall gets the mid layer height.
                    var sorted = solid.OrderBy(t => t).ToArray();
                    usedBeadArea = Bead.Area(sorted[sorted.Length / 2], 0.5 * (hMin + hMax));
                }
                for (int v = 0; v < h.Length; v++)
                    h[v] = tv[v] > 0 ? Clamp(HeightForArea(usedBeadArea, tv[v]), hMin, hMax) : hMax;
            }
            else if (mapping == LayerMapping.ConstantAspectRatio)
            {
                if (aspectRatio <= 0) throw new ArgumentException("Aspect ratio must be positive.");
                for (int v = 0; v < h.Length; v++)
                    h[v] = tv[v] > 0 ? Clamp(aspectRatio * tv[v], hMin, hMax) : hMax;
            }
            else
            {
                double span = tMax - tMin;
                for (int v = 0; v < h.Length; v++)
                {
                    double s = span > 1e-12 ? (tv[v] - tMin) / span : 0.5;
                    h[v] = tv[v] > 0 ? hMax - Clamp(s, 0, 1) * (hMax - hMin) : hMax;
                }
            }

            if (smoothing > 0)
            {
                var nb = mesh.VertexNeighbours();
                var tmp = new double[h.Length];
                for (int it = 0; it < smoothing; it++)
                {
                    for (int v = 0; v < h.Length; v++)
                    {
                        double s = h[v];
                        foreach (int w in nb[v]) s += h[w];
                        tmp[v] = s / (nb[v].Count + 1);
                    }
                    Array.Copy(tmp, h, h.Length);
                }
            }
            return h;
        }

        /// <summary>Height at which a bead of width w has cross-section A (inverse of Bead.Area in h).</summary>
        private static double HeightForArea(double area, double width)
        {
            // (w - h) h + pi h^2/4 = A  ->  c h^2 - w h + A = 0, c = 1 - pi/4; take the smaller root.
            double c = 1 - Math.PI / 4.0;
            double disc = width * width - 4 * c * area;
            return disc < 0 ? width : (width - Math.Sqrt(disc)) / (2 * c);
        }

        private static double Clamp(double x, double a, double b) => x < a ? a : (x > b ? b : x);
    }
}
