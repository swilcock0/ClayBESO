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
            p[1].Optional = true;
            p[3].Optional = true;
            p[4].Optional = true;
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

            var model = RhinoConvert.Unwrap<ShellModel>(modelObj);
            if (model == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a Shell Model."); return; }

            try
            {
                var t = thick.Count > 0 ? RhinoConvert.Expand(thick, model.ElementCount, "Thickness") : model.Thickness.ToList();
                if (t.Any(x => x <= 0)) throw new ArgumentException("Thickness must be positive (use the BESO void-mode outputs as a hide mask instead).");
                var scaling = ElementScaling.FromThickness(t);
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
                da.SetData(6,
                    $"Equations: {solver.EquationCount}\nCompliance: {r.Compliance:G5} N·mm\n" +
                    $"Max displacement: {r.MaxDisplacement:G4} mm\nMax von Mises: {vm.Max():G4} MPa");
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }
    }
}
