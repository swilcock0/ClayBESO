using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.GH.Common;
using ClayBeso.Printing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace ClayBeso.GH.Components
{
    public class PrintCheckComponent : LegendComponent
    {
        public PrintCheckComponent()
            : base("Print Check", "Check",
                "Structural check of a print, in one place: the finished piece under all loads, and the piece while it " +
                "is printed (stages loaded by their own weight). Layer interfaces carry force only through their bonded " +
                "strip c = (w - h)/w, and the wall is weaker across the layers.\n" +
                "Connect the Print output of Print Layers. Pick what to see with Show, and when with Stage.",
                Tab.Name, Tab.Analyse) { }

        public override Guid ComponentGuid => new Guid("4b64848e-a1b8-4f8f-83e5-375e37fdd92a");
        protected override System.Drawing.Bitmap Icon => Icons.Get("PrintCheck");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        private static readonly (string, int)[] ShowOptions =
        {
            ("Interlayer utilisation", 0),
            ("Interface angle", 1),
            ("Von Mises stress", 2),
            ("Displacement", 3),
            ("Layer contact", 4),
        };

        private static readonly (string, int)[] StageOptions =
        {
            ("Finished piece", -1),
            ("Worst print stage", -2),
        };

        protected override IEnumerable<Dropdown> Dropdowns => new[]
        {
            new Dropdown("Show", 0, ShowOptions),
            new Dropdown("Stage", -1, StageOptions),
        };

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Print", "Print", "Print output of Print Layers.", GH_ParamAccess.item);
            p.AddNumberParameter("Strength", "Strength", "Clay strength [MPa] for the interlayer utilisation. 0 = the Clay Material strength; if that is 0 too, utilisation isn't reported.", GH_ParamAccess.item, 0.0);
            p.AddIntegerParameter("Show", "Show", "0 = interlayer utilisation, 1 = interface angle, 2 = von Mises, 3 = displacement, 4 = layer contact (dropdown appears when placed).", GH_ParamAccess.item, 0);
            p.AddIntegerParameter("Stage", "Stage", "-1 = finished piece, -2 = worst print stage, 0..N-1 = that stage (dropdown appears when placed; use a slider for a specific stage).", GH_ParamAccess.item, -1);
            p.AddIntegerParameter("Stages", "Stages", "Print stages to check (0 = finished piece only).", GH_ParamAccess.item, 8);
            p.AddNumberParameter("Bond Efficiency", "η", "Interface efficiency 0-1 (calibrate with docs/VALIDATION.md section 4). 0 = the design's, or 1.", GH_ParamAccess.item, 0.0);
            p.AddBooleanParameter("Orthotropic", "Orth", "Treat the wall as weaker across its layers (stiffness × η·c from the printed layer heights).", GH_ParamAccess.item, true);
            p.AddBooleanParameter("Stage Point Loads", "SPL", "Also apply the point loads during printing (they always act on the finished piece).", GH_ParamAccess.item, false);
            p.AddNumberParameter("Deform Scale", "DS", "Displacement exaggeration for the preview.", GH_ParamAccess.item, 10.0);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the preview colour scale. Empty = automatic for what is shown.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("Max Utilisation", "Max Utilisation", "Highest interlayer utilisation, finished piece or any stage (> 1 = expected to fail).", GH_ParamAccess.item);
            p.AddMeshParameter("Preview", "Preview", "The shown stage, deformed and coloured by the Show option.", GH_ParamAccess.item);
            p.AddTextParameter("Info", "Info", "Summary.", GH_ParamAccess.item);
            p.AddIntegerParameter("Worst Stage", "Worst Stage", "Index of the most critical print stage (-1 if none).", GH_ParamAccess.item);
            p.AddPointParameter("Worst Point", "Worst Point", "Where the highest value of the shown quantity is.", GH_ParamAccess.item);
            p.AddNumberParameter("Values", "V", "Per face: the shown quantity for the shown stage (NaN where not printed or undefined).", GH_ParamAccess.list);
            p.AddNumberParameter("Stage Height", "z", "Per stage: height of the highest printed point (document units).", GH_ParamAccess.list);
            p.AddNumberParameter("Stage Utilisation", "Us", "Per stage: highest interlayer utilisation (von Mises [MPa] without a strength).", GH_ParamAccess.list);
            p.AddVectorParameter("Force Flow", "FF", "Per face, finished piece: dominant principal force direction; length 0-1 = how directional.", GH_ParamAccess.list);
            p.AddLineParameter("Flow Lines", "FL", "Finished piece: a short line per face along the force flow, for viewing.", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object printObj = null;
            double strength = 0, eta = 0, ds = 10;
            bool orth = true, stagePl = false, legend = true;
            int stages = 8, show = 0, stage = -1;
            if (!da.GetData(In("Print"), ref printObj)) return;
            da.GetData(In("Strength"), ref strength);
            da.GetData(In("Bond Efficiency"), ref eta);
            da.GetData(In("Orthotropic"), ref orth);
            da.GetData(In("Stages"), ref stages);
            da.GetData(In("Stage Point Loads"), ref stagePl);
            da.GetData(In("Show"), ref show);
            da.GetData(In("Stage"), ref stage);
            da.GetData(In("Deform Scale"), ref ds);
            da.GetData(In("Legend"), ref legend);

            var plan = RhinoConvert.Unwrap<PrintPlan>(printObj);
            if (plan == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a Print (from Print Layers)."); return; }

            try
            {
                var model = plan.Design.Model;
                if (strength <= 0) strength = model.Material.Strength;
                var check = PrintChecker.Check(plan, new PrintCheckSettings
                {
                    Strength = Math.Max(0, strength),
                    Bond = eta > 0 ? new BondModel(Math.Min(1, eta)) : null,
                    Orthotropic = orth,
                    Stages = Math.Max(0, stages),
                    StagePointLoads = stagePl,
                });

                int shown = stage == -2 ? check.WorstStage : stage;
                if (shown >= check.Stages.Count || (shown >= 0 && check.Stages[shown].Skipped != null)) shown = -1;
                var values = Values(check, plan, show, shown, out string title, out string unit, out bool fromFinished);
                double s = model.UnitScale;

                var finite = values.Where(v => !double.IsNaN(v)).ToArray();
                (double, double)? auto = show == 0 ? (0.0, Math.Max(1.0, finite.DefaultIfEmpty(0).Max()))
                    : show == 1 ? (0.0, 90.0)
                    : show == 4 ? (0.0, 1.0)
                    : ((double, double)?)null;
                var range = ReadRange(da, "Colour Range", auto);
                var disp = shown < 0 ? (Func<int, Geometry.Vec3>)(v => check.Final.Displacement(v)) : (v => check.Stages[shown].Displacement[v]);
                var printed = shown < 0 ? null : check.Stages[shown].VonMises.Select(double.IsNaN).ToArray();

                da.SetData(Out("Max Utilisation"), check.MaxUtilisation);
                da.SetDataList(Out("Values"), values);
                da.SetDataList(Out("Stage Height"), check.Stages.Select(x => x.TopZ / s));
                da.SetDataList(Out("Stage Utilisation"), check.Stages.Select(x => check.HasStrength ? x.MaxUtilisation : x.MaxVonMises));
                da.SetData(Out("Worst Stage"), check.WorstStage);
                if (finite.Length > 0)
                {
                    int iMax = Array.IndexOf(values, finite.Max());
                    da.SetData(Out("Worst Point"), RhinoConvert.ToPoint(model.Mesh.FaceCentroid(iMax), s));
                }
                da.SetDataList(Out("Force Flow"), FlowConvert.ToVectors(check.FinalInterlayer.Flow));
                da.SetDataList(Out("Flow Lines"), FlowConvert.ToLines(model, check.FinalInterlayer.Flow));
                da.SetData(Out("Preview"), Preview.FaceColoured(model.Mesh, values, s, out var used, range, disp, ds, printed));
                string when = shown < 0 ? "finished piece" : $"stage {shown}";
                SetLegend($"{title}, {when}", unit, used, legend);
                da.SetData(Out("Info"), Info(check, plan, shown));

                if (fromFinished && shown >= 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{title} is shown for the finished piece (not computed per stage).");
                if (!check.HasStrength && show == 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No clay strength: set Strength here or on Clay Material to get utilisation.");
                if (check.HasStrength && check.MaxUtilisation > 1)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Interlayer utilisation reaches {check.MaxUtilisation:G3} (> 1).");
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        private static double[] Values(PrintCheckResult c, PrintPlan plan, int show, int stage, out string title, out string unit, out bool fromFinished)
        {
            var model = plan.Design.Model;
            var mesh = model.Mesh;
            fromFinished = false;
            var fin = c.FinalInterlayer;
            switch (show)
            {
                case 1:
                    title = "Interface angle"; unit = "deg"; fromFinished = true;
                    return fin.FlowAngle;
                case 2:
                    title = "Von Mises"; unit = "MPa";
                    return stage < 0 ? c.FinalVonMises : c.Stages[stage].VonMises;
                case 3:
                {
                    title = "Displacement"; unit = "mm";
                    var st = stage < 0 ? null : c.Stages[stage];
                    return Enumerable.Range(0, mesh.FaceCount).Select(f =>
                    {
                        if (st != null && double.IsNaN(st.VonMises[f])) return double.NaN;
                        return mesh.Faces[f].Average(v => (st == null ? c.Final.Displacement(v) : st.Displacement[v]).Length);
                    }).ToArray();
                }
                case 4:
                    title = "Layer contact"; unit = "fraction"; fromFinished = true;
                    return fin.Contact;
                default:
                    title = "Interlayer utilisation"; unit = "-";
                    if (!c.HasStrength) return Enumerable.Repeat(double.NaN, mesh.FaceCount).ToArray();
                    return stage < 0 ? fin.Utilisation : c.Stages[stage].Utilisation;
            }
        }

        private static string Info(PrintCheckResult c, PrintPlan plan, int shown)
        {
            var fin = c.FinalInterlayer;
            var valid = Enumerable.Range(0, fin.Utilisation.Length).Where(fin.IsValid).ToArray();
            double wsum = valid.Sum(e => Math.Abs(fin.Flow.Dominant[e]));
            double angle = wsum > 0 ? valid.Sum(e => fin.FlowAngle[e] * Math.Abs(fin.Flow.Dominant[e])) / wsum : 0;
            string lines =
                $"Finished piece (all loads): " +
                (c.HasStrength ? $"interlayer utilisation {c.FinalMaxUtilisation:G3}, " : "") +
                $"von Mises {c.FinalVonMises.Max():G3} MPa, displacement {c.Final.MaxDisplacement:G3} mm\n" +
                $"Interface angle (force-weighted mean) {angle:F1}° (0 = force crosses the layers squarely)\n";
            if (c.Stages.Count > 0 && c.WorstStage >= 0)
            {
                var w = c.Stages[c.WorstStage];
                lines += $"During printing ({c.Stages.Count} stages, self-weight): worst is stage {c.WorstStage}, layer {w.Layer}: " +
                         (c.HasStrength ? $"utilisation {w.MaxUtilisation:G3}, " : "") + $"von Mises {w.MaxVonMises:G3} MPa\n";
            }
            lines += $"Showing {(shown < 0 ? "the finished piece" : $"stage {shown}")}";
            if (!c.HasStrength) lines += "\n(no strength: utilisation not reported)";
            return lines;
        }
    }
}
