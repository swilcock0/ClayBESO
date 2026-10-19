using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Geometry;

namespace ClayBeso.Printing
{
    public enum LayerMethod
    {
        /// <summary>
        /// Layers fit both the base and the top rim: every layer, including the last, is a
        /// closed loop. Target heights are matched in a least-squares sense.
        /// </summary>
        Conforming = 0,

        /// <summary>
        /// Weighted geodesic distance from the base (fast marching). Local layer heights are
        /// exact, but the top can end in partial layers.
        /// </summary>
        Eikonal = 1,
    }

    /// <summary>
    /// A scalar "layer index" field phi on the shell mesh: layer k is the iso-curve phi = k.
    /// The spacing between consecutive iso-curves follows the requested layer height.
    /// </summary>
    public sealed class LayerField
    {
        public TriMesh Mesh { get; }
        public double[] Phi { get; }
        public int[] BaseVertices { get; }
        public int[] TopVertices { get; }

        /// <summary>Per-vertex target layer height [mm].</summary>
        public double[] TargetHeight { get; }

        /// <summary>Per-vertex factor turning surface spacing into vertical spacing (1 when measuring along the surface).</summary>
        public double[] SpacingFactor { get; }

        /// <summary>Number of layers (the top rim sits at phi = LayerCount in conforming mode).</summary>
        public int LayerCount { get; }

        private LayerField(TriMesh mesh, double[] phi, int[] baseV, int[] topV, double[] h, double[] factor, int count)
        {
            Mesh = mesh; Phi = phi; BaseVertices = baseV; TopVertices = topV;
            TargetHeight = h; SpacingFactor = factor; LayerCount = count;
        }

        /// <summary>Vertices within <paramref name="tolerance"/> of the lowest / highest Z.</summary>
        public static (int[] bottom, int[] top) ExtremeVertices(TriMesh mesh, Vec3 up, double tolerance)
        {
            var hgt = mesh.Vertices.Select(p => Vec3.Dot(p, up)).ToArray();
            double lo = hgt.Min(), hi = hgt.Max();
            var bottom = Enumerable.Range(0, hgt.Length).Where(i => hgt[i] <= lo + tolerance).ToArray();
            var top = Enumerable.Range(0, hgt.Length).Where(i => hgt[i] >= hi - tolerance).ToArray();
            return (bottom, top);
        }

        /// <param name="layerHeight">Target layer height per vertex [mm].</param>
        /// <param name="baseVertices">Where printing starts (phi = 0).</param>
        /// <param name="topVertices">Top rim (conforming mode only; may be empty).</param>
        /// <param name="up">Build direction.</param>
        /// <param name="vertical">Measure layer height along the build direction instead of along the surface.</param>
        public static LayerField Compute(
            TriMesh mesh,
            IList<double> layerHeight,
            IList<int> baseVertices,
            IList<int> topVertices,
            Vec3 up,
            bool vertical,
            LayerMethod method)
        {
            if (layerHeight.Count != mesh.VertexCount) throw new ArgumentException("Need one layer height per vertex.");
            if (baseVertices == null || baseVertices.Count == 0) throw new ArgumentException("No base vertices.");
            up = up.Unit();

            // Slowness: layers per unit surface length. In vertical mode a surface step ds rises by
            // ds * |grad_s z|, so a sloping wall packs layers more sparsely along the surface.
            var factor = new double[mesh.VertexCount];
            var vn = mesh.VertexNormals;
            for (int v = 0; v < factor.Length; v++)
            {
                if (!vertical) { factor[v] = 1; continue; }
                double nz = Vec3.Dot(vn[v], up);
                factor[v] = Math.Max(0.05, Math.Sqrt(Math.Max(0, 1 - nz * nz)));
            }
            var slowness = new double[mesh.VertexCount];
            for (int v = 0; v < slowness.Length; v++) slowness[v] = factor[v] / layerHeight[v];

            var baseSet = new HashSet<int>(baseVertices);
            var top = (topVertices ?? Array.Empty<int>()).Where(v => !baseSet.Contains(v)).ToArray();

            double[] phi;
            int count;
            if (method == LayerMethod.Eikonal || top.Length == 0)
            {
                phi = FastMarching.Solve(mesh, slowness, baseSet.ToArray());
                count = (int)Math.Floor(phi.Where(p => !double.IsInfinity(p)).Max() + 1e-9);
            }
            else
            {
                phi = Conforming(mesh, slowness, baseSet, top, out count);
            }
            return new LayerField(mesh, phi, baseSet.ToArray(), top, layerHeight.ToArray(), factor, count);
        }

        private static double[] Conforming(TriMesh mesh, double[] slowness, HashSet<int> baseSet, int[] top, out int count)
        {
            var ops = new MeshOperators(mesh);

            // 1) Harmonic field base -> top gives a smooth, conflict-free stacking direction.
            var bc = new Dictionary<int, double>();
            foreach (int v in baseSet) bc[v] = 0;
            foreach (int v in top) bc[v] = 1;
            var psi = ops.SolveLeastSquaresGradient(null, bc);

            // 2) Target gradient: unit stacking direction scaled by the local layer density.
            var target = new Vec3[mesh.FaceCount];
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                var t = mesh.Faces[f];
                var d = ops.FaceGradient(f, psi).Unit();
                double s = (slowness[t[0]] + slowness[t[1]] + slowness[t[2]]) / 3.0;
                target[f] = d * s;
            }

            // 3) Fit with only the base pinned, to find how many layers the targets imply...
            var baseOnly = new Dictionary<int, double>();
            foreach (int v in baseSet) baseOnly[v] = 0;
            var free = ops.SolveLeastSquaresGradient(target, baseOnly);
            double topMean = top.Average(v => free[v]);
            count = Math.Max(1, (int)Math.Round(topMean));

            // 4) ...then pin the rim to that whole number so the last layer closes.
            foreach (int v in top) baseOnly[v] = count;
            return ops.SolveLeastSquaresGradient(target, baseOnly);
        }

        /// <summary>
        /// Layer height actually produced on each face: 1/|grad phi|, converted to the same
        /// measure (vertical or along-surface) as the target.
        /// </summary>
        public double[] RealisedFaceHeight()
        {
            var ops = new MeshOperators(Mesh);
            var h = new double[Mesh.FaceCount];
            for (int f = 0; f < h.Length; f++)
            {
                var t = Mesh.Faces[f];
                double g = ops.FaceGradient(f, Phi).Length;
                double fac = (SpacingFactor[t[0]] + SpacingFactor[t[1]] + SpacingFactor[t[2]]) / 3.0;
                h[f] = g > 1e-12 ? fac / g : double.PositiveInfinity;
            }
            return h;
        }
    }
}
