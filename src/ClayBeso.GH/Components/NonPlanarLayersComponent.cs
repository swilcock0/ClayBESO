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
                "Slice the shell into non-planar layer toolpaths whose spacing follows the target layer heights.\n" +
                "Method 0 = Conforming: layers fit both the base and the top rim, so every layer is a closed loop " +
                "(heights matched as closely as possible). Check the error output.\n" +
                "Method 1 = Eikonal: exact local layer heights grown from the base. The top may end in partial layers.",
                Tab.Name, Tab.Print) { }

        public override Guid ComponentGuid => new Guid("f3c6b8d7-a609-4053-9eb6-5b7a2c4f9d06");
        protected override System.Drawing.Bitmap Icon => Icons.Get("NonPlanarLayers");

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell model (for the mesh).", GH_ParamAccess.item);
            p.AddNumberParameter("Layer Heights", "h", "Target layer height per vertex [mm] (from Layer Heights).", GH_ParamAccess.list);
            p.AddNumberParameter("Vertex Thickness", "tv", "Optional bead width per vertex [mm], passed through per point.", GH_ParamAccess.list);
            p.AddIntegerParameter("Method", "Mth", "0 = Conforming (closed layers, fits base and rim), 1 = Eikonal (exact heights).", GH_ParamAccess.item, 0);
            p.AddBooleanParameter("Vertical", "Vz", "Measure layer height vertically (Z) rather than along the wall.", GH_ParamAccess.item, true);
            p.AddNumberParameter("Tolerance", "Tol", "Band (document units) at the bottom/top used to find the base and the rim.", GH_ParamAccess.item, 0.5);
            p.AddVectorParameter("Seam", "Seam", "Closed layers start at the point furthest in this direction.", GH_ParamAccess.item, new Vector3d(1, 0, 0));
            p.AddNumberParameter("Nominal Area", "A0", "Bead cross-section [mm²] that corresponds to 100% flow. 0 = median of the paths.", GH_ParamAccess.item, 0.0);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the height-error colour scale [%], e.g. -20 to 20. Empty = symmetric about 0.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
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

            var model = RhinoConvert.Unwrap<ShellModel>(modelObj);
            if (model == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a Shell Model."); return; }

            try
            {
                var mesh = model.Mesh;
                double s = model.UnitScale;
                var hv = RhinoConvert.Expand(h, mesh.VertexCount, "Layer Heights");
                var thick = tv.Count > 0 ? RhinoConvert.Expand(tv, mesh.VertexCount, "Vertex Thickness") : null;

                var (bottom, top) = LayerField.ExtremeVertices(mesh, Vec3.UnitZ, tol * s);
                var m = method == 1 ? LayerMethod.Eikonal : LayerMethod.Conforming;
                var field = LayerField.Compute(mesh, hv, bottom, top, Vec3.UnitZ, vertical, m);
                var paths = LayerSlicer.Slice(field, thick, Vec3.UnitZ, RhinoConvert.ToVec(seam));

                var curves = new DataTree<Curve>();
                var th = new DataTree<double>();
                var rh = new DataTree<double>();
                var wd = new DataTree<double>();
                var ba = new DataTree<double>();
                var ff = new DataTree<double>();
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
                string flow = allAreas.Length > 0
                    ? $"\nNominal bead area {a0:F2} mm², flow factor {allAreas[0] / a0:F2}-{allAreas[allAreas.Length - 1] / a0:F2}"
                    : "\n(connect Vertex Thickness to get bead area and flow)";
                da.SetData(7,
                    $"{m} method, {field.LayerCount} layers, {paths.Count} paths ({open} open)\n" +
                    $"Base vertices: {field.BaseVertices.Length}, rim vertices: {field.TopVertices.Length}\n" +
                    $"Layer height error: mean {finite.DefaultIfEmpty(0).Average():P1}, max {finite.DefaultIfEmpty(0).Max():P1}" + flow);

                if (open > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{open} open (partial) layer paths.");
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
