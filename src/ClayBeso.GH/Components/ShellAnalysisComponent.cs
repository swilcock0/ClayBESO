using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.GH.Common;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace ClayBeso.GH.Components
{
    public class ShellAnalysisComponent : LegendComponent
    {
        public ShellAnalysisComponent()
            : base("Shell Analysis", "Analyse",
                "Linear static analysis of a shell: stresses, displacements and force flow, and optionally linear " +
                "buckling (how many times the loads the shell carries before it buckles).\n" +
                "Connect a Shell Model to check the starting shell, or the Design output of Shell BESO to check the " +
                "optimised one (its thickness, Layer Rule and bond come with it).",
                Tab.Name, Tab.Analyse) { }

        /// <summary>For the legacy version, which only differs in the order of its inputs and outputs.</summary>
        protected ShellAnalysisComponent(string name, string nickname, string description)
            : base(name, nickname, description, Tab.Name, Tab.Analyse) { }

        public override Guid ComponentGuid => new Guid("e5fee71a-9070-45a7-b1fe-8cb8b4a16cde");
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override IEnumerable<Dropdown> Dropdowns => new[]
        {
            new Dropdown("Show", 0, ("Von Mises stress", 0), ("Displacement", 1), ("Buckling mode", 2)),
        };
        protected override System.Drawing.Bitmap Icon => Icons.Get("ShellAnalysis");

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell Model (starting shell) or the Design output of Shell BESO (optimised shell).", GH_ParamAccess.item);
            p.AddIntegerParameter("Show", "Show", "Preview colours: 0 = von Mises stress, 1 = displacement, 2 = buckling mode (turns Buckling on) (a dropdown appears when you place the component).", GH_ParamAccess.item, 0);
            p.AddNumberParameter("Thickness", "Thickness", "Optional thickness per face [mm], replacing the model's or design's.", GH_ParamAccess.list);
            p.AddGenericParameter("Layer Rule", "Layer Rule", "Optional Layer Rule: analyse the wall as weaker across its layers (κ = η·(t − h)/t). Default: the design's rule, if any.", GH_ParamAccess.item);
            p.AddNumberParameter("Bond Efficiency", "η", "With a Layer Rule: interface efficiency 0-1. 0 = the design's, or 1.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Deform Scale", "DS", "Displacement exaggeration for the preview mesh.", GH_ParamAccess.item, 10.0);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the colour scale (MPa for stress, mm for displacement). Empty = min-max of the result.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            p.AddBooleanParameter("Buckling", "Buckling", "Also run a linear buckling analysis (takes longer; off by default so a running BESO isn't slowed down).", GH_ParamAccess.item, false);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddMeshParameter("Preview", "Preview", "Deformed mesh coloured by the Show option.", GH_ParamAccess.item);
            p.AddTextParameter("Info", "Info", "Summary: peak stress and where, displacement, layers.", GH_ParamAccess.item);
            p.AddNumberParameter("Max Displacement", "Max Displacement", "Largest displacement [mm].", GH_ParamAccess.item);
            p.AddNumberParameter("Von Mises", "Von Mises", "Von Mises stress per face, worst of the two surfaces [MPa].", GH_ParamAccess.list);
            p.AddVectorParameter("Displacements", "d", "Displacement per vertex [mm].", GH_ParamAccess.list);
            p.AddNumberParameter("Utilisation", "U", "σ / strength per face (Strength on the Material component).", GH_ParamAccess.list);
            p.AddNumberParameter("Compliance", "C", "Compliance F·u [N·mm]. Lower = stiffer.", GH_ParamAccess.item);
            p.AddPointParameter("Max Stress Point", "σMax", "Centre of the face with the highest von Mises stress.", GH_ParamAccess.item);
            p.AddVectorParameter("Force Flow", "FF", "Per face: direction of the dominant principal in-plane force; length 0-1 = how directional it is.", GH_ParamAccess.list);
            p.AddLineParameter("Flow Lines", "FL", "A short line per face along the force flow, for viewing force-flow trajectories.", GH_ParamAccess.list);
            p.AddNumberParameter("Buckling Factor", "Buckling Factor", "With Buckling on: lowest buckling load factor [-]. The loads (self-weight and point loads) times this make a perfect shell buckle; < 1 = buckles. Infinity = nothing in compression.", GH_ParamAccess.item);
            p.AddVectorParameter("Buckling Mode", "φ", "With Buckling on: buckling shape per vertex, scaled so the largest movement is 1 (no units).", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object modelObj = null, ruleObj = null;
            var thick = new List<double>();
            double ds = 10, eta = 0;
            int show = 0;
            bool legend = true;
            if (!da.GetData(In("Model"), ref modelObj)) return;
            da.GetDataList(In("Thickness"), thick);
            da.GetData(In("Deform Scale"), ref ds);
            da.GetData(In("Legend"), ref legend);
            da.GetData(In("Layer Rule"), ref ruleObj);
            da.GetData(In("Bond Efficiency"), ref eta);
            da.GetData(In("Show"), ref show);
            bool buckle = false;
            da.GetData(In("Buckling"), ref buckle);
            if (show == 2) buckle = true;
            var range = ReadRange(da, "Colour Range", null);

            try
            {
                var design = PrintLayersComponent.ToDesign(modelObj, thick);
                var model = design.Model;
                var t = design.Thickness;
                if (t.Any(x => x <= 0)) throw new ArgumentException("Thickness must be positive (void-mode designs have removed faces).");

                var scaling = ElementScaling.FromThickness(t);
                string layers = "";
                var rule = RhinoConvert.Unwrap<Printing.LayerRule>(ruleObj) ?? design.Rule;
                if (rule != null)
                {
                    model.EnableLayerAnisotropy(Geometry.Vec3.UnitZ);
                    var resolved = rule.Resolve(t);
                    var bond = eta > 0 ? new BondModel(Math.Min(1, eta)) : design.Bond ?? new BondModel();
                    for (int e = 0; e < t.Length; e++) scaling.Across[e] = bond.Efficiency * bond.Contact(t[e], resolved.Height(t[e]));
                    layers = $"\nPrinted layers: {resolved}, η = {bond.Efficiency:G3}, κ {scaling.Across.Min():F2}-{scaling.Across.Max():F2}";
                }

                var solver = new ShellSolver(model);
                var buckling = buckle ? Buckling.Compute(solver, scaling) : null;
                var r = buckling?.Analysis ?? solver.Solve(scaling);
                var mode = buckling != null && buckling.Modes.Length > 0 ? buckling.Modes[0] : null;
                var vm = solver.VonMises(r, scaling);
                double s = model.UnitScale;

                da.SetData(Out("Compliance"), r.Compliance);
                da.SetData(Out("Max Displacement"), r.MaxDisplacement);
                da.SetDataList(Out("Displacements"), Enumerable.Range(0, model.NodeCount).Select(i =>
                {
                    var d = r.Displacement(i);
                    return new Vector3d(d.X, d.Y, d.Z);
                }));
                da.SetDataList(Out("Von Mises"), vm);
                if (model.Material.Strength > 0)
                    da.SetDataList(Out("Utilisation"), vm.Select(x => x / model.Material.Strength));
                if (buckling != null)
                {
                    da.SetData(Out("Buckling Factor"), buckling.Critical);
                    if (mode != null) da.SetDataList(Out("Buckling Mode"), mode.Select(v => new Vector3d(v.X, v.Y, v.Z)));
                    BucklingText.Warn(this, buckling.Critical, "this shell");
                    if (!buckling.Converged)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "The buckling analysis did not fully converge; the factor is approximate.");
                }
                if (show == 2)
                {
                    double amp = BucklingText.Amplitude(model.Mesh);
                    da.SetData(Out("Preview"), Preview.FaceColoured(model.Mesh, BucklingText.FaceValues(model.Mesh, mode), s, out var usedB,
                        range ?? (0, 1), v => mode == null ? Geometry.Vec3.Zero : mode[v] * amp, ds));
                    SetLegend("Buckling mode", "relative", usedB, legend);
                }
                else if (show == 1)
                {
                    var dispFace = model.Mesh.Faces.Select(f => f.Average(v => r.Displacement(v).Length)).ToArray();
                    da.SetData(Out("Preview"), Preview.FaceColoured(model.Mesh, dispFace, s, out var usedD, range, r.Displacement, ds));
                    SetLegend("Displacement", "mm", usedD, legend);
                }
                else
                {
                    da.SetData(Out("Preview"), Preview.FaceColoured(model.Mesh, vm, s, out var used, range, r.Displacement, ds));
                    SetLegend("Von Mises stress", "MPa", used, legend);
                }

                int iMax = Array.IndexOf(vm, vm.Max());
                var (mem, bend) = solver.StressParts(r, scaling, iMax);
                var centre = model.Mesh.FaceCentroid(iMax);
                double zMin = model.Mesh.Vertices.Min(v => v.Z), zTop = model.Mesh.Vertices.Max(v => v.Z);
                var sorted = vm.OrderBy(x => x).ToArray();
                da.SetData(Out("Max Stress Point"), RhinoConvert.ToPoint(centre, s));
                var flow = ForceFlow.Compute(model, r, scaling);
                da.SetDataList(Out("Force Flow"), FlowConvert.ToVectors(flow));
                da.SetDataList(Out("Flow Lines"), FlowConvert.ToLines(model, flow));
                da.SetData(Out("Info"),
                    $"{design}\n" +
                    $"Max displacement: {r.MaxDisplacement:G4} mm, compliance {r.Compliance:G5} N·mm\n" +
                    $"Max von Mises: {vm.Max():G4} MPa at face {iMax}, {(centre.Z - zMin) / Math.Max(1e-9, zTop - zMin):P0} of the height, " +
                    $"wall {t[iMax]:G3} mm (membrane {mem:G3} / bending {bend:G3} MPa)\n" +
                    $"99th percentile: {sorted[(int)(0.99 * (sorted.Length - 1))]:G4} MPa, mean {vm.Average():G4} MPa" + layers +
                    (buckling != null
                        ? $"\nBuckling factor (perfect shape): {BucklingText.Factor(buckling.Critical)}; < 1 = buckles, treat < {BucklingText.Margin:G2} as at risk"
                        : ""));
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }
    }
}
