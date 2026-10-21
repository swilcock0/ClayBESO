using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.GH.Common;
using ClayBeso.Optimisation;
using Grasshopper.Kernel;

namespace ClayBeso.GH.Components
{
    /// <summary>
    /// Runs the optimiser live: while Run is true it does a few iterations per solution and then
    /// schedules another solution, so the canvas stays responsive and you can watch it evolve.
    /// </summary>
    public class BesoComponent : LegendComponent
    {
        private BesoOptimizer _opt;
        private ShellModel _model;
        private string _signature;
        private bool _radiusAuto;
        private string _error;

        public BesoComponent()
            : base("Shell BESO", "BESO",
                "Bi-directional evolutionary structural optimisation of a shell for minimum compliance " +
                "(maximum stiffness), including self-weight.\n" +
                "Mode 0 = Thickness: graded wall thickness, no holes (always printable). By default it keeps " +
                "the clay volume of the Shell Model thickness and only redistributes it.\n" +
                "Mode 1 = Void: classic BESO, removes faces to make perforations.\n" +
                "Set Run to true (Boolean Toggle) to iterate. Set Reset to true to start over.\n" +
                "Connect the Design output to Print Layers (and to Shell Analysis to analyse the result).",
                Tab.Name, Tab.Optimise) { }

        /// <summary>For the legacy version, which only differs in the order of its inputs and outputs.</summary>
        protected BesoComponent(string name, string nickname, string description)
            : base(name, nickname, description, Tab.Name, Tab.Optimise) { }

        public override Guid ComponentGuid => new Guid("0cfa1b98-835b-45e0-b75c-c575ce010dab");

        protected override IEnumerable<Dropdown> Dropdowns => new[] { new Dropdown("Mode", 0, Modes.BesoMode) };
        protected override System.Drawing.Bitmap Icon => Icons.Get("ShellBeso");

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell model.", GH_ParamAccess.item);
            p.AddBooleanParameter("Run", "Run", "Iterate while true (connect a Boolean Toggle).", GH_ParamAccess.item, false);
            p.AddBooleanParameter("Reset", "Reset", "Restart from the initial design.", GH_ParamAccess.item, false);
            p.AddIntegerParameter("Mode", "Mode", "0 = Thickness (graded wall), 1 = Void (perforations). A dropdown appears when you place the component.", GH_ParamAccess.item, 0);
            p.AddNumberParameter("Min Thickness", "Min Thickness", "Thickness mode: thinnest printable wall [mm] (about 1.1x nozzle diameter).", GH_ParamAccess.item, 3.5);
            p.AddNumberParameter("Max Thickness", "Max Thickness", "Thickness mode: thickest wall [mm] (widest bead you can extrude, about 2.3x nozzle).", GH_ParamAccess.item, 7.0);
            p.AddNumberParameter("Filter Radius", "Filter Radius", "Filter radius in document units. 0 (default) = automatic: 3× the average mesh edge length (Info shows the value used). In thickness mode the wall thickness is averaged over this radius, so it sets how wide the transitions between thin and thick wall are (no stair-steps between faces). Use at least 2-3× the mesh edge length; larger = smoother, more gradual walls.", GH_ParamAccess.item, 0.0);
            p.AddGenericParameter("Layer Rule", "Layer Rule", "Optional Layer Rule. When connected, the wall is weaker across its layers: across-layer stiffness × κ = η·(t − h)/t, with h from the rule. The rule travels on in the Design output.", GH_ParamAccess.item);
            p.AddNumberParameter("Bond Efficiency", "η", "With a Layer Rule: interface efficiency 0-1 (calibrate from tests, see docs/VALIDATION.md §4). 1 = only the contact geometry weakens the wall.", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Volume Fraction", "Vf", "Thickness mode: 0 (default) = keep the clay volume of the Shell Model thickness and only redistribute it. " +
                "A value > 0 sets a different target: average thickness = Vf × tMax (must be above tMin/tMax). " +
                "Void mode: fraction of the full wall to keep (required).", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Playback", "Playback", "Show the design at a point of the run: 0 = first iteration, 1 = final (default). Connect a 0-1 slider to scrub through the optimisation. Thickness, Design and the preview follow it; it never restarts the run.", GH_ParamAccess.item, 1.0);
            p.AddIntegerParameter("Max Iterations", "N", "Iteration limit.", GH_ParamAccess.item, 100);
            p.AddNumberParameter("Evolution Rate", "ER", "Max relative volume change per iteration.", GH_ParamAccess.item, 0.03);
            p.AddNumberParameter("Penalty", "p", "Void mode: stiffness penalty exponent.", GH_ParamAccess.item, 3.0);
            p.AddNumberParameter("Admission Ratio", "AR", "Void mode: max fraction of volume re-added per iteration.", GH_ParamAccess.item, 0.02);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the thickness colour scale [mm]. Empty = tMin to tMax in thickness mode, min-max in void mode.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("Design", "Design", "The model with this thickness and the Layer Rule and bond it was optimised with, in one wire. Connect it to Print Layers or Shell Analysis.", GH_ParamAccess.item);
            p.AddNumberParameter("Thickness", "Thickness", "Wall thickness per face [mm] (0 for removed faces in void mode).", GH_ParamAccess.list);
            p.AddMeshParameter("Preview", "Preview", "Mesh coloured by thickness (void faces hidden).", GH_ParamAccess.item);
            p.AddTextParameter("Info", "Info", "Status.", GH_ParamAccess.item);
            p.AddBooleanParameter("Converged", "Converged", "True once compliance has stabilised at the target volume.", GH_ParamAccess.item);
            p.AddIntegerParameter("Iteration", "i", "Iterations done (or the one shown in playback).", GH_ParamAccess.item);
            p.AddNumberParameter("Compliance", "C", "Compliance history [N·mm].", GH_ParamAccess.list);
            p.AddNumberParameter("Volume", "V", "Volume-fraction history.", GH_ParamAccess.list);
            p.AddBooleanParameter("Solid", "S", "Per face: true if material is kept.", GH_ParamAccess.list);
            p.AddNumberParameter("Sensitivity", "α", "Filtered sensitivity per face (higher = more useful material).", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object modelObj = null;
            int mode = 0, maxIt = 100;
            double vf = 0.0, tMin = 3.5, tMax = 7, er = 0.03, radius = 0, pen = 3, ar = 0.02;
            bool run = false, reset = false;
            if (!da.GetData(In("Model"), ref modelObj)) return;
            da.GetData(In("Mode"), ref mode);
            da.GetData(In("Volume Fraction"), ref vf);
            da.GetData(In("Min Thickness"), ref tMin);
            da.GetData(In("Max Thickness"), ref tMax);
            da.GetData(In("Evolution Rate"), ref er);
            da.GetData(In("Filter Radius"), ref radius);
            da.GetData(In("Max Iterations"), ref maxIt);
            da.GetData(In("Run"), ref run);
            da.GetData(In("Reset"), ref reset);
            da.GetData(In("Penalty"), ref pen);
            da.GetData(In("Admission Ratio"), ref ar);
            bool legend = true;
            da.GetData(In("Legend"), ref legend);
            double playback = 1.0;
            da.GetData(In("Playback"), ref playback);
            object ruleObj = null;
            double eta = 1.0;
            da.GetData(In("Layer Rule"), ref ruleObj);
            da.GetData(In("Bond Efficiency"), ref eta);
            var rule = RhinoConvert.Unwrap<Printing.LayerRule>(ruleObj);
            var range = ReadRange(da, "Colour Range", mode == 1 ? ((double, double)?)null : (tMin, tMax));

            var model = RhinoConvert.Unwrap<ShellModel>(modelObj);
            if (model == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a Shell Model."); return; }

            // Radius in mm: given in document units, or automatic from the mesh (already in mm).
            _radiusAuto = radius <= 0;
            double radiusMm = radius > 0 ? radius * model.UnitScale : SensitivityFilter.AutoRadius(model.Mesh);
            var settings = new BesoSettings
            {
                Mode = mode == 1 ? BesoMode.Void : BesoMode.Thickness,
                VolumeFraction = vf,
                MinThickness = tMin,
                MaxThickness = tMax,
                EvolutionRate = er,
                FilterRadius = radiusMm,
                MaxIterations = maxIt,
                Penalty = pen,
                MaxAdmissionRatio = ar,
                LayerRule = rule,
                Bond = new BondModel(Math.Max(1e-6, Math.Min(1, eta))),
            };
            string sig = string.Join("|", new object[] { mode, vf, tMin, tMax, er, radius, maxIt, pen, ar, rule?.ToString() ?? "no layers", eta }
                .Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)));

            // Any change to the model or settings restarts the optimisation.
            if (reset || _opt == null || !ReferenceEquals(model, _model) || sig != _signature)
            {
                try
                {
                    _opt = new BesoOptimizer(model, settings);
                    _model = model;
                    _signature = sig;
                    _error = null;
                }
                catch (Exception ex)
                {
                    _opt = null;
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                    return;
                }
            }

            if (settings.Mode == BesoMode.Thickness)
            {
                double avg = _opt.TargetAverageThickness, span = tMax - tMin;
                if (avg < tMin + 0.15 * span || avg > tMax - 0.15 * span)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Average wall {avg:G3} mm is close to {(avg < tMin + 0.15 * span ? "tMin" : "tMax")}: " +
                        "most of the wall will sit at that limit. For more variation, set the Shell Model thickness " +
                        "(or Vf) so the average is nearer the middle of tMin–tMax.");
            }

            if (run && !reset && !_opt.Finished && _error == null)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    // Iterate for ~0.3 s, then hand control back to Grasshopper and come back.
                    do { _opt.Step(); } while (!_opt.Finished && sw.ElapsedMilliseconds < 300);
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                }
                if (!_opt.Finished && _error == null)
                    OnPingDocument()?.ScheduleSolution(5, d => ExpireSolution(false));
            }

            if (_error != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Error, _error);
            Output(da, range, legend, playback);
        }

        private void Output(IGH_DataAccess da, (double lo, double hi)? range, bool legend, double playback)
        {
            var design = _opt.DesignAt(playback, out int shown);
            var thickness = _opt.EffectiveThickness(design);
            var solid = _opt.Solid(design);
            da.SetDataList(Out("Thickness"), thickness);
            da.SetDataList(Out("Solid"), solid);
            if (_opt.Sensitivity != null) da.SetDataList(Out("Sensitivity"), _opt.Sensitivity);
            da.SetData(Out("Preview"), Preview.FaceColoured(_model.Mesh, thickness, _model.UnitScale, out var used, range,
                hide: solid.Select(s => !s).ToArray()));
            SetLegend("Wall thickness", "mm", used, legend);
            da.SetDataList(Out("Compliance"), _opt.ComplianceHistory);
            da.SetDataList(Out("Volume"), _opt.VolumeFractionHistory);
            da.SetData(Out("Iteration"), shown);
            da.SetData(Out("Converged"), _opt.Converged);
            da.SetData(Out("Design"), new ShellDesign(_model, thickness, _opt.Rule, _opt.Rule != null ? _opt.Settings.Bond : null,
                $"Shell BESO, iteration {shown}"));

            if (shown != _opt.Iteration)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"Playback: showing iteration {shown} of {_opt.Iteration}. Downstream components get this design; set Playback to 1 for the final one.");

            string state = _opt.Converged ? "converged"
                : _opt.Iteration >= _opt.Settings.MaxIterations ? "stopped at iteration limit"
                : _opt.Iteration == 0 ? "ready (set Run = true)" : "running / paused";
            string c = _opt.ComplianceHistory.Count > 0 ? $"{_opt.ComplianceHistory.Last():G5} N·mm" : "-";
            da.SetData(Out("Info"),
                $"{_opt.Settings.Mode} mode, {state}\nIteration {_opt.Iteration}" +
                (shown != _opt.Iteration
                    ? $" (PLAYBACK: showing iteration {shown}, compliance {_opt.ComplianceHistory[shown - 1]:G5} N·mm)\n"
                    : "\n") +
                (_opt.Settings.KeepVolume
                    ? $"Redistributing the starting clay: average wall {_opt.TargetAverageThickness:F2} mm\n"
                    : $"Volume fraction {_opt.CurrentVolumeFraction:F3} (target {_opt.Settings.VolumeFraction:F3}" +
                      (_opt.Settings.Mode == BesoMode.Thickness ? $" = average wall {_opt.TargetAverageThickness:F2} mm)\n" : ")\n")) +
                $"Compliance {c}\nFilter radius {_opt.Settings.FilterRadius / _model.UnitScale:G3} (document units)" +
                (_radiusAuto ? ", automatic\n" : "\n") +
                $"Thickness {thickness.Where(t => t > 0).DefaultIfEmpty(0).Min():F2}-{thickness.Max():F2} mm" +
                (_opt.Iteration > 0 ? $"\nLast solve: {_opt.Solver.LastTimings.Total:F0} ms ({_opt.Solver.LastTimings})" : "") +
                LayerInfo(design, solid));
        }

        private string LayerInfo(double[] design, bool[] solid)
        {
            if (_opt.Rule == null) return "\nPrinted layers: ignored (connect a Layer Rule to include them)";
            var kappa = _opt.ScalingFor(design).Across.Where((k, e) => solid[e]).DefaultIfEmpty(1).ToArray();
            return $"\nPrinted layers: {_opt.Rule}, η = {_opt.Settings.Bond.Efficiency:G3}\n" +
                   $"  across-layer stiffness κ {kappa.Min():F2}-{kappa.Max():F2}";
        }
    }
}
