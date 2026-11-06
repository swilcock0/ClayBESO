using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Optimisation;

namespace ClayBeso.Printing
{
    /// <summary>The wall the structural checks of <see cref="LayerReview"/> assume.</summary>
    public enum WallMode
    {
        /// <summary>One bead everywhere, <see cref="PrinterProfile.BeadWidth"/> wide.</summary>
        Bead = 0,

        /// <summary>
        /// The thickness BESO gives this shape with the same clay as one bead, within the printer's wall
        /// range: where the wall wants to be wider than a bead, a woven (back and forth) path is needed.
        /// </summary>
        Ideal = 1,
    }

    public enum IssueKind
    {
        TallLayer, LowLayer, PoorContact, Overhang, Unsupported, Tilt, LayerBond, PrintStage, Buckling,
        /// <summary>The given height is less than the space down to the layer below: the bead doesn't reach it.</summary>
        LayerGap,
        /// <summary>The given height is more than the space down to the layer below: the bead is pushed into it.</summary>
        Squashed,
    }

    /// <summary>One problem area: neighbouring faces of the wall that fail the same check.</summary>
    public sealed class LayerIssue
    {
        public IssueKind Kind { get; internal set; }

        /// <summary>Short name, e.g. "Layer too tall".</summary>
        public string Title { get; internal set; }

        /// <summary>What was measured against which limit, at the worst point of the area.</summary>
        public string Reason { get; internal set; }

        /// <summary>What to change.</summary>
        public string Advice { get; internal set; }

        /// <summary>Layers the area spans (0 = first layer, the same numbers as the input branches).</summary>
        public int FirstLayer { get; internal set; }
        public int LastLayer { get; internal set; }

        /// <summary>Worst point of the area [mm].</summary>
        public Vec3 Location { get; internal set; }

        /// <summary>Worst value / limit (above 1 = fails; 2 = twice the limit).</summary>
        public double Severity { get; internal set; }

        /// <summary>Faces of <see cref="LayerReviewResult.Mesh"/> in the area.</summary>
        public int[] Faces { get; internal set; }

        public string Layers => FirstLayer == LastLayer ? $"layer {FirstLayer}" : $"layers {FirstLayer}-{LastLayer}";

        public override string ToString() => $"{Title}, {Layers}: {Reason} {Advice}";
    }

    public sealed class LayerReviewOptions
    {
        public WallMode Wall { get; set; } = WallMode.Bead;

        /// <summary>Print stages checked for strength and buckling while printing.</summary>
        public int Stages { get; set; } = 6;

        /// <summary>Include linear buckling (finished piece and stages).</summary>
        public bool Buckling { get; set; } = true;

        /// <summary>
        /// Above this many faces the structure is analysed on a coarser copy of the wall (every few layers,
        /// evenly spaced points) and the results are mapped back. The per-point checks always use every point.
        /// </summary>
        public int MaxAnalysisFaces { get; set; } = 6000;

        /// <summary>BESO iterations for <see cref="WallMode.Ideal"/>.</summary>
        public int MaxIterations { get; set; } = 50;

        /// <summary>
        /// Report beads taller than the space below them (pushed into the bead below) as problem areas. Off by
        /// default: some squash is normal in clay printing and only adds clay; they are still counted
        /// (<see cref="LayerReviewResult.SquashedPoints"/>) and shown in <see cref="LayerReviewResult.HeightVsSpacing"/>.
        /// </summary>
        public bool ReportSquashed { get; set; }
    }

    public sealed class LayerReviewResult
    {
        public PrinterProfile Printer { get; internal set; }
        public WallMode Wall { get; internal set; }

        /// <summary>The input was one spiral path, split into turns.</summary>
        public bool Spiral { get; internal set; }

        public int LayerCount { get; internal set; }

        /// <summary>The wall through the input points: layer k to layer k+1 is one strip of faces [mm].</summary>
        public TriMesh Mesh { get; internal set; }

        /// <summary>Per face of <see cref="Mesh"/>: the lower layer of its strip.</summary>
        public int[] FaceLayer { get; internal set; }

        /// <summary>Layer number at each vertex of <see cref="Mesh"/> (fractional along a spiral turn).</summary>
        public double[] VertexLayer { get; internal set; }

        /// <summary>The input layers as paths, with height, width, normal and bead area per point.</summary>
        public List<LayerPath> Paths { get; internal set; }

        /// <summary>Mesh the structure was analysed on: <see cref="Mesh"/> or a coarser copy.</summary>
        public TriMesh AnalysisMesh { get; internal set; }

        public ShellModel Model { get; internal set; }
        public ShellDesign Design { get; internal set; }
        public PrintPlan Plan { get; internal set; }
        public PrintCheckResult Check { get; internal set; }
        public PrintScore Score { get; internal set; }

        /// <summary>Ideal wall: BESO iterations, convergence, compliance vs one bead (below 1 = stiffer).</summary>
        public int Iterations { get; internal set; }
        public bool Converged { get; internal set; }
        public double ComplianceRatio { get; internal set; } = 1;

        // Per face of Mesh (NaN where not defined).
        /// <summary>Wall thickness [mm].</summary>
        public double[] Thickness { get; internal set; }
        /// <summary>Layer height of the beads on the face [mm].</summary>
        public double[] LayerHeight { get; internal set; }
        /// <summary>Layer height / bead width.</summary>
        public double[] ContactRatio { get; internal set; }
        /// <summary>Wall lean from vertical [deg].</summary>
        public double[] Lean { get; internal set; }
        /// <summary>Share of the bead width past the bead below.</summary>
        public double[] BeadOffset { get; internal set; }
        /// <summary>Layer slope along the path [deg].</summary>
        public double[] Tilt { get; internal set; }
        /// <summary>Given height minus the space down to the layer below [mm]: below 0 = a gap under the bead,
        /// above 0 = squashed into the bead below (NaN without given heights, and on the first layer).</summary>
        public double[] HeightVsSpacing { get; internal set; }
        /// <summary>Interlayer utilisation of the finished piece (NaN without a strength).</summary>
        public double[] Interlayer { get; internal set; }
        /// <summary>Highest interlayer utilisation over the print stages (NaN without a strength).</summary>
        public double[] StageUtilisation { get; internal set; }
        /// <summary>Buckling shape of the most critical case (finished piece or a stage), largest = 1.</summary>
        public double[] BucklingShape { get; internal set; }
        /// <summary>Worst check: value / limit (above 1 = fails).</summary>
        public double[] Severity { get; internal set; }

        /// <summary>Lowest buckling factor, and where: -1 = finished piece, else the stage's top layer.</summary>
        public double BucklingFactor { get; internal set; } = double.NaN;
        public int BucklingLayer { get; internal set; } = -1;

        /// <summary>Problem areas, worst first.</summary>
        public List<LayerIssue> Issues { get; internal set; } = new List<LayerIssue>();

        /// <summary>Things to know that aren't problems of the design (e.g. the heights don't match the geometry).</summary>
        public List<string> Warnings { get; internal set; } = new List<string>();

        /// <summary>Share of points whose given height differs from the spacing of the layers by more than 25 %.</summary>
        public double HeightMismatch { get; internal set; }

        /// <summary>Points whose given height is more than <see cref="LayerReview.SpacingTolerance"/> taller than the space below.</summary>
        public int SquashedPoints { get; internal set; }
    }

    /// <summary>
    /// Checks layers someone designed themselves (points per layer, or one spiral) instead of layers the
    /// plugin made. It builds the wall through the points, then runs the same checks as Clay Print: layer
    /// height limits, layer contact, overhang, bead support and tilt per point, and the structure (bond
    /// between layers, every print stage, buckling) with the base on the bed and self-weight. Failing faces
    /// are grouped into areas with a reason, so a design can be improved step by step. Nothing is re-sliced.
    /// </summary>
    public static class LayerReview
    {
        /// <summary>Buckling factor below which a buckling area is reported (same as Print Check's warning).</summary>
        public const double BucklingMargin = 3.0;

        /// <summary>A given height further than this share of the spacing from it is a gap or a squashed layer.</summary>
        public const double SpacingTolerance = 0.15;

        private sealed class Layer
        {
            public List<int> V = new List<int>();      // vertex ids, in print order (closed loops without the repeated start)
            public List<double> U = new List<double>(); // 0..1 along the layer (arc length; angle for spiral turns)
            public List<double> H = new List<double>(); // given height per point (NaN = not given)
            public bool Closed;
        }

        /// <param name="layers">Points of each layer in print order [mm], lowest layer first. One list that
        /// winds round more than once is treated as a spiral.</param>
        /// <param name="heights">Null, or per layer one height or one per point [mm]. Missing heights come from the
        /// spacing of the layers.</param>
        public static LayerReviewResult Run(IList<IList<Vec3>> layers, IList<IList<double>> heights,
            ClayMaterial material, PrinterProfile printer, LayerReviewOptions options = null)
        {
            var o = options ?? new LayerReviewOptions();
            if (layers == null || layers.Count == 0) throw new ArgumentException("No layers.");
            if (heights != null && heights.Count != layers.Count)
                throw new ArgumentException($"Heights has {heights.Count} lists but there are {layers.Count} layers: give one list of heights per layer.");
            var up = Vec3.UnitZ;
            var r = new LayerReviewResult { Printer = printer, Wall = o.Wall };

            // 1. Layers and the vertices they share.
            var pos = new List<Vec3>();
            List<Layer> L;
            bool spiral = layers.Count == 1 && Turns(layers[0]) > 1.2;
            if (spiral)
            {
                var h0 = heights?[0];
                L = SplitSpiral(layers[0], ExpandHeights(h0, layers[0].Count, 0), pos);
            }
            else
            {
                L = new List<Layer>();
                for (int k = 0; k < layers.Count; k++)
                    L.Add(MakeLayer(layers[k], ExpandHeights(heights?[k], layers[k].Count, k), pos, k));
                Align(L, pos);
            }
            if (L.Count < 2) throw new ArgumentException("Need at least two layers (or a spiral of more than one turn).");
            r.Spiral = spiral;
            r.LayerCount = L.Count;

            // 2. The wall through the points.
            var faces = new List<int[]>();
            var faceLayer = new List<int>();
            for (int k = 0; k + 1 < L.Count; k++)
            {
                bool closed = L[k].Closed && L[k + 1].Closed;
                Zip(L[k], L[k + 1], closed, pos, faces, faceLayer, k);
            }
            if (spiral && SpiralClockwise(layers[0]))
                foreach (var f in faces) { int swap = f[1]; f[1] = f[2]; f[2] = swap; }
            if (faces.Count == 0) throw new ArgumentException("Could not build a wall between the layers.");
            var mesh = new TriMesh(pos, faces);
            r.Mesh = mesh;
            r.FaceLayer = faceLayer.ToArray();

            var vLayer = new int[pos.Count];
            var vU = new double[pos.Count];
            for (int k = 0; k < L.Count; k++)
                for (int i = 0; i < L[k].V.Count; i++)
                {
                    // A spiral turn's end is the next turn's start: it belongs to the next turn.
                    if (spiral && i == L[k].V.Count - 1 && k + 1 < L.Count && L[k + 1].V[0] == L[k].V[i]) continue;
                    vLayer[L[k].V[i]] = k;
                    vU[L[k].V[i]] = L[k].U[i];
                }
            var vPhi = new double[pos.Count];
            for (int v = 0; v < vPhi.Length; v++) vPhi[v] = vLayer[v] + (spiral ? vU[v] : 0);
            r.VertexLayer = vPhi;

            // 3. Layer heights: given, else the vertical spacing to the layer below.
            var geo = GeometricHeights(L, pos);
            // The real space under each bead: unknown for the first layer (it sits on the bed).
            var spacing = new double[pos.Count];
            var isGiven = new bool[pos.Count];
            for (int v = 0; v < spacing.Length; v++) spacing[v] = vLayer[v] == 0 ? double.NaN : geo[v];
            int given = 0, mismatched = 0;
            var ratios = new List<double>();
            foreach (var layer in L)
                for (int i = 0; i < layer.V.Count; i++)
                {
                    int v = layer.V[i];
                    double g = geo[v];
                    if (double.IsNaN(layer.H[i])) { layer.H[i] = double.IsNaN(g) ? printer.MinLayer : g; continue; }
                    isGiven[v] = true;
                    double sp = spacing[v];
                    if (double.IsNaN(sp) || sp <= 0 || layer.H[i] <= 0) continue;
                    given++;
                    ratios.Add(sp / layer.H[i]);
                    if (Math.Abs(layer.H[i] - sp) > 0.25 * sp) mismatched++;
                }
            r.HeightMismatch = given > 0 ? (double)mismatched / given : 0;
            if (r.HeightMismatch > 0.2)
            {
                ratios.Sort();
                double ratio = ratios[ratios.Count / 2];
                r.Warnings.Add($"The given heights differ from the spacing of the layers by more than 25 % at {r.HeightMismatch:P0} of the points" +
                    (ratio > 1.5 || ratio < 0.67
                        ? $" (the spacing is typically {ratio:G3} × the given height: are the heights in mm? They always are, whatever the document units)"
                        : "") +
                    ". Each height should be the distance down to the layer below (the bead's own height). The per-point checks use the given heights; " +
                    "the structure uses the geometry.");
            }

            // 4. Mesh for the structure: the wall itself, or a coarser grid for big walls.
            var grid = AnalysisGrid(L, pos, mesh, vPhi, vU, o.MaxAnalysisFaces, spiral, r.Warnings);
            var am = grid.Mesh;
            r.AnalysisMesh = am;

            // 5. Model on the bed: the first layer clamped, self-weight.
            int n = am.VertexCount;
            var fix = new bool[6 * n];
            foreach (int v in grid.Base) for (int d = 0; d < 6; d++) fix[6 * v + d] = true;
            double bead = printer.BeadWidth;
            var model = new ShellModel(am, material, new[] { bead }, fix, null, new Vec3(0, 0, -1));
            r.Model = model;

            double[] t;
            if (o.Wall == WallMode.Ideal)
            {
                var opt = new BesoOptimizer(model, new BesoSettings
                {
                    Mode = BesoMode.Thickness,
                    MinThickness = printer.MinWall,
                    MaxThickness = printer.MaxWall,
                    FilterRadius = SensitivityFilter.AutoRadius(am),
                    MaxIterations = o.MaxIterations,
                });
                while (opt.Step()) { }
                t = opt.EffectiveThickness();
                r.Iterations = opt.Iteration;
                r.Converged = opt.Converged;
                var uniform = opt.ScalingFor(Enumerable.Repeat(bead, model.ElementCount).ToArray());
                r.ComplianceRatio = opt.Solver.Solve(opt.ScalingFor(opt.Design)).Compliance / opt.Solver.Solve(uniform).Compliance;
            }
            else t = Enumerable.Repeat(bead, model.ElementCount).ToArray();
            r.Design = new ShellDesign(model, t, null, new BondModel(),
                o.Wall == WallMode.Ideal ? "Check Layers (ideal wall)" : "Check Layers (one bead)");

            var thick = grid.ToDisplay(t);
            r.Thickness = thick;
            var vThick = mesh.FaceToVertex(thick);

            // 6. The layers as paths, with the per-point checks.
            var normals = mesh.VertexNormals;
            var paths = new List<LayerPath>();
            for (int k = 0; k < L.Count; k++)
            {
                var p = new LayerPath { Layer = k, Closed = L[k].Closed };
                for (int i = 0; i < L[k].V.Count; i++)
                {
                    int v = L[k].V[i];
                    double h = L[k].H[i], w = vThick[v];
                    p.Points.Add(pos[v]);
                    p.Normals.Add(normals[v]);
                    p.TargetHeight.Add(h);
                    p.RealisedHeight.Add(h);
                    p.Width.Add(w);
                    p.BeadArea.Add(Bead.Area(w, h));
                }
                paths.Add(p);
            }
            r.Paths = paths;
            var overhangs = paths.Select(p => OverhangCheck.Evaluate(p, up, true)).ToList();

            var settings = new PrintSettings { MaxOverhang = printer.MaxOverhang, MaxTilt = printer.MaxTilt, BaseTolerance = 0.5 };
            var areas = paths.SelectMany(p => p.BeadArea).Where(a => a > 0).OrderBy(a => a).ToArray();
            var field = LayerField.FromPhi(am, grid.Phi, grid.Base, grid.Top, up, true);
            r.Plan = new PrintPlan
            {
                Design = r.Design,
                Settings = settings,
                Rule = printer.Rule(),
                VertexHeight = field.TargetHeight,
                VertexThickness = am.FaceToVertex(t),
                Field = field,
                Paths = paths,
                Overhangs = overhangs,
                NominalArea = areas.Length > 0 ? areas[areas.Length / 2] : 0,
                HeightError = Enumerable.Repeat(double.NaN, am.FaceCount).ToArray(),   // no target to miss: the heights are the design
                Spiral = spiral,
            };

            // 7. Structure: finished piece, print stages, buckling.
            r.Check = PrintChecker.Check(r.Plan, new PrintCheckSettings
            {
                Strength = material.Strength,
                Stages = o.Stages,
                Buckling = o.Buckling,
            });

            // 8. Per-face values and issue areas.
            Evaluate(r, L, overhangs, grid, spacing, isGiven, o.ReportSquashed);

            var hs = paths.SelectMany(p => p.RealisedHeight).ToArray();
            double outside = hs.Length == 0 ? 0 : (double)hs.Count(h => h > printer.MaxLayer || h < printer.MinLayer) / hs.Length;
            var range = new ScoreItem
            {
                Name = "Layer height range",
                Score = PrintScore.Ramp(outside, 0, 0.05),
                Measured = $"{outside:P1} of the points are outside {printer.MinLayer:G3}-{printer.MaxLayer:G3} mm",
                Advice = "Keep every layer between a third and three quarters of the nozzle diameter (see Issues for where).",
            };
            r.Score = PrintScore.Evaluate(r.Plan, r.Check, new[] { range });
            return r;
        }

        // ---------------------------------------------------------------- layers

        /// <summary>How many times a path winds round the vertical axis through its centre.</summary>
        internal static double Turns(IList<Vec3> pts)
        {
            if (pts.Count < 3) return 0;
            var th = UnwrappedAngles(pts);
            return Math.Abs(th[th.Length - 1] - th[0]) / (2 * Math.PI);
        }

        private static double[] UnwrappedAngles(IList<Vec3> pts)
        {
            double cx = pts.Average(p => p.X), cy = pts.Average(p => p.Y);
            var th = new double[pts.Count];
            th[0] = Math.Atan2(pts[0].Y - cy, pts[0].X - cx);
            for (int i = 1; i < pts.Count; i++)
            {
                double a = Math.Atan2(pts[i].Y - cy, pts[i].X - cx);
                double d = a - Math.Atan2(pts[i - 1].Y - cy, pts[i - 1].X - cx);
                while (d > Math.PI) d -= 2 * Math.PI;
                while (d <= -Math.PI) d += 2 * Math.PI;
                th[i] = th[i - 1] + d;
            }
            return th;
        }

        private static bool SpiralClockwise(IList<Vec3> pts)
        {
            var th = UnwrappedAngles(pts);
            return th[th.Length - 1] < th[0];
        }

        private static double[] ExpandHeights(IList<double> h, int count, int layer)
        {
            var result = Enumerable.Repeat(double.NaN, count).ToArray();
            if (h == null || h.Count == 0) return result;
            if (h.Count == 1) { for (int i = 0; i < count; i++) result[i] = h[0]; return result; }
            if (h.Count != count)
                throw new ArgumentException($"Layer {layer} has {count} points but {h.Count} heights: give one height, or one per point.");
            for (int i = 0; i < count; i++) result[i] = h[i];
            return result;
        }

        private static Layer MakeLayer(IList<Vec3> pts, double[] h, List<Vec3> pos, int index)
        {
            var p = new List<Vec3>();
            var hh = new List<double>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (p.Count > 0 && (pts[i] - p[p.Count - 1]).Length < 1e-9) continue;
                p.Add(pts[i]);
                hh.Add(h[i]);
            }
            if (p.Count < 2) throw new ArgumentException($"Layer {index} has fewer than two distinct points.");

            // Closed: the first point repeated at the end, or an end gap no longer than a few segments.
            var seg = Enumerable.Range(0, p.Count - 1).Select(i => (p[i + 1] - p[i]).Length).OrderBy(x => x).ToArray();
            double median = seg[seg.Length / 2];
            double gap = (p[p.Count - 1] - p[0]).Length;
            bool closed = p.Count >= 3 && gap <= Math.Max(1e-6, 3 * median);
            if (closed && gap < 1e-6) { p.RemoveAt(p.Count - 1); hh.RemoveAt(hh.Count - 1); }
            if (closed && p.Count < 3) throw new ArgumentException($"Layer {index} is a closed loop with fewer than three points.");

            if (closed)
            {
                // Counter-clockwise seen from above, so the wall's normals point outwards.
                var c = Vec3.Zero;
                foreach (var q in p) c += q;
                c /= p.Count;
                double area = 0;
                for (int i = 0; i < p.Count; i++)
                {
                    var a = p[i] - c; var b = p[(i + 1) % p.Count] - c;
                    area += a.X * b.Y - a.Y * b.X;
                }
                if (area < 0)
                {
                    p.Reverse(); hh.Reverse();
                    // Keep the same start point.
                    p.Insert(0, p[p.Count - 1]); p.RemoveAt(p.Count - 1);
                    hh.Insert(0, hh[hh.Count - 1]); hh.RemoveAt(hh.Count - 1);
                }
            }

            var layer = new Layer { Closed = closed };
            foreach (var q in p) { layer.V.Add(pos.Count); pos.Add(q); }
            layer.H.AddRange(hh);
            return layer;
        }

        /// <summary>Starts each layer near the previous layer's start, then measures U along it.</summary>
        private static void Align(List<Layer> L, List<Vec3> pos)
        {
            for (int k = 0; k < L.Count; k++)
            {
                var layer = L[k];
                if (k > 0)
                {
                    var s = pos[L[k - 1].V[0]];
                    if (layer.Closed)
                    {
                        int best = 0;
                        double bd = double.MaxValue;
                        for (int i = 0; i < layer.V.Count; i++)
                        {
                            double d = (pos[layer.V[i]] - s).Length;
                            if (d < bd) { bd = d; best = i; }
                        }
                        Rotate(layer.V, best);
                        Rotate(layer.H, best);
                    }
                    else if ((pos[layer.V[layer.V.Count - 1]] - s).Length < (pos[layer.V[0]] - s).Length)
                    {
                        layer.V.Reverse();
                        layer.H.Reverse();
                    }
                }
                layer.U = ArcLength(layer, pos);
            }
        }

        private static List<double> ArcLength(Layer layer, List<Vec3> pos)
        {
            int m = layer.V.Count;
            var u = new List<double>(m) { 0 };
            double s = 0;
            for (int i = 1; i < m; i++) { s += (pos[layer.V[i]] - pos[layer.V[i - 1]]).Length; u.Add(s); }
            double total = layer.Closed ? s + (pos[layer.V[0]] - pos[layer.V[m - 1]]).Length : s;
            for (int i = 0; i < m; i++) u[i] = total > 0 ? u[i] / total : 0;
            return u;
        }

        private static void Rotate<T>(List<T> list, int start)
        {
            if (start == 0) return;
            var copy = list.Skip(start).Concat(list.Take(start)).ToList();
            list.Clear();
            list.AddRange(copy);
        }

        /// <summary>
        /// One spiral path → its turns. Turn k runs from angle k to k + 1 (whole turns from the first point);
        /// a point is inserted where each turn ends, and that point also starts the next turn.
        /// </summary>
        private static List<Layer> SplitSpiral(IList<Vec3> raw, double[] h, List<Vec3> pos)
        {
            var pts = new List<Vec3>();
            var hh = new List<double>();
            for (int i = 0; i < raw.Count; i++)
            {
                if (pts.Count > 0 && (raw[i] - pts[pts.Count - 1]).Length < 1e-9) continue;
                pts.Add(raw[i]); hh.Add(h[i]);
            }
            var th = UnwrappedAngles(pts);
            double sign = th[th.Length - 1] >= th[0] ? 1 : -1;
            var a = th.Select(x => sign * (x - th[0]) / (2 * Math.PI)).ToArray();

            var turns = new List<Layer>();
            var cur = new Layer();
            turns.Add(cur);
            void Add(Vec3 p, double height, double u)
            {
                cur.V.Add(pos.Count); pos.Add(p); cur.H.Add(height); cur.U.Add(u);
            }
            void StartNextTurn()
            {
                var next = new Layer();
                next.V.Add(cur.V[cur.V.Count - 1]); next.H.Add(cur.H[cur.H.Count - 1]); next.U.Add(0);
                turns.Add(next);
                cur = next;
            }
            // A turn end closer than this (in turns) to an existing point uses that point, so no sliver triangles.
            const double snap = 1e-6;
            Add(pts[0], hh[0], 0);
            for (int i = 1; i < pts.Count; i++)
            {
                double a0 = a[i - 1], a1 = a[i];
                bool added = false;
                if (a1 > a0)
                    for (int b = (int)Math.Floor(a0 - snap) + 1; b <= a1 + snap; b++)
                    {
                        if (b < a0 - snap || b <= turns.Count - 1) continue;
                        if (b - a0 <= snap) cur.U[cur.U.Count - 1] = 1;                  // the previous point ends the turn
                        else if (a1 - b <= snap) { Add(pts[i], hh[i], 1); added = true; }   // this point ends it
                        else
                        {
                            double s = (b - a0) / (a1 - a0);
                            Add(pts[i - 1] + (pts[i] - pts[i - 1]) * s, hh[i - 1] + (hh[i] - hh[i - 1]) * s, 1);
                        }
                        StartNextTurn();
                    }
                if (!added) Add(pts[i], hh[i], Math.Max(cur.U[cur.U.Count - 1], Math.Min(1, a1 - (turns.Count - 1))));
            }
            if (cur.V.Count < 2) turns.RemoveAt(turns.Count - 1);
            return turns;
        }

        /// <summary>Triangles between a lower and an upper layer, walking both by U.</summary>
        private static void Zip(Layer a, Layer b, bool closed, List<Vec3> pos, List<int[]> faces, List<int> faceLayer, int k)
        {
            int n = a.V.Count, m = b.V.Count;
            double UA(int i) => closed && i == n ? 1.0 : a.U[i];
            double UB(int j) => closed && j == m ? 1.0 : b.U[j];
            int VA(int i) => a.V[closed ? i % n : i];
            int VB(int j) => b.V[closed ? j % m : j];
            int iEnd = closed ? n : n - 1, jEnd = closed ? m : m - 1;
            double aMax = closed ? 1 : a.U[n - 1], bMax = closed ? 1 : b.U[m - 1];
            const double eps = 1e-9;

            void Add(int p, int q, int s)
            {
                if (p == q || q == s || p == s) return;
                if (Vec3.Cross(pos[q] - pos[p], pos[s] - pos[p]).Length < 1e-8) return;   // no area (points on a line)
                faces.Add(new[] { p, q, s });
                faceLayer.Add(k);
            }

            int i = 0, j = 0;
            while (i < iEnd || j < jEnd)
            {
                bool advanceA;
                if (i >= iEnd) { if (!closed && UB(j + 1) > aMax + eps) break; advanceA = false; }
                else if (j >= jEnd) { if (!closed && UA(i + 1) > bMax + eps) break; advanceA = true; }
                else advanceA = UA(i + 1) <= UB(j + 1);
                if (advanceA) { Add(VA(i), VA(i + 1), VB(j)); i++; }
                else { Add(VA(i), VB(j + 1), VB(j)); j++; }
            }
        }

        private static Vec3 PointAt(Layer layer, List<Vec3> pos, double u, out bool inside)
        {
            int m = layer.V.Count;
            inside = true;
            if (layer.Closed)
            {
                u -= Math.Floor(u);
                for (int i = 0; i < m; i++)
                {
                    double u0 = layer.U[i], u1 = i + 1 < m ? layer.U[i + 1] : 1.0;
                    if (u <= u1 || i == m - 1)
                    {
                        var p = pos[layer.V[i]]; var q = pos[layer.V[(i + 1) % m]];
                        double s = u1 > u0 ? (u - u0) / (u1 - u0) : 0;
                        return p + (q - p) * Math.Max(0, Math.Min(1, s));
                    }
                }
            }
            if (u < layer.U[0] - 1e-9 || u > layer.U[m - 1] + 1e-9) inside = false;
            for (int i = 0; i + 1 < m; i++)
                if (u <= layer.U[i + 1] || i == m - 2)
                {
                    double u0 = layer.U[i], u1 = layer.U[i + 1];
                    double s = u1 > u0 ? (u - u0) / (u1 - u0) : 0;
                    var p = pos[layer.V[i]]; var q = pos[layer.V[i + 1]];
                    return p + (q - p) * Math.Max(0, Math.Min(1, s));
                }
            return pos[layer.V[m - 1]];
        }

        /// <summary>Per vertex: height above the layer below at the same U (the first layer copies the second).</summary>
        private static double[] GeometricHeights(List<Layer> L, List<Vec3> pos)
        {
            var h = Enumerable.Repeat(double.NaN, pos.Count).ToArray();
            for (int k = 1; k < L.Count; k++)
                for (int i = 0; i < L[k].V.Count; i++)
                {
                    int v = L[k].V[i];
                    var below = PointAt(L[k - 1], pos, L[k].U[i], out bool inside);
                    if (inside) h[v] = pos[v].Z - below.Z;
                }
            var first = L[0];
            var second = L[1];
            for (int i = 0; i < first.V.Count; i++)
            {
                if (!double.IsNaN(h[first.V[i]])) continue;   // a spiral's shared end point
                int best = -1; double bd = double.MaxValue;
                for (int j = 0; j < second.V.Count; j++)
                {
                    double d = Math.Abs(second.U[j] - first.U[i]);
                    if (d < bd && !double.IsNaN(h[second.V[j]])) { bd = d; best = j; }
                }
                if (best >= 0) h[first.V[i]] = h[second.V[best]];
            }
            return h;
        }

        // ---------------------------------------------------------------- analysis mesh

        private sealed class Grid
        {
            public TriMesh Mesh;
            public double[] Phi;
            public int[] Base, Top;

            /// <summary>Per display face: the analysis faces it lies on (one or two).</summary>
            public int[][] Map;

            public double[] ToDisplay(IList<double> analysis)
            {
                var d = new double[Map.Length];
                for (int f = 0; f < d.Length; f++)
                {
                    double v = double.NaN;
                    foreach (int g in Map[f])
                        if (!double.IsNaN(analysis[g]) && (double.IsNaN(v) || analysis[g] > v)) v = analysis[g];
                    d[f] = v;
                }
                return d;
            }
        }

        private static Grid AnalysisGrid(List<Layer> L, List<Vec3> pos, TriMesh mesh, double[] vPhi, double[] vU,
            int maxFaces, bool spiral, List<string> warnings)
        {
            bool allClosed = L.All(l => l.Closed), allOpen = L.All(l => !l.Closed);
            if (mesh.FaceCount <= maxFaces || spiral || !(allClosed || allOpen))
            {
                if (mesh.FaceCount > maxFaces)
                    warnings.Add($"The wall has {mesh.FaceCount} faces and is analysed at full resolution " +
                        (spiral ? "(spirals are not coarsened yet)" : "(the layers are partly open, partly closed)") + ": this can be slow.");
                var phiTop = vPhi.Max();
                return new Grid
                {
                    Mesh = mesh,
                    Phi = vPhi,
                    Base = L[0].V.ToArray(),
                    Top = Enumerable.Range(0, pos.Count).Where(v => vPhi[v] >= phiTop - 1e-9).ToArray(),
                    Map = Enumerable.Range(0, mesh.FaceCount).Select(f => new[] { f }).ToArray(),
                };
            }

            // Every s-th layer, N evenly spaced points: square-ish cells, about maxFaces faces.
            double perimeter = L.Average(l => Length(l, pos));
            double height = Math.Max(1e-6, (MeanZ(L[L.Count - 1], pos) - MeanZ(L[0], pos)) / (L.Count - 1));
            bool closed = allClosed;
            int s = 1, N = 0, rows = 0;
            for (; s < L.Count; s++)
            {
                rows = (L.Count - 1 + s - 1) / s + 1;
                N = Math.Max(12, (int)Math.Round(perimeter / (s * height)));
                if (2 * (rows - 1) * N <= maxFaces) break;
            }
            if (s >= L.Count)
            {
                s = L.Count - 1;
                N = Math.Max(12, (int)Math.Round(perimeter / (s * height)));
            }
            var picked = new List<int>();
            for (int k = 0; k < L.Count - 1; k += s) picked.Add(k);
            if (picked[picked.Count - 1] != L.Count - 1) picked.Add(L.Count - 1);
            int R = picked.Count, cols = closed ? N : N + 1;

            var verts = new List<Vec3>();
            var phi = new List<double>();
            foreach (int k in picked)
                for (int j = 0; j < cols; j++)
                {
                    verts.Add(PointAt(L[k], pos, (double)j / N, out _));
                    phi.Add(k);
                }
            var faces = new List<int[]>();
            for (int rr = 0; rr + 1 < R; rr++)
                for (int j = 0; j < N; j++)
                {
                    int a0 = rr * cols + j, a1 = rr * cols + (closed ? (j + 1) % N : j + 1);
                    int b0 = a0 + cols, b1 = a1 + cols;
                    faces.Add(new[] { a0, a1, b0 });
                    faces.Add(new[] { a1, b1, b0 });
                }
            var am = new TriMesh(verts, faces);

            var map = new int[mesh.FaceCount][];
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                var t = mesh.Faces[f];
                double p = (vPhi[t[0]] + vPhi[t[1]] + vPhi[t[2]]) / 3.0;
                int rr = 0;
                while (rr + 2 < R && picked[rr + 1] <= p) rr++;
                double u0 = vU[t[0]], u1 = vU[t[1]], u2 = vU[t[2]];
                if (closed)
                {
                    // Faces across the start of the loop: average on the circle.
                    double lo = Math.Min(u0, Math.Min(u1, u2)), hi = Math.Max(u0, Math.Max(u1, u2));
                    if (hi - lo > 0.5) { if (u0 < 0.5) u0 += 1; if (u1 < 0.5) u1 += 1; if (u2 < 0.5) u2 += 1; }
                }
                double u = (u0 + u1 + u2) / 3.0;
                if (closed) u -= Math.Floor(u);
                int col = Math.Max(0, Math.Min(N - 1, (int)Math.Floor(u * N)));
                int q = 2 * (rr * N + col);
                map[f] = new[] { q, q + 1 };
            }
            warnings.Add($"The structure was analysed on a coarser copy of the wall ({am.FaceCount} faces: every {s} layer{(s > 1 ? "s" : "")}, " +
                $"{N} points per layer); the per-point checks use all {pos.Count} points.");
            return new Grid
            {
                Mesh = am,
                Phi = phi.ToArray(),
                Base = Enumerable.Range(0, cols).ToArray(),
                Top = Enumerable.Range((R - 1) * cols, cols).ToArray(),
                Map = map,
            };
        }

        private static double Length(Layer l, List<Vec3> pos)
        {
            double s = 0;
            for (int i = 1; i < l.V.Count; i++) s += (pos[l.V[i]] - pos[l.V[i - 1]]).Length;
            if (l.Closed) s += (pos[l.V[0]] - pos[l.V[l.V.Count - 1]]).Length;
            return s;
        }

        private static double MeanZ(Layer l, List<Vec3> pos) => l.V.Average(v => pos[v].Z);

        // ---------------------------------------------------------------- checks

        /// <param name="spacing">Per vertex: the space down to the layer below [mm] (NaN on the first layer).</param>
        /// <param name="given">Per vertex: the height was given (not measured from the layers).</param>
        private static void Evaluate(LayerReviewResult r, List<Layer> L, List<OverhangResult> overhangs, Grid grid,
            double[] spacing, bool[] given, bool reportSquashed)
        {
            var mesh = r.Mesh;
            var pr = r.Printer;
            int nv = mesh.VertexCount, nf = mesh.FaceCount;

            // Per vertex, from the paths (a spiral's shared point takes the later turn's values).
            double[] Nan(int c) => Enumerable.Repeat(double.NaN, c).ToArray();
            var vH = Nan(nv); var vRatio = Nan(nv); var vLean = Nan(nv); var vOff = Nan(nv); var vTilt = Nan(nv);
            for (int k = 0; k < L.Count; k++)
            {
                var path = r.Paths[k]; var oh = overhangs[k];
                for (int i = 0; i < L[k].V.Count; i++)
                {
                    int v = L[k].V[i];
                    double h = path.RealisedHeight[i], w = path.Width[i];
                    vH[v] = h;
                    vRatio[v] = w > 0 ? h / w : double.NaN;
                    vLean[v] = oh.Lean[i];
                    vOff[v] = oh.Ratio[i];
                    vTilt[v] = oh.Tilt[i];
                }
            }

            // A face shows the beads of its upper layer (the first strip also its lower layer).
            var layerOf = new int[nv];
            for (int k = 0; k < L.Count; k++) foreach (int v in L[k].V) layerOf[v] = k;
            for (int k = 1; k < L.Count; k++) layerOf[L[k].V[0]] = k;
            // Per face: the largest value of its beads (largest by size with signed: true), and which vertex it was.
            double[] ToFaces(double[] vals, int[] at = null, bool signed = false)
            {
                var f = new double[nf];
                for (int i = 0; i < nf; i++)
                {
                    double best = double.NaN;
                    int bestV = -1;
                    foreach (int v in mesh.Faces[i])
                    {
                        if (layerOf[v] <= r.FaceLayer[i] && r.FaceLayer[i] > 0) continue;
                        double x = vals[v];
                        if (double.IsNaN(x)) continue;
                        if (double.IsNaN(best) || (signed ? Math.Abs(x) > Math.Abs(best) : x > best)) { best = x; bestV = v; }
                    }
                    f[i] = best;
                    if (at != null) at[i] = bestV;
                }
                return f;
            }
            r.LayerHeight = ToFaces(vH);
            r.ContactRatio = ToFaces(vRatio);
            r.Lean = ToFaces(vLean);
            r.BeadOffset = ToFaces(vOff);
            r.Tilt = ToFaces(vTilt);
            var lowest = new double[nv];
            for (int v = 0; v < nv; v++) lowest[v] = vH[v] > 0 ? pr.MinLayer / vH[v] : double.NaN;
            var low = ToFaces(lowest);

            // Given height against the real space under the bead.
            var vDiff = Nan(nv); var vGapSev = Nan(nv); var vSquashSev = Nan(nv);
            for (int v = 0; v < nv; v++)
            {
                double sp = spacing[v];
                if (!given[v] || double.IsNaN(sp) || sp <= 0 || double.IsNaN(vH[v])) continue;
                vDiff[v] = vH[v] - sp;
                vGapSev[v] = Math.Max(0, sp - vH[v]) / (SpacingTolerance * sp);
                vSquashSev[v] = Math.Max(0, vH[v] - sp) / (SpacingTolerance * sp);
            }
            r.HeightVsSpacing = ToFaces(vDiff, signed: true);
            var gapAt = new int[nf]; var squashAt = new int[nf];
            var gapSev = ToFaces(vGapSev, gapAt);
            var squashSev = ToFaces(vSquashSev, squashAt);
            r.SquashedPoints = vSquashSev.Count(x => x > 1);
            if (!reportSquashed) squashSev = new double[nf];

            // Structure, mapped onto the wall.
            var check = r.Check;
            r.Interlayer = check.HasStrength ? grid.ToDisplay(check.FinalInterlayer.Utilisation) : Nan(nf);
            var stageU = Nan(grid.Mesh.FaceCount);
            var stageLayer = new int[grid.Mesh.FaceCount];
            if (check.HasStrength)
                foreach (var st in check.Stages)
                {
                    if (st.Skipped != null || st.Utilisation == null) continue;
                    for (int g = 0; g < stageU.Length; g++)
                        if (!double.IsNaN(st.Utilisation[g]) && (double.IsNaN(stageU[g]) || st.Utilisation[g] > stageU[g]))
                        { stageU[g] = st.Utilisation[g]; stageLayer[g] = st.Layer; }
                }
            r.StageUtilisation = grid.ToDisplay(stageU);
            var stageLayerD = grid.ToDisplay(stageLayer.Select(x => (double)x).ToArray());

            Vec3[] mode = null;
            if (check.FinalBuckling != null)
            {
                r.BucklingFactor = check.FinalBuckling.Critical;
                mode = check.FinalBuckling.Modes.Length > 0 ? check.FinalBuckling.Modes[0] : null;
                if (check.BucklingStage >= 0 && check.Stages[check.BucklingStage].BucklingFactor < r.BucklingFactor)
                {
                    var st = check.Stages[check.BucklingStage];
                    r.BucklingFactor = st.BucklingFactor;
                    r.BucklingLayer = st.Layer;
                    mode = st.BucklingMode;
                }
            }
            var am = grid.Mesh;
            var shapeA = Nan(am.FaceCount);
            if (mode != null)
            {
                for (int g = 0; g < am.FaceCount; g++)
                {
                    var t = am.Faces[g];
                    shapeA[g] = (mode[t[0]].Length + mode[t[1]].Length + mode[t[2]].Length) / 3.0;
                }
                double mx = shapeA.Where(x => !double.IsNaN(x)).DefaultIfEmpty(0).Max();
                if (mx > 0) for (int g = 0; g < shapeA.Length; g++) shapeA[g] /= mx;
            }
            r.BucklingShape = grid.ToDisplay(shapeA);

            // Severity per check: value / limit.
            double lam = r.BucklingFactor;
            bool buckles = !double.IsNaN(lam) && !double.IsInfinity(lam) && lam < BucklingMargin;
            var checks = new List<(IssueKind kind, double[] sev)>
            {
                (IssueKind.TallLayer, r.LayerHeight.Select(h => h / pr.MaxLayer).ToArray()),
                (IssueKind.LowLayer, low),
                (IssueKind.PoorContact, r.ContactRatio.Select(x => x / PrintPlan.PoorContactRatio).ToArray()),
                (IssueKind.Overhang, r.Lean.Select(x => x / pr.MaxOverhang).ToArray()),
                (IssueKind.Unsupported, r.BeadOffset.Select(x => x / 0.5).ToArray()),
                (IssueKind.Tilt, r.Tilt.Select(x => x / pr.MaxTilt).ToArray()),
                (IssueKind.LayerBond, r.Interlayer),
                (IssueKind.PrintStage, r.StageUtilisation),
                (IssueKind.Buckling, r.BucklingShape.Select(x => buckles && x >= 0.5 ? BucklingMargin / Math.Max(lam, 1e-9) : 0).ToArray()),
                (IssueKind.LayerGap, gapSev),
                (IssueKind.Squashed, squashSev),
            };
            var severity = new double[nf];
            foreach (var (_, sev) in checks)
                for (int f = 0; f < nf; f++)
                    if (!double.IsNaN(sev[f])) severity[f] = Math.Max(severity[f], sev[f]);
            r.Severity = severity;

            // Areas: neighbouring faces failing the same check.
            var adj = FaceNeighbours(mesh);
            var issues = new List<LayerIssue>();
            foreach (var (kind, sev) in checks)
            {
                var seen = new bool[nf];
                for (int f0 = 0; f0 < nf; f0++)
                {
                    if (seen[f0] || !(sev[f0] > 1)) continue;
                    var area = new List<int>();
                    var queue = new Queue<int>();
                    queue.Enqueue(f0); seen[f0] = true;
                    while (queue.Count > 0)
                    {
                        int f = queue.Dequeue();
                        area.Add(f);
                        foreach (int g in adj[f])
                            if (!seen[g] && sev[g] > 1) { seen[g] = true; queue.Enqueue(g); }
                    }
                    int worst = area.OrderByDescending(f => sev[f]).First();
                    var issue = new LayerIssue
                    {
                        Kind = kind,
                        Faces = area.ToArray(),
                        Severity = sev[worst],
                        Location = mesh.FaceCentroid(worst),
                        // The first strip also shows the first layer (0); the others their upper layer.
                        FirstLayer = area.Min(f => r.FaceLayer[f] == 0 ? 0 : r.FaceLayer[f] + 1),
                        LastLayer = area.Max(f => r.FaceLayer[f]) + 1,
                    };
                    // Gaps and squashed layers: at the bead itself, with its own height and spacing.
                    int bead = kind == IssueKind.LayerGap ? gapAt[worst] : kind == IssueKind.Squashed ? squashAt[worst] : -1;
                    if (bead >= 0) issue.Location = mesh.Vertices[bead];
                    Describe(issue, r, worst, (int)stageLayerD[worst],
                        bead >= 0 ? vH[bead] : double.NaN, bead >= 0 ? spacing[bead] : double.NaN);
                    issues.Add(issue);
                }
            }
            r.Issues = issues.OrderByDescending(i => i.Severity).ToList();
        }

        private static void Describe(LayerIssue i, LayerReviewResult r, int f, int stageLayer, double beadHeight, double beadSpacing)
        {
            var pr = r.Printer;
            double h = r.LayerHeight[f], w = r.Thickness[f];
            switch (i.Kind)
            {
                case IssueKind.TallLayer:
                    i.Title = "Layer too tall";
                    i.Reason = $"Layers up to {h:F2} mm tall, above the {pr.MaxLayer:G3} mm maximum for a {pr.Nozzle:G3} mm nozzle (75 % of the nozzle): the bead can slump or lose contact.";
                    i.Advice = "Lower the layer height here (move these layers closer together).";
                    break;
                case IssueKind.LowLayer:
                    i.Title = "Layer too low";
                    i.Reason = $"Layers only {h:F2} mm tall, below the {pr.MinLayer:G3} mm minimum (a third of the nozzle): the nozzle squashes and drags the layer below.";
                    i.Advice = "Raise the layer height here, or use a smaller nozzle.";
                    break;
                case IssueKind.PoorContact:
                    i.Title = "Poor layer contact";
                    i.Reason = $"The layer is {h / w:P0} of the bead width ({h:F2} / {w:F2} mm), so the bead overlaps the one below by only {Math.Max(0, (w - h) / w):P0} of its width and the layers barely bond.";
                    i.Advice = "Use lower layers here, or a wider bead.";
                    break;
                case IssueKind.Overhang:
                    i.Title = "Overhang";
                    i.Reason = $"The wall leans {r.Lean[f]:F0}° from vertical (limit {pr.MaxOverhang:G3}°).";
                    i.Advice = "Make the wall steeper here, or lower the layers so each bead rests on the one below.";
                    break;
                case IssueKind.Unsupported:
                    i.Title = "Bead not supported";
                    i.Reason = $"Each bead sits {r.BeadOffset[f]:P0} of its width past the bead below (limit 50 %).";
                    i.Advice = "Lower the layer height where the wall leans, or reduce the lean.";
                    break;
                case IssueKind.Tilt:
                    i.Title = "Layer too steep";
                    i.Reason = $"The layer slopes {r.Tilt[f]:F0}° along the path (limit {pr.MaxTilt:G3}°): a nozzle that can't tilt may scrape the bead.";
                    i.Advice = "Make the layer flatter along the path here.";
                    break;
                case IssueKind.LayerBond:
                    i.Title = "Layers come apart";
                    i.Reason = $"Interlayer utilisation {r.Interlayer[f]:P0} in the finished piece: the bond between the layers here carries more than it can.";
                    i.Advice = "Thicken the wall here (Wall = Ideal shows where), use lower layers, or change the shape so the load runs across the layers.";
                    break;
                case IssueKind.PrintStage:
                    i.Title = "Fails while printing";
                    i.Reason = $"Interlayer utilisation {r.StageUtilisation[f]:P0} when the piece is printed up to layer {stageLayer}: it can fail before it is finished " +
                        "(the check assumes the clay does not stiffen while printing).";
                    i.Advice = "Reduce the overhang below this height, or thicken the wall here.";
                    break;
                case IssueKind.LayerGap:
                    i.Title = "Gap under the layer";
                    i.Reason = $"The bead is {beadHeight:F2} mm tall but the layer below is {beadSpacing:F2} mm down: it stops " +
                        $"{beadSpacing - beadHeight:F2} mm short, so it doesn't touch (or bond to) the bead below and may sag into the gap.";
                    i.Advice = "Make the height here match the spacing of your layers (or move this layer down).";
                    break;
                case IssueKind.Squashed:
                    i.Title = "Layer squashed";
                    i.Reason = $"The bead is {beadHeight:F2} mm tall but the layer below is only {beadSpacing:F2} mm down: the nozzle " +
                        $"pushes {beadHeight - beadSpacing:F2} mm into the bead below (too much clay, it bulges out).";
                    i.Advice = "Make the height here match the spacing of your layers (or move this layer up).";
                    break;
                case IssueKind.Buckling:
                    i.Title = "Buckling";
                    i.Reason = $"The {(r.BucklingLayer >= 0 ? $"piece printed up to layer {r.BucklingLayer}" : "finished piece")} buckles here at {r.BucklingFactor:G3} × its own weight " +
                        $"(aim for at least {BucklingMargin:G2} ×; real walls are imperfect).";
                    i.Advice = "Make the wall thicker or more curved here, or the piece lower.";
                    break;
            }
        }

        private static List<int>[] FaceNeighbours(TriMesh mesh)
        {
            int n = mesh.VertexCount;
            var byEdge = new Dictionary<long, List<int>>();
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                var t = mesh.Faces[f];
                for (int e = 0; e < 3; e++)
                {
                    int a = t[e], b = t[(e + 1) % 3];
                    long key = a < b ? (long)a * n + b : (long)b * n + a;
                    if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = new List<int>(2);
                    list.Add(f);
                }
            }
            var result = new List<int>[mesh.FaceCount];
            for (int f = 0; f < result.Length; f++) result[f] = new List<int>(3);
            foreach (var list in byEdge.Values)
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++) { result[list[i]].Add(list[j]); result[list[j]].Add(list[i]); }
            return result;
        }
    }
}
