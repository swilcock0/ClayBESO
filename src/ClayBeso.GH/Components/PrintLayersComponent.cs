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
    public class PrintLayersComponent : LegendComponent
    {
        public PrintLayersComponent()
            : base("Print Layers", "Print",
                "From a design to printable non-planar layers in one step: layer heights from the wall thickness " +
                "(Layer Rule), the layer field, the toolpaths, the flow factor and the overhang check.\n" +
                "Connect the Design output of Shell BESO (it carries the model, thickness, Layer Rule and bond), " +
                "or a Shell Model for its own uniform wall.\n" +
                "Method: Conforming (closed layers fitting base and rim), Eikonal (exact heights from the base), or " +
                "Force-aligned (layers turn to lie across the force flow; the force flow is computed here, nothing to wire).\n" +
                "Connect the Print output to Print Check.",
                Tab.Name, Tab.Print) { }

        public override Guid ComponentGuid => new Guid("c8b1e4a6-fddc-4a3f-9af7-4c19add0c046");
        protected override System.Drawing.Bitmap Icon => Icons.Get("PrintLayers");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        private static readonly (string, int)[] ShowOptions =
        {
            ("Layer height", 0),
            ("Layer height error", 1),
            ("Wall thickness", 2),
            ("Wall lean", 3),
        };

        protected override IEnumerable<Dropdown> Dropdowns => new[]
        {
            new Dropdown("Method", 0, Modes.LayerMethod),
            new Dropdown("Show", 0, ShowOptions),
        };

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Design", "Design", "Design output of Shell BESO, or a Shell Model (uses its own thickness).", GH_ParamAccess.item);
            p.AddIntegerParameter("Method", "Method", "0 = Conforming, 1 = Eikonal, 2 = Force-aligned (a dropdown appears when you place the component).", GH_ParamAccess.item, 0);
            p.AddIntegerParameter("Show", "Show", "Preview colours: 0 = layer height, 1 = layer-height error, 2 = wall thickness, 3 = wall lean (a dropdown appears when you place the component).", GH_ParamAccess.item, 0);
            p.AddGenericParameter("Layer Rule", "Layer Rule", "Optional Layer Rule, replacing the design's. Default (none anywhere): proportional, 1-2.25 mm.", GH_ParamAccess.item);
            p.AddNumberParameter("Thickness", "Thickness", "Optional wall thickness per face [mm], replacing the design's.", GH_ParamAccess.list);
            p.AddNumberParameter("Max Deviation", "Dev", "Force-aligned: most the layers may turn away from the conforming layering [deg].", GH_ParamAccess.item, 30.0);
            p.AddNumberParameter("Max Overhang", "OvMax", "Wall lean from vertical [deg] above which a point is flagged (planar clay starts to slump around 45°; calibrate with docs/VALIDATION.md section 7).", GH_ParamAccess.item, 45.0);
            p.AddNumberParameter("Max Tilt", "TiltMax", "Layer slope along the path [deg] above which a point is flagged (nozzle scraping on a 3-axis printer).", GH_ParamAccess.item, 30.0);
            p.AddBooleanParameter("Vertical", "Vz", "Measure layer height vertically (Z) rather than along the wall.", GH_ParamAccess.item, true);
            p.AddIntegerParameter("Smoothing", "Sm", "Smoothing passes on the layer-height field.", GH_ParamAccess.item, 5);
            p.AddNumberParameter("Tolerance", "Tol", "Band (document units) at the bottom/top used to find the base and the rim.", GH_ParamAccess.item, 0.5);
            p.AddVectorParameter("Seam", "Seam", "Closed layers start at the point furthest in this direction.", GH_ParamAccess.item, new Vector3d(1, 0, 0));
            p.AddNumberParameter("Nominal Area", "A0", "Bead cross-section [mm²] for flow factor 1. 0 = median of the paths.", GH_ParamAccess.item, 0.0);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the preview colour scale. Empty = automatic for what is shown.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddCurveParameter("Layers", "Layers", "Layer toolpaths (one branch per layer) on the wall mid-surface, in document units.", GH_ParamAccess.tree);
            p.AddNumberParameter("Flow Factor", "Flow Factor", "Per point: flow multiplier (1 = Nominal Area) that gives the wall its thickness at the realised layer height.", GH_ParamAccess.tree);
            p.AddGenericParameter("Print", "Print", "Everything about this print (design, layers, paths). Connect it to Print Check.", GH_ParamAccess.item);
            p.AddMeshParameter("Preview", "Preview", "Mesh coloured by the Show option.", GH_ParamAccess.item);
            p.AddTextParameter("Info", "Info", "Summary: layers, flow range, printability warnings.", GH_ParamAccess.item);
            p.AddNumberParameter("Realised Height", "Realised Height", "Per point: layer height the layers actually have [mm].", GH_ParamAccess.tree);
            p.AddNumberParameter("Width", "Width", "Per point: bead width = wall thickness [mm].", GH_ParamAccess.tree);
            p.AddNumberParameter("Overhang Ratio", "OvR", "Per point: how far the bead sits past the one below, as a fraction of its width (> 0.5 = more than half unsupported).", GH_ParamAccess.tree);
            p.AddNumberParameter("Layer Tilt", "Tilt", "Per point: slope of the layer along the path [deg].", GH_ParamAccess.tree);
            p.AddNumberParameter("Wall Lean", "Lean", "Per point: wall lean from vertical [deg], across the bead.", GH_ParamAccess.tree);
            p.AddNumberParameter("Bead Offset", "Off", "Per point: horizontal offset past the bead below [mm].", GH_ParamAccess.tree);
            p.AddNumberParameter("Bead Area", "A", "Per point: bead cross-section [mm²] (extruded volume per mm of path).", GH_ParamAccess.tree);
            p.AddNumberParameter("Target Height", "h", "Per point: target layer height from the Layer Rule [mm].", GH_ParamAccess.tree);
            p.AddNumberParameter("Layer Field", "φ", "Per vertex: layer index field (layer k is φ = k).", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object designObj = null, ruleObj = null;
            var thick = new List<double>();
            int method = 0, smoothing = 5, show = 0;
            double maxDev = 30, tol = 0.5, nominal = 0, maxOverhang = 45, maxTilt = 30;
            bool vertical = true, legend = true;
            var seam = new Vector3d(1, 0, 0);
            if (!da.GetData(In("Design"), ref designObj)) return;
            da.GetDataList(In("Thickness"), thick);
            da.GetData(In("Layer Rule"), ref ruleObj);
            da.GetData(In("Method"), ref method);
            da.GetData(In("Max Deviation"), ref maxDev);
            da.GetData(In("Vertical"), ref vertical);
            da.GetData(In("Smoothing"), ref smoothing);
            da.GetData(In("Tolerance"), ref tol);
            da.GetData(In("Seam"), ref seam);
            da.GetData(In("Nominal Area"), ref nominal);
            da.GetData(In("Max Overhang"), ref maxOverhang);
            da.GetData(In("Max Tilt"), ref maxTilt);
            da.GetData(In("Show"), ref show);
            da.GetData(In("Legend"), ref legend);

            try
            {
                var design = ToDesign(designObj, thick);
                var model = design.Model;
                double s = model.UnitScale;
                var settings = new PrintSettings
                {
                    Method = method == 1 ? LayerMethod.Eikonal : method == 2 ? LayerMethod.ForceAligned : LayerMethod.Conforming,
                    MaxDeviation = maxDev,
                    Vertical = vertical,
                    Smoothing = Math.Max(0, smoothing),
                    BaseTolerance = tol * s,
                    Seam = RhinoConvert.ToVec(seam),
                    NominalArea = nominal,
                    MaxOverhang = maxOverhang,
                    MaxTilt = maxTilt,
                };
                var plan = PrintPlanner.Plan(design, settings, RhinoConvert.Unwrap<LayerRule>(ruleObj));
                if (design.Thickness.Any(x => x <= 0))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "The design has removed faces (void mode): layers can't run continuously across the holes.");

                var trees = new Dictionary<string, DataTree<double>>();
                DataTree<double> T(string k) => trees.TryGetValue(k, out var t) ? t : trees[k] = new DataTree<double>();
                var curves = new DataTree<Curve>();
                for (int i = 0; i < plan.Paths.Count; i++)
                {
                    var path = plan.Paths[i];
                    var ov = plan.Overhangs[i];
                    var gp = new GH_Path(path.Layer - 1);
                    var pts = path.Points.Select(q => RhinoConvert.ToPoint(q, s)).ToList();
                    if (path.Closed) pts.Add(pts[0]);
                    curves.Add(new PolylineCurve(pts), gp);
                    T("flow").AddRange(path.BeadArea.Select(plan.FlowFactor), gp);
                    T("real").AddRange(path.RealisedHeight, gp);
                    T("width").AddRange(path.Width, gp);
                    T("ratio").AddRange(ov.Ratio, gp);
                    T("tilt").AddRange(ov.Tilt, gp);
                    T("lean").AddRange(ov.Lean, gp);
                    T("offset").AddRange(ov.Offset, gp);
                    T("area").AddRange(path.BeadArea, gp);
                    T("target").AddRange(path.TargetHeight, gp);
                }

                da.SetDataTree(Out("Layers"), curves);
                da.SetDataTree(Out("Flow Factor"), T("flow"));
                da.SetDataTree(Out("Realised Height"), T("real"));
                da.SetDataTree(Out("Width"), T("width"));
                da.SetDataTree(Out("Overhang Ratio"), T("ratio"));
                da.SetDataTree(Out("Layer Tilt"), T("tilt"));
                da.SetData(Out("Print"), plan);
                da.SetDataTree(Out("Wall Lean"), T("lean"));
                da.SetDataTree(Out("Bead Offset"), T("offset"));
                da.SetDataTree(Out("Bead Area"), T("area"));
                da.SetDataTree(Out("Target Height"), T("target"));
                da.SetDataList(Out("Layer Field"), plan.Field.Phi);

                SetPreview(da, plan, show, legend);
                da.SetData(Out("Info"), Info(plan));
                Warn(plan);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        internal static ShellDesign ToDesign(object obj, IList<double> thickness)
        {
            var design = RhinoConvert.Unwrap<ShellDesign>(obj);
            if (design == null)
            {
                var model = RhinoConvert.Unwrap<ShellModel>(obj)
                    ?? throw new ArgumentException("Design must be the Design output of Shell BESO or a Shell Model.");
                design = ShellDesign.FromModel(model);
            }
            if (thickness != null && thickness.Count > 0)
                design = new ShellDesign(design.Model, RhinoConvert.Expand(thickness, design.Model.ElementCount, "Thickness"),
                    design.Rule, design.Bond, design.Source + ", thickness replaced");
            return design;
        }

        private void SetPreview(IGH_DataAccess da, PrintPlan plan, int show, bool legend)
        {
            var mesh = plan.Design.Model.Mesh;
            double s = plan.Design.Model.UnitScale;
            switch (show)
            {
                case 1:
                {
                    var pct = plan.HeightError.Select(e => 100 * e).ToArray();
                    double w = Math.Max(1.0, pct.Where(e => !double.IsNaN(e)).Select(Math.Abs).DefaultIfEmpty(0).Max());
                    da.SetData(Out("Preview"), Preview.FaceColoured(mesh, pct, s, out var used, ReadRange(da, "Colour Range", (-w, w))));
                    SetLegend("Layer height error", "% (realised vs target)", used, legend);
                    break;
                }
                case 2:
                    da.SetData(Out("Preview"), Preview.FaceColoured(mesh, plan.Design.Thickness, s, out var usedT, ReadRange(da, "Colour Range", null)));
                    SetLegend("Wall thickness", "mm", usedT, legend);
                    break;
                case 3:
                {
                    var lean = mesh.FaceNormals.Select(n => Math.Asin(Math.Min(1, Math.Abs(n.Z))) * 180 / Math.PI).ToArray();
                    da.SetData(Out("Preview"), Preview.FaceColoured(mesh, lean, s, out var usedL, ReadRange(da, "Colour Range", (0.0, 90.0))));
                    SetLegend("Wall lean from vertical", "deg", usedL, legend);
                    break;
                }
                default:
                    da.SetData(Out("Preview"), Preview.VertexColoured(mesh, plan.VertexHeight, s, out var usedH,
                        ReadRange(da, "Colour Range", (plan.Rule.HMin, plan.Rule.HMax))));
                    SetLegend("Layer height", "mm", usedH, legend);
                    break;
            }
        }

        private static string Info(PrintPlan plan)
        {
            var flows = plan.Paths.SelectMany(p => p.BeadArea).Where(a => a > 0).Select(plan.FlowFactor).ToArray();
            var err = plan.HeightError.Where(e => !double.IsNaN(e)).Select(Math.Abs).DefaultIfEmpty(0).ToArray();
            var leans = plan.Overhangs.SelectMany(o => o.Lean).Where(x => !double.IsNaN(x)).DefaultIfEmpty(0);
            var tilts = plan.Overhangs.SelectMany(o => o.Tilt).DefaultIfEmpty(0);
            var ratios = plan.Overhangs.SelectMany(o => o.Ratio).Where(x => !double.IsNaN(x)).DefaultIfEmpty(0);
            var st = plan.Settings;
            return
                $"{plan.Design}\n" +
                $"{st.Method} layers: {plan.Field.LayerCount} layers, {plan.Paths.Count} paths ({plan.OpenPaths} open)\n" +
                $"{plan.Rule}\n" +
                $"Layer height error: mean {err.Average():P1}, max {err.Max():P1}\n" +
                (flows.Length > 0 ? $"Flow factor {flows.Min():F2}-{flows.Max():F2} (nominal bead {plan.NominalArea:F2} mm²)\n" : "") +
                $"Printability: max wall lean {leans.Max():F1}° ({plan.LeanFlags} points > {st.MaxOverhang:G3}°), " +
                $"max tilt {tilts.Max():F1}° ({plan.TiltFlags} > {st.MaxTilt:G3}°), " +
                $"max overhang ratio {ratios.Max():F2} ({plan.RatioFlags} > 0.5)" +
                (plan.Flow != null ? "\nForce flow computed from the design (all loads)." : "");
        }

        private void Warn(PrintPlan plan)
        {
            var st = plan.Settings;
            if (plan.OpenPaths > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{plan.OpenPaths} open (partial) layer paths.");
            if (plan.LeanFlags > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{plan.LeanFlags} of {plan.PointCount} path points lean more than {st.MaxOverhang:G3}° from vertical. " +
                    "Lower layers there, or try the Force-aligned method.");
            if (plan.RatioFlags > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{plan.RatioFlags} path points have a bead more than half past the one below (Overhang Ratio > 0.5).");
            if (plan.TiltFlags > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{plan.TiltFlags} path points tilt more than {st.MaxTilt:G3}° along the path: a nozzle that cannot tilt may scrape.");
            var err = plan.HeightError.Where(e => !double.IsNaN(e)).Select(Math.Abs).ToArray();
            if (err.Length > 0 && err.Average() > 0.15)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Realised layer heights differ from the targets by >15% on average (the targets can't all fit a flat rim). " +
                    "The Flow Factor still gives the right wall, but check Realised Height, or try Eikonal or more smoothing.");
        }
    }
}
