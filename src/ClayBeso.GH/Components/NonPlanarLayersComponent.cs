using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.GH.Common;
using ClayBeso.Printing;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Rhino.Geometry;

namespace ClayBeso.GH.Components
{
    public class NonPlanarLayersComponent : LegendComponent
    {
        public NonPlanarLayersComponent()
            : base("Non-planar Layers", "Layers",
                "Legacy: replaced by Print Layers, which does this with fewer connections. Kept so older definitions still open.\n" +
                "Slice the shell into non-planar layer toolpaths whose spacing follows the target layer heights.\n" +
                "Method 0 = Conforming: layers fit both the base and the top rim, so every layer is a closed loop " +
                "(heights matched as closely as possible). Check the error output.\n" +
                "Method 1 = Eikonal: exact local layer heights grown from the base. The top may end in partial layers.\n" +
                "Method 2 = Force-aligned: like Conforming, but layers turn to lie across the force flow (Force Flow from " +
                "Shell Analysis), so interfaces carry compression rather than shear (Vele, Kurilla & Achten 2023).\n" +
                "Also checks printability per point: wall lean across the bead, bead offset past the bead below, and " +
                "layer tilt along the path (nozzle scraping on a 3-axis printer).",
                Tab.Name, Tab.Print) { }

        public override Guid ComponentGuid => new Guid("f3c6b8d7-a609-4053-9eb6-5b7a2c4f9d06");
        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override IEnumerable<Dropdown> Dropdowns => new[] { new Dropdown("Method", 0, Modes.LayerMethod) };
        protected override System.Drawing.Bitmap Icon => Icons.Get("NonPlanarLayers");

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell model (for the mesh).", GH_ParamAccess.item);
            p.AddNumberParameter("Layer Heights", "h", "Target layer height per vertex [mm] (from Layer Heights).", GH_ParamAccess.list);
            p.AddNumberParameter("Vertex Thickness", "tv", "Optional bead width per vertex [mm], passed through per point.", GH_ParamAccess.list);
            p.AddIntegerParameter("Method", "Mth", "0 = Conforming (closed layers, fits base and rim), 1 = Eikonal (exact heights), 2 = Force-aligned (needs Force Flow).", GH_ParamAccess.item, 0);
            p.AddBooleanParameter("Vertical", "Vz", "Measure layer height vertically (Z) rather than along the wall.", GH_ParamAccess.item, true);
            p.AddNumberParameter("Tolerance", "Tol", "Band (document units) at the bottom/top used to find the base and the rim.", GH_ParamAccess.item, 0.5);
            p.AddVectorParameter("Seam", "Seam", "Closed layers start at the point furthest in this direction.", GH_ParamAccess.item, new Vector3d(1, 0, 0));
            p.AddNumberParameter("Nominal Area", "A0", "Bead cross-section [mm²] that corresponds to 100% flow. 0 = median of the paths.", GH_ParamAccess.item, 0.0);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the height-error colour scale [%], e.g. -20 to 20. Empty = symmetric about 0.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            p.AddVectorParameter("Force Flow", "FF", "Method 2: force flow per mesh face, from the Force Flow output of Shell Analysis (or Interlayer Check).", GH_ParamAccess.list);
            p.AddNumberParameter("Max Deviation", "Dev", "Method 2: most the layers may turn away from the conforming layering [deg]. Larger = more aligned with the force, but steeper layer tilts.", GH_ParamAccess.item, 30.0);
            p.AddNumberParameter("Max Overhang", "OvMax", "Wall lean from vertical [deg] above which a point is flagged. 45 = where planar clay prints started to slump in Vele and Achten (2023); calibrate with docs/VALIDATION.md section 7.", GH_ParamAccess.item, 45.0);
            p.AddNumberParameter("Max Tilt", "TiltMax", "Layer slope along the path [deg] above which a point is flagged (a nozzle that cannot tilt scrapes steep layers). Calibrate with docs/VALIDATION.md section 7.", GH_ParamAccess.item, 30.0);
            for (int i = 2; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddCurveParameter("Layers", "L", "Layer toolpaths (one branch per layer), on the wall mid-surface, in document units.", GH_ParamAccess.tree);
            p.AddNumberParameter("Target Height", "h", "Target layer height at each path point [mm].", GH_ParamAccess.tree);
            p.AddNumberParameter("Realised Height", "hr", "Layer height the layers actually have at each point [mm].", GH_ParamAccess.tree);
            p.AddNumberParameter("Width", "w", "Bead width (wall thickness) at each point [mm].", GH_ParamAccess.tree);
            p.AddNumberParameter("Layer Field", "φ", "Layer index field per vertex (layer k is φ = k).", GH_ParamAccess.list);
            p.AddIntegerParameter("Layer Count", "n", "Number of layers.", GH_ParamAccess.item);
            p.AddMeshParameter("Preview", "P", "Mesh coloured by relative layer-height error (realised vs target).", GH_ParamAccess.item);
            p.AddTextParameter("Info", "I", "Summary.", GH_ParamAccess.item);
            p.AddNumberParameter("Bead Area", "A", "Bead cross-section at each point [mm²] for width w and the realised height. Extruded volume per mm of path. Needs Vertex Thickness.", GH_ParamAccess.tree);
            p.AddNumberParameter("Flow Factor", "F", "Flow multiplier at each point (1 = Nominal Area). Multiply your nominal extrusion / flow % by this, so the wall gets width w at the realised layer height.", GH_ParamAccess.tree);
            p.AddNumberParameter("Wall Lean", "Lean", "Per point: wall lean from vertical [deg], across the bead. 0 = vertical wall.", GH_ParamAccess.tree);
            p.AddNumberParameter("Layer Tilt", "Tilt", "Per point: slope of the layer along the path [deg].", GH_ParamAccess.tree);
            p.AddNumberParameter("Bead Offset", "Off", "Per point: how far the bead sits past the one below [mm] = realised height x tan(lean).", GH_ParamAccess.tree);
            p.AddNumberParameter("Overhang Ratio", "OvR", "Per point: bead offset / bead width, the unsupported fraction of the bead. Needs Vertex Thickness.", GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object modelObj = null;
            var h = new List<double>();
            var tv = new List<double>();
            int method = 0;
            bool vertical = true;
            double tol = 0.5, nominal = 0;
            var seam = new Vector3d(1, 0, 0);
            if (!da.GetData(0, ref modelObj)) return;
            if (!da.GetDataList(1, h)) return;
            da.GetDataList(2, tv);
            da.GetData(3, ref method);
            da.GetData(4, ref vertical);
            da.GetData(5, ref tol);
            da.GetData(6, ref seam);
            da.GetData(7, ref nominal);
            bool legend = true;
            da.GetData(9, ref legend);
            var flowIn = new List<Vector3d>();
            double maxDev = 30, maxOverhang = 45, maxTilt = 30;
            da.GetDataList(10, flowIn);
            da.GetData(11, ref maxDev);
            da.GetData(12, ref maxOverhang);
            da.GetData(13, ref maxTilt);

            var model = RhinoConvert.Unwrap<ShellModel>(modelObj);
            if (model == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a Shell Model."); return; }

            try
            {
                var mesh = model.Mesh;
                double s = model.UnitScale;
                var hv = RhinoConvert.Expand(h, mesh.VertexCount, "Layer Heights");
                var thick = tv.Count > 0 ? RhinoConvert.Expand(tv, mesh.VertexCount, "Vertex Thickness") : null;

                var (bottom, top) = LayerField.ExtremeVertices(mesh, Vec3.UnitZ, tol * s);
                var m = method == 1 ? LayerMethod.Eikonal : method == 2 ? LayerMethod.ForceAligned : LayerMethod.Conforming;
                Vec3[] flowField = null;
                if (m == LayerMethod.ForceAligned)
                {
                    if (flowIn.Count != mesh.FaceCount)
                        throw new ArgumentException($"Method 2 needs Force Flow with one vector per mesh face ({mesh.FaceCount}), got {flowIn.Count}. " +
                                                    "Connect the Force Flow output of Shell Analysis for this model.");
                    flowField = FlowConvert.FromVectors(flowIn);
                }
                var field = LayerField.Compute(mesh, hv, bottom, top, Vec3.UnitZ, vertical, m, flowField, maxDev);
                var paths = LayerSlicer.Slice(field, thick, Vec3.UnitZ, RhinoConvert.ToVec(seam));

                var curves = new DataTree<Curve>();
                var th = new DataTree<double>();
                var rh = new DataTree<double>();
                var wd = new DataTree<double>();
                var ba = new DataTree<double>();
                var ff = new DataTree<double>();
                var leanT = new DataTree<double>();
                var tiltT = new DataTree<double>();
                var offT = new DataTree<double>();
                var ratioT = new DataTree<double>();
                int nPts = 0, nLean = 0, nTilt = 0, nRatio = 0;
                double maxLeanSeen = 0, maxTiltSeen = 0, maxRatioSeen = 0;
                var allAreas = paths.SelectMany(q => q.BeadArea).Where(a => a > 0).OrderBy(a => a).ToArray();
                double a0 = nominal > 0 ? nominal : (allAreas.Length > 0 ? allAreas[allAreas.Length / 2] : 0);
                int open = 0;
                foreach (var path in paths)
                {
                    var gp = new GH_Path(path.Layer - 1);
                    var pts = path.Points.Select(q => RhinoConvert.ToPoint(q, s)).ToList();
                    if (path.Closed) pts.Add(pts[0]); else open++;
                    curves.Add(new PolylineCurve(pts), gp);
                    th.AddRange(path.TargetHeight, gp);
                    rh.AddRange(path.RealisedHeight, gp);
                    if (thick != null) wd.AddRange(path.Width, gp);
                    if (thick != null && a0 > 0)
                    {
                        ba.AddRange(path.BeadArea, gp);
                        ff.AddRange(path.BeadArea.Select(a => a / a0), gp);
                    }

                    var ov = OverhangCheck.Evaluate(path, Vec3.UnitZ, vertical);
                    leanT.AddRange(ov.Lean, gp);
                    tiltT.AddRange(ov.Tilt, gp);
                    offT.AddRange(ov.Offset, gp);
                    if (thick != null) ratioT.AddRange(ov.Ratio, gp);
                    for (int i = 0; i < ov.Lean.Count; i++)
                    {
                        nPts++;
                        if (ov.Lean[i] > maxOverhang) nLean++;
                        if (ov.Tilt[i] > maxTilt) nTilt++;
                        if (ov.Ratio[i] > 0.5) nRatio++;
                        if (!double.IsNaN(ov.Lean[i])) maxLeanSeen = Math.Max(maxLeanSeen, ov.Lean[i]);
                        maxTiltSeen = Math.Max(maxTiltSeen, ov.Tilt[i]);
                        if (!double.IsNaN(ov.Ratio[i])) maxRatioSeen = Math.Max(maxRatioSeen, ov.Ratio[i]);
                    }
                }

                // Relative error per face for the preview and the summary.
                var real = field.RealisedFaceHeight();
                var err = new double[mesh.FaceCount];
                for (int f = 0; f < err.Length; f++)
                {
                    var t = mesh.Faces[f];
                    double target = (hv[t[0]] + hv[t[1]] + hv[t[2]]) / 3.0;
                    err[f] = double.IsInfinity(real[f]) ? double.NaN : (real[f] - target) / target;
                }
                var finite = err.Where(e => !double.IsNaN(e)).Select(Math.Abs).ToArray();

                da.SetDataTree(0, curves);
                da.SetDataTree(1, th);
                da.SetDataTree(2, rh);
                da.SetDataTree(3, wd);
                da.SetDataList(4, field.Phi);
                da.SetData(5, field.LayerCount);
                var errPct = err.Select(e => 100 * e).ToArray();
                double worst = Math.Max(1.0, errPct.Where(e => !double.IsNaN(e)).Select(Math.Abs).DefaultIfEmpty(0).Max());
                var range = ReadRange(da, 8, (-worst, worst));
                da.SetData(6, Preview.FaceColoured(mesh, errPct, s, out var usedRange, range));
                SetLegend("Layer height error", "% (realised vs target)", usedRange, legend);
                da.SetDataTree(8, ba);
                da.SetDataTree(9, ff);
                da.SetDataTree(10, leanT);
                da.SetDataTree(11, tiltT);
                da.SetDataTree(12, offT);
                da.SetDataTree(13, ratioT);
                string printability =
                    $"\nPrintability: max wall lean {maxLeanSeen:F1}° ({nLean} points > {maxOverhang:G3}°), " +
                    $"max layer tilt {maxTiltSeen:F1}° ({nTilt} points > {maxTilt:G3}°)" +
                    (thick != null ? $", max overhang ratio {maxRatioSeen:F2} ({nRatio} points > 0.5)" : "");
                string flow = allAreas.Length > 0
                    ? $"\nNominal bead area {a0:F2} mm², flow factor {allAreas[0] / a0:F2}-{allAreas[allAreas.Length - 1] / a0:F2}"
                    : "\n(connect Vertex Thickness to get bead area and flow)";
                da.SetData(7,
                    $"{m} method, {field.LayerCount} layers, {paths.Count} paths ({open} open)\n" +
                    $"Base vertices: {field.BaseVertices.Length}, rim vertices: {field.TopVertices.Length}\n" +
                    $"Layer height error: mean {finite.DefaultIfEmpty(0).Average():P1}, max {finite.DefaultIfEmpty(0).Max():P1}" + flow + printability);

                if (open > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{open} open (partial) layer paths.");
                if (nLean > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"{nLean} of {nPts} path points lean more than {maxOverhang:G3}° from vertical: planar clay prints start to slump there. " +
                        "Lower the layers in that region, or use Method 2 (force-aligned).");
                if (nRatio > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"{nRatio} path points have a bead overhanging the one below by more than half its width (Overhang Ratio > 0.5).");
                if (nTilt > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"{nTilt} path points have layers tilting more than {maxTilt:G3}° along the path: a nozzle that cannot tilt may scrape there.");
                if (finite.Length > 0 && finite.Average() > 0.15)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Realised layer heights differ from the targets by >15% on average. The target field " +
                        "probably can't fit a flat rim. The Flow Factor still gives the right wall width, but check " +
                        "the Realised Height stays printable, or try Method 1 or more smoothing.");
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }
    }
}
