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
                "strip c = (w - h)/w, and the wall is weaker across the layers. With Buckling on, it also finds how many " +
                "times its loads each stage carries before the wall buckles.\n" +
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
            ("Buckling mode", 5),
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
            p.AddNumberParameter("Strength", "Strength", "Clay strength [MPa] for the interlayer utilisation. 0 = the Material strength; if that is 0 too, utilisation isn't reported.", GH_ParamAccess.item, 0.0);
            p.AddIntegerParameter("Show", "Show", "0 = interlayer utilisation, 1 = interface angle, 2 = von Mises, 3 = displacement, 4 = layer contact, 5 = buckling mode (needs Buckling on; dropdown appears when placed).", GH_ParamAccess.item, 0);
            p.AddIntegerParameter("Stage", "Stage", "-1 = finished piece, -2 = worst print stage, 0..N-1 = that stage (dropdown appears when placed; use a slider for a specific stage).", GH_ParamAccess.item, -1);
            p.AddIntegerParameter("Stages", "Stages", "Print stages to check (0 = finished piece only).", GH_ParamAccess.item, 8);
            p.AddNumberParameter("Bond Efficiency", "η", "Interface efficiency 0-1 (calibrate with docs/VALIDATION.md section 4). 0 = the design's, or 1.", GH_ParamAccess.item, 0.0);
            p.AddBooleanParameter("Orthotropic", "Orth", "Treat the wall as weaker across its layers (stiffness × η·c from the printed layer heights).", GH_ParamAccess.item, true);
            p.AddBooleanParameter("Stage Point Loads", "SPL", "Also apply the point loads during printing (they always act on the finished piece).", GH_ParamAccess.item, false);
            p.AddNumberParameter("Deform Scale", "DS", "Displacement exaggeration for the preview.", GH_ParamAccess.item, 10.0);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the preview colour scale. Empty = automatic for what is shown.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            p.AddBooleanParameter("Buckling", "Buckling", "Linear buckling analysis of the finished piece and of every print stage (takes longer). Turn off while BESO is running if it slows things down.", GH_ParamAccess.item, true);
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
            p.AddNumberParameter("Buckling Factor", "Buckling Factor", "Lowest buckling load factor [-], finished piece or any stage: the loads times this make a perfect shell buckle. < 1 = buckles; real walls buckle well below it, so aim higher (see Info). Infinity = nothing in compression.", GH_ParamAccess.item);
            p.AddNumberParameter("Stage Buckling", "λs", "Per stage: buckling load factor under its own weight [-] (how many times heavier the clay could be before that stage buckles).", GH_ParamAccess.list);
            p.AddGenericParameter("Check", "Check", "The whole check result. Connect it to Print Playback to see how safe the print is at each moment.", GH_ParamAccess.item);
            p.AddIntegerParameter("Score", "Score", "Printability score 1-100 (overhang, bead support, nozzle tilt, layer contact and heights, closed layers, buckling, layer bond). Use it to compare designs; Score Report says what limits it.", GH_ParamAccess.item);
            p.AddTextParameter("Score Report", "Report", "Each aspect of the score, weakest first, with what to change.", GH_ParamAccess.item);
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
            bool buckling = true;
            da.GetData(In("Buckling"), ref buckling);

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
                    Buckling = buckling,
                });

                int shown = stage == -2 ? check.WorstStage : stage;
                if (shown >= check.Stages.Count || (shown >= 0 && check.Stages[shown].Skipped != null)) shown = -1;
                var values = Values(check, plan, show, shown, out string title, out string unit, out bool fromFinished);
                double s = model.UnitScale;

                var finite = values.Where(v => !double.IsNaN(v)).ToArray();
                (double, double)? auto = show == 0 ? (0.0, Math.Max(1.0, finite.DefaultIfEmpty(0).Max()))
                    : show == 1 ? (0.0, 90.0)
                    : show == 4 || show == 5 ? (0.0, 1.0)
                    : ((double, double)?)null;
                var range = ReadRange(da, "Colour Range", auto);
                var disp = shown < 0 ? (Func<int, Geometry.Vec3>)(v => check.Final.Displacement(v)) : (v => check.Stages[shown].Displacement[v]);
                if (show == 5)
                {
                    // Draw the buckling shape instead of the static deflection.
                    var mode = BucklingMode(check, shown);
                    double amp = BucklingText.Amplitude(model.Mesh);
                    disp = v => mode == null ? Geometry.Vec3.Zero : mode[v] * amp;
                }
                var printed = shown < 0 ? null : check.Stages[shown].VonMises.Select(double.IsNaN).ToArray();

                da.SetData(Out("Max Utilisation"), check.MaxUtilisation);
                da.SetDataList(Out("Values"), values);
                da.SetDataList(Out("Stage Height"), check.Stages.Select(x => x.TopZ / s));
                da.SetDataList(Out("Stage Utilisation"), check.Stages.Select(x => check.HasStrength ? x.MaxUtilisation : x.MaxVonMises));
                da.SetData(Out("Worst Stage"), check.WorstStage);
                da.SetData(Out("Buckling Factor"), check.MinBucklingFactor);
                da.SetDataList(Out("Stage Buckling"), check.Stages.Select(x => x.BucklingFactor));
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
                da.SetData(Out("Check"), check);
                var score = PrintScore.Evaluate(plan, check);
                da.SetData(Out("Score"), score.Total);
                da.SetData(Out("Score Report"), score.Report());

                if (fromFinished && shown >= 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{title} is shown for the finished piece (not computed per stage).");
                if (!check.HasStrength && show == 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No clay strength: set Strength here or on Material to get utilisation.");
                if (show == 5 && !buckling)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Turn Buckling on to see the buckling mode.");
                BucklingText.Warn(this, check.MinBucklingFactor,
                    check.BucklingStage >= 0 && check.Stages[check.BucklingStage].BucklingFactor <= check.FinalBuckling.Critical
                        ? $"stage {check.BucklingStage}" : "finished piece");
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
                case 5:
                {
                    title = "Buckling mode"; unit = "relative";
                    var values = BucklingText.FaceValues(mesh, BucklingMode(c, stage));
                    if (stage >= 0)
                        for (int f = 0; f < values.Length; f++)
                            if (double.IsNaN(c.Stages[stage].VonMises[f])) values[f] = double.NaN;
                    return values;
                }
                default:
                    title = "Interlayer utilisation"; unit = "-";
                    if (!c.HasStrength) return Enumerable.Repeat(double.NaN, mesh.FaceCount).ToArray();
                    return stage < 0 ? fin.Utilisation : c.Stages[stage].Utilisation;
            }
        }

        private static Geometry.Vec3[] BucklingMode(PrintCheckResult c, int stage)
        {
            if (stage >= 0) return c.Stages[stage].BucklingMode;
            var b = c.FinalBuckling;
            return b != null && b.Modes.Length > 0 ? b.Modes[0] : null;
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
            if (c.FinalBuckling != null)
            {
                lines += $"Buckling factor (perfect shape): finished piece {BucklingText.Factor(c.FinalBuckling.Critical)}";
                if (c.BucklingStage >= 0)
                {
                    var b = c.Stages[c.BucklingStage];
                    lines += $", lowest while printing {BucklingText.Factor(b.BucklingFactor)} at stage {c.BucklingStage} " +
                             $"(z = {b.TopZ / plan.Design.Model.UnitScale:G4})";
                }
                lines += $"\n  < 1 = buckles; real walls buckle well below the perfect-shape value, so treat < {BucklingText.Margin:G2} as at risk\n";
            }
            lines += $"Showing {(shown < 0 ? "the finished piece" : $"stage {shown}")}";
            if (!c.HasStrength) lines += "\n(no strength: utilisation not reported)";
            return lines;
        }
    }
}
