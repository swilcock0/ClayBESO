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
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace ClayBeso.GH.Components
{
    public class ClayPrintComponent : LegendComponent
    {
        public ClayPrintComponent()
            : base("Clay Print", "Clay Print",
                "Everything in one component: give the shape of the wall and pick the printer.\n" +
                "It clamps the base to the bed, optimises the wall thickness for its own weight, makes the " +
                "non-planar layers, checks the finished piece and every print stage (strength and buckling) and " +
                "scores how printable it is (1-100). Change the shape and watch the Score and Report.\n" +
                "Time (0-1) plays the print back. For more control, use the separate components (Shell Model, " +
                "Shell BESO, Print Layers, Print Check, Print Playback).",
                Tab.Name, Tab.Start) { }

        public override Guid ComponentGuid => new Guid("8bc3566e-c810-4a16-b47c-4d1c4c621f39");
        protected override System.Drawing.Bitmap Icon => Icons.Get("ClayBeso");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override IEnumerable<Dropdown> Dropdowns => new[]
        {
            new Dropdown("Printer", 0, PrinterProfile.Presets.Select((p, i) => (p.Name, i)).ToArray()),
        };

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGeometryParameter("Shape", "Shape", "The wall's mid-surface: a mesh, surface or polysurface (open at the top, no floor), " +
                "standing on the bed (lowest point = bed). Surfaces are meshed automatically.", GH_ParamAccess.item);
            p.AddIntegerParameter("Printer", "Printer", "Printer and nozzle (a dropdown appears when you place the component). Sets wall thickness, " +
                "layer height, overhang and tilt limits and speed.", GH_ParamAccess.item, 0);
            p.AddBooleanParameter("Optimise", "Optimise", "Optimise the wall thickness (same clay, stiffer). False = uniform wall, faster while you shape.", GH_ParamAccess.item, true);
            p.AddBooleanParameter("Fix Overhang", "Fix Overhang", "First pull overhangs back to the printer's max lean (changes the shape; see the Shape output and Fix Overhang component).", GH_ParamAccess.item, false);
            p.AddNumberParameter("Time", "Time", "Print playback, 0 = start, 1 = finished. Use a 0-1 slider; right-click > Animate for a video.", GH_ParamAccess.item, 1.0);
            p.AddGenericParameter("Material", "Material", "Optional: a Material component. Default: green clay (E = 1 MPa, 1900 kg/m³, strength 0.03 MPa placeholder).", GH_ParamAccess.item);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddIntegerParameter("Score", "Score", "Printability 1-100. Use it to compare designs; the Report says what limits it.", GH_ParamAccess.item);
            p.AddTextParameter("Report", "Report", "Each aspect of the score, weakest first, with what to change.", GH_ParamAccess.item);
            p.AddMeshParameter("Preview", "Preview", "The beads printed up to Time (document units), coloured by wall thickness (bead width).", GH_ParamAccess.item);
            p.AddCurveParameter("Layers", "Layers", "Layer toolpaths, one branch per layer (document units).", GH_ParamAccess.tree);
            p.AddNumberParameter("Flow Factor", "Flow Factor", "Per toolpath point: flow multiplier (1 = the median bead) that gives the wall its thickness.", GH_ParamAccess.tree);
            p.AddNumberParameter("Print Time", "Print Time", "Printing time [min] at the printer's speed (no travel or pauses).", GH_ParamAccess.item);
            p.AddTextParameter("Info", "Info", "What was done: mesh, printer, optimisation, layers, time.", GH_ParamAccess.item);
            p.AddMeshParameter("Shape", "Shape", "The mid-surface that was analysed (document units): the input, or the corrected shape with Fix Overhang on.", GH_ParamAccess.item);
            p.AddGenericParameter("Design", "Design", "For advanced use: the design (connect to Print Layers or Shell Analysis).", GH_ParamAccess.item);
            p.AddGenericParameter("Print", "Print", "For advanced use: the print (connect to Print Check or Print Playback).", GH_ParamAccess.item);
            p.AddGenericParameter("Check", "Check", "For advanced use: the check result (connect to Print Playback).", GH_ParamAccess.item);
        }

        // Moving only the Time slider must not re-run the analysis.
        private ClayPrintResult _result;
        private string _key;
        private double _seconds;
        private int _faces;

        protected override void SolveInstance(IGH_DataAccess da)
        {
            IGH_GeometricGoo shape = null;
            object matObj = null;
            int printerIndex = 0;
            bool optimise = true, fixOverhang = false;
            double time = 1;
            if (!da.GetData(In("Shape"), ref shape) || shape == null) return;
            da.GetData(In("Printer"), ref printerIndex);
            da.GetData(In("Optimise"), ref optimise);
            da.GetData(In("Fix Overhang"), ref fixOverhang);
            da.GetData(In("Time"), ref time);
            da.GetData(In("Material"), ref matObj);

            try
            {
                if (printerIndex < 0 || printerIndex >= PrinterProfile.Presets.Count)
                    throw new ArgumentException($"Printer must be 0-{PrinterProfile.Presets.Count - 1}.");
                var printer = PrinterProfile.Presets[printerIndex];
                var material = RhinoConvert.Unwrap<ClayMaterial>(matObj) ?? new ClayMaterial(1.0, 0.35, 1900, 0.03);
                double s = RhinoConvert.DocToMm();
                var mesh = RhinoConvert.ToTriMesh(ToMesh(shape, s), s);

                string key = Key(mesh, printerIndex, optimise, material) + "/" + fixOverhang;
                if (_result == null || key != _key)
                {
                    _result = null;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = ClayPrint.Run(mesh, material, printer, new ClayPrintOptions { Optimise = optimise, FixOverhang = fixOverhang, BaseTolerance = 0.5 * s });
                    r.Model.UnitScale = s;
                    _seconds = sw.Elapsed.TotalSeconds;
                    _faces = mesh.FaceCount;
                    _result = r;
                    _key = key;
                }
                var res = _result;

                da.SetData(Out("Score"), res.Score.Total);
                da.SetData(Out("Report"), res.Score.Report());

                var tl = res.Timeline;
                var range = (printer.MinWall, printer.MaxWall);
                var beads = tl.Beads(time, (i, k) => tl.Paths[i].Width[k], 3);
                da.SetData(Out("Preview"), Preview.ValueColoured(beads, s, range));
                SetLegend("Wall thickness (bead width)", "mm", range, true);

                var curves = new DataTree<Curve>();
                var flow = new DataTree<double>();
                foreach (var path in res.Plan.Paths)
                {
                    var gp = new GH_Path(path.Layer - 1);
                    var pts = path.Points.Select(q => RhinoConvert.ToPoint(q, s)).ToList();
                    if (path.Closed) pts.Add(pts[0]);
                    curves.Add(new PolylineCurve(pts), gp);
                    flow.AddRange(path.BeadArea.Select(res.Plan.FlowFactor), gp);
                }
                da.SetDataTree(Out("Layers"), curves);
                da.SetDataTree(Out("Flow Factor"), flow);
                da.SetData(Out("Print Time"), tl.TotalTime / 60);
                da.SetData(Out("Info"), Info(res, time));
                da.SetData(Out("Shape"), RhinoConvert.ToRhino(res.Shape, s));
                da.SetData(Out("Design"), res.Design);
                da.SetData(Out("Print"), res.Plan);
                da.SetData(Out("Check"), res.Check);

                if (res.Score.Failing.Any())
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Score {res.Score.Total}: fails on {string.Join(", ", res.Score.Failing.Select(i => i.Name))}. See Report.");
                if (_faces > 15000)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{_faces} faces: slow to update. A coarser mesh is faster while you shape.");
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        /// <summary>A mesh of the shape. Surfaces get roughly 5000 even triangles: fine enough, still interactive.</summary>
        private static Mesh ToMesh(IGH_GeometricGoo shape, double docToMm)
        {
            Mesh mesh = null;
            if (GH_Convert.ToMesh(shape, ref mesh, GH_Conversion.Primary) && mesh != null) return mesh;
            Brep brep = null;
            if (!GH_Convert.ToBrep(shape, ref brep, GH_Conversion.Both) || brep == null)
                throw new ArgumentException("Shape must be a mesh, surface or polysurface.");
            double area = AreaMassProperties.Compute(brep)?.Area ?? 0;
            if (area <= 0) throw new ArgumentException("The surface has no area.");
            double edge = Math.Sqrt(area / (0.433 * 5000));   // equilateral triangles: area = 0.433 edge²
            var mp = MeshingParameters.QualityRenderMesh;
            mp.MaximumEdgeLength = edge;
            mp.MinimumEdgeLength = 0.5 * edge;
            mp.GridAspectRatio = 1;
            mp.SimplePlanes = false;
            var result = new Mesh();
            foreach (var m in Mesh.CreateFromBrep(brep, mp) ?? new Mesh[0]) result.Append(m);
            if (result.Faces.Count == 0) throw new ArgumentException("Could not mesh the surface.");
            return result;
        }

        private static string Key(TriMesh mesh, int printer, bool optimise, ClayMaterial m)
        {
            double h = 0;
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var p = mesh.Vertices[i];
                h += (i % 7 + 1) * p.X + (i % 11 + 1) * p.Y + (i % 13 + 1) * p.Z;
            }
            return $"{mesh.VertexCount}/{mesh.FaceCount}/{h:R}/{printer}/{optimise}/{m.E:R}/{m.Nu:R}/{m.Density:R}/{m.Strength:R}";
        }

        private string Info(ClayPrintResult r, double time)
        {
            var now = r.Timeline.At(time);
            var t = r.Design.Thickness;
            return
                $"{r.Printer}\n" +
                $"Mesh: {_faces} faces; computed in {_seconds:F1} s\n" +
                (r.Fix == null ? ""
                    : r.Fix.MovedCount > 0
                        ? $"Fix Overhang: {r.Fix.MovedCount} points moved, at most {r.Fix.MaxMove:F1} mm\n"
                        : "Fix Overhang: nothing to fix\n") +
                (r.Iterations > 0
                    ? $"Wall {t.Min():F1}-{t.Max():F1} mm after {r.Iterations} iterations{(r.Converged ? "" : " (not converged)")}: " +
                      $"{1 / r.ComplianceRatio - 1:P0} stiffer than a uniform {r.Printer.StartWall:G3} mm wall with the same clay\n"
                    : $"Uniform wall {r.Printer.StartWall:G3} mm (Optimise off)\n") +
                $"{r.Plan.Field.LayerCount} layers, {r.Timeline.TotalLength / 1000:F1} m of bead, " +
                $"{r.Timeline.TotalTime / 60:F0} min at {r.Printer.Speed:G3} mm/s\n" +
                $"Playback: layer {now.Layer}, {now.Time / 60:F1} min";
        }
    }
}
