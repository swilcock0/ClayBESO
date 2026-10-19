using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Geometry;

namespace ClayBeso.Printing
{
    public enum LayerMapping
    {
        /// <summary>
        /// For printers without flow control: constant bead cross-section (constant flow and feed).
        /// h is chosen so that a bead of width t has area A (see <see cref="Bead.Area"/>), roughly
        /// h = A / t, so thick walls come from squashing the bead with a lower layer.
        /// </summary>
        ConstantBeadArea = 0,

        /// <summary>Linear remap: thickest wall -> hMin, thinnest wall -> hMax (fixed-flow style).</summary>
        LinearInverse = 1,

        /// <summary>
        /// h = k * t: every bead keeps the same height/width ratio, which keeps stacking stability
        /// uniform. Thickness and layer height rise together. Needs flow control (bead area ~ t^2).
        /// </summary>
        ConstantAspectRatio = 2,

        /// <summary>
        /// Thinnest wall -> hMin, thickest wall -> hMax: more demand means a wider bead and a taller
        /// layer, with the flow raised to supply both (see the flow factor). Needs flow control.
        /// </summary>
        Proportional = 3,
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
        /// <param name="thicknessRange">Thickness interval mapped onto [hMin, hMax] by the linear mappings
        /// (e.g. the optimiser's tMin..tMax). null = the min..max of the field itself.</param>
        public static double[] Compute(
            TriMesh mesh,
            IList<double> faceThickness,
            double hMin,
            double hMax,
            LayerMapping mapping,
            double beadArea,
            int smoothing,
            out double usedBeadArea,
            double aspectRatio = 0.4,
            (double lo, double hi)? thicknessRange = null)
        {
            var rule = new LayerRule(mapping, hMin, hMax, beadArea, aspectRatio, thicknessRange);
            return Compute(mesh, faceThickness, rule, smoothing, out usedBeadArea);
        }

        /// <summary>Layer height per vertex from a shared <see cref="LayerRule"/>; automatic values come from the field.</summary>
        public static double[] Compute(TriMesh mesh, IList<double> faceThickness, LayerRule rule, int smoothing, out double usedBeadArea)
        {
            var tv = mesh.FaceToVertex(faceThickness);
            var resolved = rule.Resolve(tv);
            usedBeadArea = resolved.BeadArea;

            var h = new double[mesh.VertexCount];
            for (int v = 0; v < h.Length; v++) h[v] = resolved.Height(tv[v]);

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

    }
}
