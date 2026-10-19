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
                "Linear static analysis of a shell model, optionally with a per-face thickness " +
                "(e.g. the BESO result).",
                Tab.Name, Tab.Analyse) { }

        public override Guid ComponentGuid => new Guid("c9f3e5a4-73d6-4d20-8b83-2e4d9f1c6a03");
        protected override System.Drawing.Bitmap Icon => Icons.Get("ShellAnalysis");

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell model.", GH_ParamAccess.item);
            p.AddNumberParameter("Thickness", "t", "Optional thickness per face [mm]. Default: the model's thickness.", GH_ParamAccess.list);
            p.AddNumberParameter("Deform Scale", "DS", "Displacement exaggeration for the preview mesh.", GH_ParamAccess.item, 10.0);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the stress colour scale [MPa]. Empty = min-max of the result.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            p.AddGenericParameter("Layer Rule", "Rule", "Optional Layer Rule: analyse the wall as weaker across its layers (κ = η·(t − h)/t), as Shell BESO does with the same rule.", GH_ParamAccess.item);
            p.AddNumberParameter("Bond Efficiency", "η", "With a Layer Rule: interface efficiency 0-1.", GH_ParamAccess.item, 1.0);
            p[1].Optional = true;
            p[3].Optional = true;
            p[4].Optional = true;
            p[5].Optional = true;
            p[6].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("Compliance", "C", "Compliance F·u [N·mm]. Lower = stiffer.", GH_ParamAccess.item);
            p.AddNumberParameter("Max Displacement", "dMax", "Largest displacement [mm].", GH_ParamAccess.item);
            p.AddVectorParameter("Displacements", "d", "Displacement per vertex [mm].", GH_ParamAccess.list);
            p.AddNumberParameter("Von Mises", "σ", "Von Mises stress per face, worst of the two surfaces [MPa].", GH_ParamAccess.list);
            p.AddNumberParameter("Utilisation", "U", "σ / strength per face (needs Strength on the material).", GH_ParamAccess.list);
            p.AddMeshParameter("Preview", "P", "Deformed mesh coloured by von Mises stress.", GH_ParamAccess.item);
            p.AddTextParameter("Info", "I", "Summary.", GH_ParamAccess.item);
            p.AddPointParameter("Max Stress Point", "σMax", "Centre of the face with the highest von Mises stress.", GH_ParamAccess.item);
            p.AddVectorParameter("Force Flow", "FF", "Per face: direction of the dominant principal in-plane force (the force-flow line direction); length 0-1 = how directional it is (0 = equal in all directions). Feed it to Non-planar Layers (Method 2).", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object modelObj = null;
            var thick = new List<double>();
            double ds = 10;
            if (!da.GetData(0, ref modelObj)) return;
            da.GetDataList(1, thick);
            da.GetData(2, ref ds);
            bool legend = true;
            da.GetData(4, ref legend);
            var range = ReadRange(da, 3, null);
            object ruleObj = null;
            double eta = 1.0;
            da.GetData(5, ref ruleObj);
            da.GetData(6, ref eta);
            var rule = RhinoConvert.Unwrap<Printing.LayerRule>(ruleObj);

            var model = RhinoConvert.Unwrap<ShellModel>(modelObj);
            if (model == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a Shell Model."); return; }

            try
            {
                var t = thick.Count > 0 ? RhinoConvert.Expand(thick, model.ElementCount, "Thickness") : model.Thickness.ToList();
                if (t.Any(x => x <= 0)) throw new ArgumentException("Thickness must be positive (use the BESO void-mode outputs as a hide mask instead).");
                var scaling = ElementScaling.FromThickness(t);
                string layers = "";
                if (rule != null)
                {
                    model.EnableLayerAnisotropy(Geometry.Vec3.UnitZ);
                    var resolved = rule.Resolve(t);
                    var bond = new BondModel(Math.Max(1e-6, Math.Min(1, eta)));
                    for (int e = 0; e < t.Count; e++) scaling.Across[e] = bond.Efficiency * bond.Contact(t[e], resolved.Height(t[e]));
                    layers = $"\nPrinted layers: {resolved}, η = {bond.Efficiency:G3}, κ {scaling.Across.Min():F2}-{scaling.Across.Max():F2}";
                    if (rule.ThicknessRange == null &&
                        (rule.Mapping == Printing.LayerMapping.Proportional || rule.Mapping == Printing.LayerMapping.LinearInverse))
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                            "The Layer Rule has no Thickness Range, so here it is taken from this thickness field, while Shell BESO " +
                            "uses its tMin..tMax. Set the Thickness Range on the Layer Rule so both use the same mapping.");
                }
                var solver = new ShellSolver(model);
                var r = solver.Solve(scaling);
                var vm = solver.VonMises(r, scaling);
                double s = model.UnitScale;

                da.SetData(0, r.Compliance);
                da.SetData(1, r.MaxDisplacement);
                da.SetDataList(2, Enumerable.Range(0, model.NodeCount).Select(i =>
                {
                    var d = r.Displacement(i);
                    return new Vector3d(d.X, d.Y, d.Z);
                }));
                da.SetDataList(3, vm);
                if (model.Material.Strength > 0)
                    da.SetDataList(4, vm.Select(x => x / model.Material.Strength));
                da.SetData(5, Preview.FaceColoured(model.Mesh, vm, s, out var used, range, r.Displacement, ds));
                SetLegend("Von Mises stress", "MPa", used, legend);
                int iMax = Array.IndexOf(vm, vm.Max());
                var (mem, bend) = solver.StressParts(r, scaling, iMax);
                var centre = model.Mesh.FaceCentroid(iMax);
                double zMin = model.Mesh.Vertices.Min(v => v.Z), zTop = model.Mesh.Vertices.Max(v => v.Z);
                var sorted = vm.OrderBy(x => x).ToArray();
                da.SetData(7, RhinoConvert.ToPoint(centre, s));
                da.SetDataList(8, FlowConvert.ToVectors(ForceFlow.Compute(model, r, scaling)));
                da.SetData(6,
                    $"Equations: {solver.EquationCount}\nCompliance: {r.Compliance:G5} N·mm\n" +
                    $"Max displacement: {r.MaxDisplacement:G4} mm\n" +
                    $"Max von Mises: {vm.Max():G4} MPa at face {iMax}, {(centre.Z - zMin) / Math.Max(1e-9, zTop - zMin):P0} of the height, " +
                    $"wall {t[iMax]:G3} mm (membrane {mem:G3} / bending {bend:G3} MPa)\n" +
                    $"99th percentile: {sorted[(int)(0.99 * (sorted.Length - 1))]:G4} MPa, mean {vm.Average():G4} MPa" + layers);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }
    }
}
