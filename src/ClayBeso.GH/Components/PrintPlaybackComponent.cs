using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.GH.Common;
using ClayBeso.Printing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace ClayBeso.GH.Components
{
    public class PrintPlaybackComponent : LegendComponent
    {
        public PrintPlaybackComponent()
            : base("Print Playback", "Playback",
                "Watch the print being built: the beads laid down up to a moment of the print, and where the nozzle is.\n" +
                "Connect the Print output of Print Layers and drive Time (0-1) with a slider. Right-click the slider > " +
                "Animate to make a video.\n" +
                "Each bead is as wide as the wall and as tall as its layer, with rounded sides, so the preview shows the " +
                "real bead stack. Connect Print Check's Check output to see how safe the print is at that moment.",
                Tab.Name, Tab.Print) { }

        public override Guid ComponentGuid => new Guid("8e5fe843-b827-4e67-8c66-3e578a029861");
        protected override System.Drawing.Bitmap Icon => Icons.Get("PrintPlayback");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        private static readonly (string, int)[] ColourOptions =
        {
            ("Layer", 0),
            ("Flow factor", 1),
            ("Layer height", 2),
            ("Bead width", 3),
            ("Wall lean", 4),
        };

        protected override IEnumerable<Dropdown> Dropdowns => new[] { new Dropdown("Colour", 0, ColourOptions) };

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Print", "Print", "Print output of Print Layers.", GH_ParamAccess.item);
            p.AddNumberParameter("Time", "Time", "Moment of the print, 0 = start, 1 = finished (by print time). Use a 0-1 slider.", GH_ParamAccess.item, 1.0);
            p.AddIntegerParameter("Colour", "Colour", "Bead colours: 0 = layer, 1 = flow factor, 2 = layer height, 3 = bead width, 4 = wall lean (a dropdown appears when you place the component).", GH_ParamAccess.item, 0);
            p.AddGenericParameter("Check", "Check", "Optional: Check output of Print Check, to report the buckling factor and interlayer utilisation of the stage printed so far.", GH_ParamAccess.item);
            p.AddNumberParameter("Speed", "Speed", "Nozzle speed along the path [mm/s], for the print time.", GH_ParamAccess.item, 25.0);
            p.AddIntegerParameter("Smoothness", "Sm", "Segments per rounded bead side (1 = flat-sided beads, light; 4 = round).", GH_ParamAccess.item, 3);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the colour scale. Empty = the range over the whole print, so colours don't shift while playing.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddMeshParameter("Beads", "Beads", "The beads printed so far (document units), coloured by the Colour option.", GH_ParamAccess.item);
            p.AddPointParameter("Nozzle", "Nozzle", "Nozzle position at this moment (document units).", GH_ParamAccess.item);
            p.AddCurveParameter("Current Layer", "Current Layer", "Toolpath of the layer being printed (document units).", GH_ParamAccess.item);
            p.AddIntegerParameter("Layer", "Layer", "Layer being printed (1 = first).", GH_ParamAccess.item);
            p.AddNumberParameter("Elapsed", "Elapsed", "Print time so far [min] at Speed.", GH_ParamAccess.item);
            p.AddNumberParameter("Total Time", "Total", "Print time of the whole piece [min] at Speed (printing only, no travel or pauses).", GH_ParamAccess.item);
            p.AddTextParameter("Info", "Info", "Progress, time and, with Check connected, how safe the print is at this moment.", GH_ParamAccess.item);
        }

        // The timeline only depends on the print and the speed; keep it while the slider moves.
        private PrintTimeline _timeline;

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object printObj = null, checkObj = null;
            double time = 1, speed = 25;
            int colour = 0, smooth = 3;
            bool legend = true;
            if (!da.GetData(In("Print"), ref printObj)) return;
            da.GetData(In("Time"), ref time);
            da.GetData(In("Colour"), ref colour);
            da.GetData(In("Check"), ref checkObj);
            da.GetData(In("Speed"), ref speed);
            da.GetData(In("Smoothness"), ref smooth);
            da.GetData(In("Legend"), ref legend);

            try
            {
                var plan = RhinoConvert.Unwrap<PrintPlan>(printObj)
                    ?? throw new ArgumentException("Print must be the Print output of Print Layers.");
                if (_timeline == null || _timeline.Plan != plan || _timeline.Speed != speed)
                    _timeline = new PrintTimeline(plan, speed);
                var tl = _timeline;
                if (tl.Paths.Count == 0) throw new ArgumentException("The print has no layer paths.");
                double s = plan.Design.Model.UnitScale;

                var (value, title, unit) = Field(tl, colour);
                var all = tl.Paths.SelectMany((p, i) => Enumerable.Range(0, p.Points.Count).Select(k => value(i, k)));
                var range = ReadRange(da, "Colour Range", Preview.AutoRange(all)).Value;

                var now = tl.At(time);
                var beads = tl.Beads(time, value, Math.Max(1, Math.Min(8, smooth)));
                da.SetData(Out("Beads"), Preview.ValueColoured(beads, s, range));
                SetLegend(title, unit, range, legend);

                var path = tl.Paths[now.Path];
                var pts = path.Points.Select(q => RhinoConvert.ToPoint(q, s)).ToList();
                if (path.Closed) pts.Add(pts[0]);
                da.SetData(Out("Nozzle"), RhinoConvert.ToPoint(now.Nozzle, s));
                da.SetData(Out("Current Layer"), new PolylineCurve(pts));
                da.SetData(Out("Layer"), now.Layer);
                da.SetData(Out("Elapsed"), now.Time / 60);
                da.SetData(Out("Total Time"), tl.TotalTime / 60);

                var check = RhinoConvert.Unwrap<PrintCheckResult>(checkObj);
                if (checkObj != null && check == null)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Check must be the Check output of Print Check.");
                da.SetData(Out("Info"), Info(tl, now, check));
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        /// <summary>Value per (timeline path, point) for the Colour option.</summary>
        private static (Func<int, int, double> value, string title, string unit) Field(PrintTimeline tl, int colour)
        {
            var plan = tl.Plan;
            // Overhang results are stored in the plan's path order; the timeline sorts by layer.
            var planIndex = new Dictionary<LayerPath, int>();
            for (int i = 0; i < plan.Paths.Count; i++) planIndex[plan.Paths[i]] = i;
            double At(IList<double> v, int k) => v != null && k < v.Count ? v[k] : double.NaN;
            switch (colour)
            {
                case 1: return ((i, k) => plan.FlowFactor(At(tl.Paths[i].BeadArea, k)), "Flow factor", "× nominal");
                case 2: return ((i, k) => At(tl.Paths[i].RealisedHeight, k), "Layer height", "mm");
                case 3: return ((i, k) => At(tl.Paths[i].Width, k), "Bead width", "mm");
                case 4: return ((i, k) => At(plan.Overhangs[planIndex[tl.Paths[i]]].Lean, k), "Wall lean", "deg");
                default: return ((i, k) => tl.Paths[i].Layer, "Layer", "");
            }
        }

        private static string Info(PrintTimeline tl, PrintMoment now, PrintCheckResult check)
        {
            int layers = tl.Paths.Max(p => p.Layer);
            var text =
                $"Layer {now.Layer} of {layers}, {now.Length / tl.TotalLength:P0} of the path\n" +
                $"Time {now.Time / 60:F1} of {tl.TotalTime / 60:F1} min at {tl.Speed:G3} mm/s " +
                $"({tl.TotalLength / 1000:F1} m of bead; printing only, no travel or pauses)";
            if (check == null || check.Stages.Count == 0) return text;

            // The last checked stage that is already printed (stages are checked at a few heights only).
            var done = check.Stages.Where(st => st.Skipped == null && st.Layer <= now.Layer).ToList();
            if (done.Count == 0) return text + "\nNo print stage checked this low yet.";
            var stage = done[done.Count - 1];
            int index = check.Stages.IndexOf(stage);
            text += $"\nPrinted so far ≈ stage {index} (up to layer {stage.Layer}): " +
                    $"buckling factor {BucklingText.Factor(stage.BucklingFactor)}";
            if (check.HasStrength) text += $", interlayer utilisation {stage.MaxUtilisation:P0}";
            if (!double.IsNaN(stage.BucklingFactor) && stage.BucklingFactor < BucklingText.Margin)
                text += $"\nAt risk: buckling factor below {BucklingText.Margin:G2}.";
            if (check.HasStrength && stage.MaxUtilisation > 1)
                text += "\nAt risk: layers expected to separate (utilisation > 100 %).";
            return text;
        }
    }
}
