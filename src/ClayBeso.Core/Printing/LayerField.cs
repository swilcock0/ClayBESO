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

        /// <summary>
        /// Like <see cref="Conforming"/>, but the stacking direction on each face is turned towards the
        /// force flow, so layers lie across the force and interfaces carry it as compression rather than
        /// shear (Vele, Kurilla &amp; Achten 2023). The turn is limited to a maximum deviation and
        /// scaled by how directional the force is. Turning can pull layers apart; where it would make
        /// them taller than the maximum layer height, the turn is reduced.
        /// </summary>
        ForceAligned = 2,
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

        /// <summary>
        /// Force-aligned method: faces where the turn towards the force was reduced so the layers stay
        /// within the maximum layer height (all faces when the whole turn had to be scaled back; 0 for the
        /// other methods or when nothing was reduced).
        /// </summary>
        public int ReducedTurnFaces { get; private set; }

        /// <summary>
        /// Force-aligned method: share of the full turn that was kept overall (1 = all of it; less when the
        /// whole turn had to be scaled back to keep the layers within the maximum height; 0 = conforming).
        /// </summary>
        public double TurnFraction { get; private set; } = 1;

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
        /// <param name="flow">Force-aligned method: force flow per face. Direction = the force direction,
        /// length 0..1 = how directional it is (0 = no preference, keep the conforming direction).</param>
        /// <param name="maxDeviationDeg">Force-aligned method: most the stacking direction may turn from the
        /// conforming one [deg].</param>
        /// <param name="smoothing">Force-aligned method: smoothing passes on the direction field.</param>
        /// <param name="maxHeight">Force-aligned method: tallest layer the turn may create [mm], in the same
        /// measure as the targets (the Layer Rule's maximum). Where even conforming layers are taller, the
        /// turn may not make them taller still. 0 = no limit.</param>
        public static LayerField Compute(
            TriMesh mesh,
            IList<double> layerHeight,
            IList<int> baseVertices,
            IList<int> topVertices,
            Vec3 up,
            bool vertical,
            LayerMethod method,
            IList<Vec3> flow = null,
            double maxDeviationDeg = 30,
            int smoothing = 5,
            double maxHeight = 0)
        {
            if (method == LayerMethod.ForceAligned && (flow == null || flow.Count != mesh.FaceCount))
                throw new ArgumentException("Force-aligned layers need a force flow vector per mesh face.");
            if (layerHeight.Count != mesh.VertexCount) throw new ArgumentException("Need one layer height per vertex.");
            if (baseVertices == null || baseVertices.Count == 0) throw new ArgumentException("No base vertices.");
            up = up.Unit();

            var factor = SpacingFactors(mesh, up, vertical);
            var slowness = new double[mesh.VertexCount];
            for (int v = 0; v < slowness.Length; v++) slowness[v] = factor[v] / layerHeight[v];

            var baseSet = new HashSet<int>(baseVertices);
            var top = (topVertices ?? Array.Empty<int>()).Where(v => !baseSet.Contains(v)).ToArray();

            double[] phi;
            int count;
            int reduced = 0;
            double fraction = 1;
            if (method == LayerMethod.Eikonal || top.Length == 0)
            {
                phi = FastMarching.Solve(mesh, slowness, baseSet.ToArray());
                count = (int)Math.Floor(phi.Where(p => !double.IsInfinity(p)).Max() + 1e-9);
            }
            else
            {
                phi = Conforming(mesh, slowness, factor, baseSet, top, out count,
                    method == LayerMethod.ForceAligned ? flow : null, maxDeviationDeg, smoothing,
                    maxHeight > 0 ? maxHeight : double.PositiveInfinity, out reduced, out fraction);
            }
            return new LayerField(mesh, phi, baseSet.ToArray(), top, layerHeight.ToArray(), factor, count)
            {
                ReducedTurnFaces = reduced,
                TurnFraction = method == LayerMethod.ForceAligned ? fraction : 0,
            };
        }

        /// <summary>Rounds of per-face turn reduction for the force-aligned height limit.</summary>
        private const int LimitRounds = 6;

        /// <summary>Bisection steps on the overall turn when the per-face rounds don't settle.</summary>
        private const int BisectionSteps = 6;

        /// <summary>Slack on the height limit, so round-off doesn't count as a violation.</summary>
        private const double LimitSlack = 1.02;

        private static double[] Conforming(TriMesh mesh, double[] slowness, double[] factor, HashSet<int> baseSet, int[] top,
            out int count, IList<Vec3> flow, double maxDeviationDeg, int smoothing, double hMax, out int reduced, out double fraction)
        {
            var ops = new MeshOperators(mesh);
            reduced = 0;
            fraction = 1;

            // 1) Harmonic field base -> top gives a smooth, conflict-free stacking direction.
            var bc = new Dictionary<int, double>();
            foreach (int v in baseSet) bc[v] = 0;
            foreach (int v in top) bc[v] = 1;
            var psi = ops.SolveLeastSquaresGradient(null, bc);
            var dir0 = new Vec3[mesh.FaceCount];
            for (int f = 0; f < mesh.FaceCount; f++) dir0[f] = ops.FaceGradient(f, psi).Unit();

            if (flow == null) return FitLayers(mesh, ops, dir0, slowness, baseSet, top, out count);

            // Force-aligned. Turning the direction field makes the target gradient curl, and the
            // least-squares fit then spreads the layers apart in places. Closed layers can't all turn
            // without some spreading, so the turn gives way where it would make layers taller than hMax
            // (or than the conforming layers there, where those are already taller):
            //  1) per face: halve the turn on faces that come out too tall, and a little around them;
            //  2) if that doesn't settle, turn everything less by one common fraction (bisection). A
            //     fraction of 0 is the conforming field, so a valid answer always exists.
            var phiConf = FitLayers(mesh, ops, dir0, slowness, baseSet, top, out int countConf);
            var turned = TurnTowardsFlow(mesh, dir0, flow, maxDeviationDeg, smoothing);
            if (double.IsPositiveInfinity(hMax))
                return FitLayers(mesh, ops, turned, slowness, baseSet, top, out count);

            var conforming = RealisedFaceHeight(mesh, phiConf, factor);
            var limit = conforming.Select(h => LimitSlack * Math.Max(hMax, double.IsInfinity(h) || double.IsNaN(h) ? 0 : h)).ToArray();
            var neighbours = FaceNeighbours(mesh);
            var scale = Enumerable.Repeat(1.0, mesh.FaceCount).ToArray();

            double[] Fit(double fraction, out int n, out int[] tooTall)
            {
                var dir = new Vec3[mesh.FaceCount];
                for (int f = 0; f < dir.Length; f++)
                {
                    double w = fraction * scale[f];
                    var d = dir0[f] * (1 - w) + turned[f] * w;
                    dir[f] = d.Length > 1e-12 ? d.Unit() : dir0[f];
                }
                var result = FitLayers(mesh, ops, dir, slowness, baseSet, top, out n);
                var h = RealisedFaceHeight(mesh, result, factor);
                tooTall = Enumerable.Range(0, mesh.FaceCount)
                    .Where(f => !double.IsInfinity(h[f]) && !double.IsNaN(h[f]) && h[f] > limit[f]).ToArray();
                return result;
            }

            for (int round = 0; round < LimitRounds; round++)
            {
                var phi = Fit(1, out count, out var tooTall);
                if (tooTall.Length == 0)
                {
                    reduced = scale.Count(x => x < 1);
                    return phi;
                }
                foreach (int f in tooTall)
                {
                    scale[f] *= 0.5;
                    foreach (int g in neighbours[f]) scale[g] *= 0.75;
                }
            }

            double lo = 0, hi = 1;
            var best = phiConf;
            count = countConf;
            for (int step = 0; step < BisectionSteps; step++)
            {
                double mid = 0.5 * (lo + hi);
                var phi = Fit(mid, out int n, out var tooTall);
                if (tooTall.Length == 0) { lo = mid; best = phi; count = n; }
                else hi = mid;
            }
            reduced = mesh.FaceCount;
            fraction = lo;
            if (lo == 0) count = countConf;
            return best;
        }

        /// <summary>
        /// Fit phi to a stacking direction field: target gradient = direction / layer height. Pinned to 0 at
        /// the base, then to the implied whole number of layers at the rim so the last layer closes.
        /// </summary>
        private static double[] FitLayers(TriMesh mesh, MeshOperators ops, Vec3[] dir, double[] slowness,
            HashSet<int> baseSet, int[] top, out int count)
        {
            var target = new Vec3[mesh.FaceCount];
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                var t = mesh.Faces[f];
                double s = (slowness[t[0]] + slowness[t[1]] + slowness[t[2]]) / 3.0;
                target[f] = dir[f] * s;
            }

            // Fit with only the base pinned, to find how many layers the targets imply...
            var baseOnly = new Dictionary<int, double>();
            foreach (int v in baseSet) baseOnly[v] = 0;
            var free = ops.SolveLeastSquaresGradient(target, baseOnly);
            double topMean = top.Average(v => free[v]);
            count = Math.Max(1, (int)Math.Round(topMean));

            // ...then pin the rim to that whole number so the last layer closes.
            foreach (int v in top) baseOnly[v] = count;
            return ops.SolveLeastSquaresGradient(target, baseOnly);
        }

        /// <summary>
        /// Turn each face's stacking direction towards the force flow (sign chosen to point upwards
        /// with the conforming direction) by at most maxDeviation, scaled by the flow's
        /// directionality; then smooth the field across neighbouring faces.
        /// </summary>
        private static Vec3[] TurnTowardsFlow(TriMesh mesh, Vec3[] dir0, IList<Vec3> flow, double maxDeviationDeg, int smoothing)
        {
            var normals = mesh.FaceNormals;
            double maxDev = Math.Max(0, maxDeviationDeg) * Math.PI / 180;
            var dir = new Vec3[dir0.Length];
            for (int f = 0; f < dir.Length; f++)
            {
                var d0 = dir0[f];
                var n = normals[f];
                double weight = Math.Min(1, flow[f].Length);
                var fl = flow[f] - n * Vec3.Dot(flow[f], n);
                if (weight < 1e-9 || fl.Length < 1e-12 || d0.Length < 1e-12) { dir[f] = d0; continue; }
                fl = fl.Unit();
                if (Vec3.Dot(fl, d0) < 0) fl = -fl;
                var side = fl - d0 * Vec3.Dot(fl, d0);
                double angle = Math.Acos(Math.Min(1, Vec3.Dot(fl, d0)));
                if (side.Length < 1e-12) { dir[f] = d0; continue; }
                double turn = Math.Min(angle, maxDev) * weight;
                dir[f] = (d0 * Math.Cos(turn) + side.Unit() * Math.Sin(turn)).Unit();
            }

            if (smoothing > 0)
            {
                var neighbours = FaceNeighbours(mesh);
                var tmp = new Vec3[dir.Length];
                for (int it = 0; it < smoothing; it++)
                {
                    for (int f = 0; f < dir.Length; f++)
                    {
                        var sum = dir[f];
                        foreach (int g in neighbours[f]) sum += Vec3.Dot(dir[g], dir[f]) >= 0 ? dir[g] : -dir[g];
                        var n = normals[f];
                        sum -= n * Vec3.Dot(sum, n);
                        tmp[f] = sum.Length > 1e-12 ? sum.Unit() : dir[f];
                    }
                    Array.Copy(tmp, dir, dir.Length);
                }
            }
            return dir;
        }

        private static List<int>[] FaceNeighbours(TriMesh mesh)
        {
            var byEdge = new Dictionary<(int, int), List<int>>();
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                var t = mesh.Faces[f];
                for (int k = 0; k < 3; k++)
                {
                    int a = t[k], b = t[(k + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = new List<int>(2);
                    list.Add(f);
                }
            }
            var result = new List<int>[mesh.FaceCount];
            for (int f = 0; f < result.Length; f++) result[f] = new List<int>(3);
            foreach (var list in byEdge.Values)
                if (list.Count == 2) { result[list[0]].Add(list[1]); result[list[1]].Add(list[0]); }
            return result;
        }

        /// <summary>
        /// Layer height actually produced on each face: 1/|grad phi|, converted to the same
        /// measure (vertical or along-surface) as the target.
        /// </summary>
        public double[] RealisedFaceHeight() => RealisedFaceHeight(Mesh, Phi, SpacingFactor);

        /// <summary>Realised layer height per face from any layer field phi (e.g. read back from Grasshopper).</summary>
        public static double[] RealisedFaceHeight(TriMesh mesh, IList<double> phi, Vec3 up, bool vertical) =>
            RealisedFaceHeight(mesh, phi, SpacingFactors(mesh, up.Unit(), vertical));

        private static double[] RealisedFaceHeight(TriMesh mesh, IList<double> phi, double[] spacingFactor)
        {
            var ops = new MeshOperators(mesh);
            var h = new double[mesh.FaceCount];
            for (int f = 0; f < h.Length; f++)
            {
                var t = mesh.Faces[f];
                double g = ops.FaceGradient(f, phi).Length;
                double fac = (spacingFactor[t[0]] + spacingFactor[t[1]] + spacingFactor[t[2]]) / 3.0;
                h[f] = g > 1e-12 ? fac / g : double.PositiveInfinity;
            }
            return h;
        }

        /// <summary>
        /// Per-vertex factor from spacing along the wall to vertical spacing. In vertical mode a
        /// surface step ds rises by ds * |grad_s z|, so a sloping wall packs layers more sparsely
        /// along the surface.
        /// </summary>
        internal static double[] SpacingFactors(TriMesh mesh, Vec3 up, bool vertical)
        {
            var factor = new double[mesh.VertexCount];
            var vn = mesh.VertexNormals;
            for (int v = 0; v < factor.Length; v++)
            {
                if (!vertical) { factor[v] = 1; continue; }
                double nz = Vec3.Dot(vn[v], up);
                factor[v] = Math.Max(0.05, Math.Sqrt(Math.Max(0, 1 - nz * nz)));
            }
            return factor;
        }
    }
}
