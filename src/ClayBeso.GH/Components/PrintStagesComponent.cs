using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.GH.Common;
using Grasshopper.Kernel;

namespace ClayBeso.GH.Components
{
    public class PrintStagesComponent : LegendComponent
    {
        public PrintStagesComponent()
            : base("Print Stages", "Stages",
                "Analyse the shell while it is being printed: at each stage only the layers printed so far (from the " +
                "Layer Field of Non-planar Layers) exist, loaded by their own weight. Overhangs and soft clay usually fail " +
                "part-way up, which an analysis of the finished piece can't show.\n" +
                "With a clay strength, each stage also gets the interlayer check, and the worst stage is the one with the " +
                "highest interlayer utilisation; without one, the highest von Mises stress. Clay stiffness is taken as the " +
                "same at every height (no ageing yet).",
                Tab.Name, Tab.Analyse) { }

        public override Guid ComponentGuid => new Guid("cb1e458c-578c-4b01-a5ff-7a19f038b41a");
        protected override System.Drawing.Bitmap Icon => Icons.Get("PrintStages");
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell model.", GH_ParamAccess.item);
            p.AddNumberParameter("Thickness", "t", "Wall thickness per face [mm] (e.g. from Shell BESO). Default: the model's thickness.", GH_ParamAccess.list);
            p.AddNumberParameter("Layer Field", "φ", "Layer field per vertex, from the Layer Field output of Non-planar Layers.", GH_ParamAccess.list);
            p.AddIntegerParameter("Stages", "N", "Number of stages, evenly spaced in layers up to the last one.", GH_ParamAccess.item, 10);
            p.AddIntegerParameter("Show Stage", "Show", "Stage shown in the preview: 0 = first ... N-1 = finished piece; -1 = the worst stage.", GH_ParamAccess.item, -1);
            p.AddBooleanParameter("Vertical", "Vz", "Same setting as on Non-planar Layers (layer height measured in Z).", GH_ParamAccess.item, true);
            p.AddNumberParameter("Strength", "f", "Clay strength [MPa] for the interlayer check at each stage. 0 = use the Clay Material strength; if that is 0 too, the check is skipped.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Bond Efficiency", "η", "Interface efficiency 0-1 (see Interlayer Check).", GH_ParamAccess.item, 1.0);
            p.AddBooleanParameter("Orthotropic", "Orth", "Treat the wall as weaker across its layers (stiffness × η·c from the realised layer heights).", GH_ParamAccess.item, true);
            p.AddBooleanParameter("Point Loads", "PL", "Also apply the model's point loads at every stage (normally they act only on the finished piece).", GH_ParamAccess.item, false);
            p.AddNumberParameter("Deform Scale", "DS", "Displacement exaggeration for the preview.", GH_ParamAccess.item, 10.0);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the preview colour scale. Empty = 0 to the maximum over all stages (the same scale for every stage).", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            p[1].Optional = true;
            for (int i = 3; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddIntegerParameter("Stage Layer", "L", "Per stage: printed up to this layer.", GH_ParamAccess.list);
            p.AddNumberParameter("Stage Height", "z", "Per stage: height of the highest printed point (document units).", GH_ParamAccess.list);
            p.AddNumberParameter("Max Displacement", "dMax", "Per stage: largest displacement [mm].", GH_ParamAccess.list);
            p.AddNumberParameter("Max Von Mises", "σMax", "Per stage: largest von Mises stress [MPa].", GH_ParamAccess.list);
            p.AddNumberParameter("Max Utilisation", "Umax", "Per stage: largest interlayer utilisation (empty without a strength).", GH_ParamAccess.list);
            p.AddIntegerParameter("Worst Stage", "Worst", "Index of the most critical stage.", GH_ParamAccess.item);
            p.AddMeshParameter("Preview", "P", "The shown stage, deformed and coloured by interlayer utilisation (or von Mises without a strength).", GH_ParamAccess.item);
            p.AddTextParameter("Info", "I", "Summary.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object modelObj = null;
            var thick = new List<double>();
            var phi = new List<double>();
            int count = 10, show = -1;
            bool vertical = true, orthotropic = true, pointLoads = false, legend = true;
            double strength = 0, eta = 1.0, ds = 10;
            if (!da.GetData(0, ref modelObj)) return;
            da.GetDataList(1, thick);
            if (!da.GetDataList(2, phi)) return;
            da.GetData(3, ref count);
            da.GetData(4, ref show);
            da.GetData(5, ref vertical);
            da.GetData(6, ref strength);
            da.GetData(7, ref eta);
            da.GetData(8, ref orthotropic);
            da.GetData(9, ref pointLoads);
            da.GetData(10, ref ds);
            da.GetData(12, ref legend);

            var model = RhinoConvert.Unwrap<ShellModel>(modelObj);
            if (model == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a Shell Model."); return; }

            try
            {
                if (strength <= 0) strength = model.Material.Strength;
                var t = thick.Count > 0 ? RhinoConvert.Expand(thick, model.ElementCount, "Thickness") : model.Thickness.ToList();
                if (t.Any(x => x <= 0)) throw new ArgumentException("Thickness must be positive.");
                if (phi.Count != model.NodeCount)
                    throw new ArgumentException($"Layer Field must have one value per mesh vertex ({model.NodeCount}), got {phi.Count}.");

                var options = new PrintStageOptions
                {
                    Vertical = vertical,
                    IncludePointLoads = pointLoads,
                    Strength = Math.Max(0, strength),
                    Bond = new BondModel(Math.Max(1e-6, Math.Min(1, eta))),
                    Orthotropic = orthotropic,
                };
                var layers = PrintStages.EvenStages(phi, Math.Max(1, count));
                var stages = PrintStages.Run(model, t, phi, layers, options);
                var done = Enumerable.Range(0, stages.Count).Where(i => stages[i].Skipped == null).ToArray();
                if (done.Length == 0) throw new ArgumentException("No stage could be analysed: " + stages[0].Skipped);

                bool useU = options.Strength > 0;
                int worst = useU
                    ? done.OrderByDescending(i => stages[i].MaxUtilisation).First()
                    : done.OrderByDescending(i => stages[i].MaxVonMises).First();
                int shown = show < 0 || show >= stages.Count || stages[show].Skipped != null ? worst : show;
                var st = stages[shown];

                double s = model.UnitScale;
                double overallMax = done.Max(i => useU ? stages[i].MaxUtilisation : stages[i].MaxVonMises);
                var range = ReadRange(da, 11, (0.0, Math.Max(useU ? 1.0 : 1e-12, overallMax)));
                var values = useU ? st.Utilisation : st.VonMises;
                var hide = values.Select(double.IsNaN).ToArray();
                // Faces without a layer direction (e.g. the base ring) have no utilisation but are printed: show them grey.
                if (useU) hide = st.VonMises.Select(double.IsNaN).ToArray();

                da.SetDataList(0, stages.Select(x => x.Layer));
                da.SetDataList(1, stages.Select(x => x.TopZ / s));
                da.SetDataList(2, stages.Select(x => x.MaxDisplacement));
                da.SetDataList(3, stages.Select(x => x.MaxVonMises));
                if (useU) da.SetDataList(4, stages.Select(x => x.MaxUtilisation));
                da.SetData(5, worst);
                da.SetData(6, Preview.FaceColoured(model.Mesh, values, s, out var used, range,
                    v => st.Displacement[v], ds, hide));
                SetLegend(useU ? $"Interlayer utilisation, stage {shown}" : $"Von Mises, stage {shown}", useU ? "-" : "MPa", used, legend);

                var w = stages[worst];
                da.SetData(7,
                    $"{stages.Count} stages, layers {layers.First()}-{layers.Last()}\n" +
                    $"Worst: stage {worst} (layer {w.Layer}, {w.TopZ / s:G4} units high): " +
                    (useU ? $"interlayer utilisation {w.MaxUtilisation:G3}, " : "") +
                    $"von Mises {w.MaxVonMises:G3} MPa, displacement {w.MaxDisplacement:G3} mm\n" +
                    $"Finished piece: " + (useU ? $"utilisation {stages.Last().MaxUtilisation:G3}, " : "") +
                    $"von Mises {stages.Last().MaxVonMises:G3} MPa\n" +
                    $"Showing stage {shown}" + (useU ? "" : "\n(no strength: interlayer check skipped)"));

                if (useU && w.MaxUtilisation > 1)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Interlayer utilisation exceeds 1 during printing (stage {worst}, layer {w.Layer}).");
                if (worst != stages.Count - 1)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        $"The most critical moment is during printing (stage {worst}), not the finished piece.");
                foreach (var x in stages.Where(x => x.Skipped != null))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Stage at layer {x.Layer} skipped: {x.Skipped}");
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }
    }
}
