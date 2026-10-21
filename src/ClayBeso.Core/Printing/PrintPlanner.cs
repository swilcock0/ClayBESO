using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.Geometry;

namespace ClayBeso.Printing
{
    public sealed class PrintSettings
    {
        public LayerMethod Method { get; set; } = LayerMethod.Conforming;

        /// <summary>Force-aligned: most the layers may turn from the conforming layering [deg].</summary>
        public double MaxDeviation { get; set; } = 30;

        /// <summary>Layer heights measured in Z (true) or along the wall.</summary>
        public bool Vertical { get; set; } = true;

        /// <summary>Smoothing passes on the layer-height field.</summary>
        public int Smoothing { get; set; } = 5;

        /// <summary>Band at the bottom/top that defines the base and the rim [mm].</summary>
        public double BaseTolerance { get; set; } = 0.5;

        /// <summary>Closed layers start at the point furthest along this direction.</summary>
        public Vec3 Seam { get; set; } = new Vec3(1, 0, 0);

        /// <summary>Bead area [mm^2] for flow factor 1; 0 = median of the paths.</summary>
        public double NominalArea { get; set; }

        public double MaxOverhang { get; set; } = 45;
        public double MaxTilt { get; set; } = 30;
    }

    /// <summary>Everything needed to print and check a shell: layer field, toolpaths and per-point data.</summary>
    public sealed class PrintPlan
    {
        public ShellDesign Design { get; internal set; }
        public PrintSettings Settings { get; internal set; }

        /// <summary>The resolved layer rule actually used.</summary>
        public LayerRule Rule { get; internal set; }

        public double[] VertexHeight { get; internal set; }
        public double[] VertexThickness { get; internal set; }
        public LayerField Field { get; internal set; }
        public List<LayerPath> Paths { get; internal set; }
        public List<OverhangResult> Overhangs { get; internal set; }

        /// <summary>Bead area for flow factor 1 [mm^2].</summary>
        public double NominalArea { get; internal set; }

        /// <summary>Force flow used by the force-aligned method (null otherwise).</summary>
        public ForceFlowField Flow { get; internal set; }

        /// <summary>Relative error realised vs target layer height per face (NaN where undefined).</summary>
        public double[] HeightError { get; internal set; }

        public double FlowFactor(double beadArea) => NominalArea > 0 ? beadArea / NominalArea : double.NaN;

        public int OpenPaths => Paths.Count(p => !p.Closed);
        public int LeanFlags => Overhangs.Sum(o => o.Lean.Count(x => x > Settings.MaxOverhang));
        public int TiltFlags => Overhangs.Sum(o => o.Tilt.Count(x => x > Settings.MaxTilt));
        public int RatioFlags => Overhangs.Sum(o => o.Ratio.Count(x => x > 0.5));
        public int PointCount => Paths.Sum(p => p.Points.Count);

        /// <summary>
        /// Path points whose layer is taller than <see cref="PoorContactRatio"/> times the bead width: the
        /// bead overlaps the one below by less than 20 % of its width, so the layers barely bond.
        /// </summary>
        public int PoorContactFlags => Paths.Sum(p =>
            Enumerable.Range(0, p.Points.Count).Count(i => p.RealisedHeight[i] > PoorContactRatio * p.Width[i]));

        /// <summary>Realised layer height / bead width above which a point is flagged (contact c below 0.2).</summary>
        public const double PoorContactRatio = 0.8;
    }

    /// <summary>
    /// Thickness → layer heights → non-planar layer field → toolpaths, plus the printability data,
    /// in one step. The force-aligned method runs its own analysis for the force flow.
    /// </summary>
    public static class PrintPlanner
    {
        /// <summary>Default rule when neither the design nor the caller gives one.</summary>
        public static LayerRule DefaultRule() => new LayerRule(LayerMapping.Proportional, 1.0, 2.25);

        public static PrintPlan Plan(ShellDesign design, PrintSettings settings, LayerRule rule = null)
        {
            var model = design.Model;
            var mesh = model.Mesh;
            var t = design.Thickness;
            var up = Vec3.UnitZ;

            // Rule: explicit > the design's (already resolved by BESO) > default; then fill automatic values.
            var r = (rule ?? design.Rule ?? DefaultRule()).Resolve(mesh.FaceToVertex(t));

            var h = LayerHeightField.Compute(mesh, t, r, settings.Smoothing, out _);
            var tv = mesh.FaceToVertex(t);
            var (bottom, top) = LayerField.ExtremeVertices(mesh, up, settings.BaseTolerance);

            ForceFlowField flow = null;
            Vec3[] flowVectors = null;
            if (settings.Method == LayerMethod.ForceAligned)
            {
                var scaling = ElementScaling.FromThickness(t.Select(x => Math.Max(x, 1e-6)).ToArray());
                if (design.Rule != null && design.Bond != null)
                {
                    // Same layered stiffness the design was optimised with.
                    model.EnableLayerAnisotropy(up);
                    for (int e = 0; e < t.Length; e++)
                        scaling.Across[e] = design.Bond.Efficiency * design.Bond.Contact(t[e], design.Rule.Height(t[e]));
                }
                var res = new ShellSolver(model).Solve(scaling);
                flow = ForceFlow.Compute(model, res, scaling);
                flowVectors = flow.Direction.Select((d, e) => d * flow.Anisotropy[e]).ToArray();
            }

            var field = LayerField.Compute(mesh, h, bottom, top, up, settings.Vertical, settings.Method,
                flowVectors, settings.MaxDeviation, maxHeight: r.HMax);
            var paths = LayerSlicer.Slice(field, tv, up, settings.Seam);
            var overhangs = paths.Select(p => OverhangCheck.Evaluate(p, up, settings.Vertical)).ToList();

            var areas = paths.SelectMany(p => p.BeadArea).Where(a => a > 0).OrderBy(a => a).ToArray();
            double nominal = settings.NominalArea > 0 ? settings.NominalArea : (areas.Length > 0 ? areas[areas.Length / 2] : 0);

            var real = field.RealisedFaceHeight();
            var err = new double[mesh.FaceCount];
            for (int f = 0; f < err.Length; f++)
            {
                var tri = mesh.Faces[f];
                double target = (h[tri[0]] + h[tri[1]] + h[tri[2]]) / 3.0;
                err[f] = double.IsInfinity(real[f]) ? double.NaN : (real[f] - target) / target;
            }

            return new PrintPlan
            {
                Design = design,
                Settings = settings,
                Rule = r,
                VertexHeight = h,
                VertexThickness = tv,
                Field = field,
                Paths = paths,
                Overhangs = overhangs,
                NominalArea = nominal,
                Flow = flow,
                HeightError = err,
            };
        }
    }
}
