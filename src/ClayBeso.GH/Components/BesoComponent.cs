using System;
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
        private string _error;

        public BesoComponent()
            : base("Shell BESO", "BESO",
                "Bi-directional evolutionary structural optimisation of a shell for minimum compliance " +
                "(maximum stiffness), including self-weight.\n" +
                "Mode 0 = Thickness: graded wall thickness, no holes (always printable). By default it keeps " +
                "the clay volume of the Shell Model thickness and only redistributes it.\n" +
                "Mode 1 = Void: classic BESO, removes faces to make perforations.\n" +
                "Set Run to true (Boolean Toggle) to iterate. Set Reset to true to start over.",
                Tab.Name, Tab.Optimise) { }

        public override Guid ComponentGuid => new Guid("d1a4f6b5-84e7-4e31-9c94-3f5e0a2d7b04");
        protected override System.Drawing.Bitmap Icon => Icons.Get("ShellBeso");

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell model.", GH_ParamAccess.item);
            p.AddIntegerParameter("Mode", "Mode", "0 = Thickness (graded wall), 1 = Void (perforations).", GH_ParamAccess.item, 0);
            p.AddNumberParameter("Volume Fraction", "Vf", "Thickness mode: 0 (default) = keep the clay volume of the Shell Model thickness and only redistribute it. " +
                "A value > 0 sets a different target: average thickness = Vf × tMax (must be above tMin/tMax). " +
                "Void mode: fraction of the full wall to keep (required).", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Min Thickness", "tMin", "Thickness mode: thinnest printable wall [mm] (about 1.1x nozzle diameter).", GH_ParamAccess.item, 3.5);
            p.AddNumberParameter("Max Thickness", "tMax", "Thickness mode: thickest wall [mm] (widest bead you can extrude, about 2.3x nozzle).", GH_ParamAccess.item, 7.0);
            p.AddNumberParameter("Evolution Rate", "ER", "Max relative volume change per iteration.", GH_ParamAccess.item, 0.03);
            p.AddNumberParameter("Filter Radius", "R", "Sensitivity filter radius in document units. Use 2-3× the mesh edge length.", GH_ParamAccess.item, 10.0);
            p.AddIntegerParameter("Max Iterations", "N", "Iteration limit.", GH_ParamAccess.item, 100);
            p.AddBooleanParameter("Run", "Run", "Iterate while true.", GH_ParamAccess.item, false);
            p.AddBooleanParameter("Reset", "Reset", "Restart from the initial design.", GH_ParamAccess.item, false);
            p.AddNumberParameter("Penalty", "p", "Void mode: stiffness penalty exponent.", GH_ParamAccess.item, 3.0);
            p.AddNumberParameter("Admission Ratio", "AR", "Void mode: max fraction of volume re-added per iteration.", GH_ParamAccess.item, 0.02);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the thickness colour scale [mm]. Empty = tMin to tMax in thickness mode, min-max in void mode.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("Thickness", "t", "Wall thickness per face [mm] (0 for removed faces in void mode).", GH_ParamAccess.list);
            p.AddBooleanParameter("Solid", "S", "Per face: true if material is kept.", GH_ParamAccess.list);
            p.AddNumberParameter("Sensitivity", "α", "Filtered sensitivity per face (higher = more useful material).", GH_ParamAccess.list);
            p.AddMeshParameter("Preview", "P", "Mesh coloured by thickness (void faces hidden).", GH_ParamAccess.item);
            p.AddNumberParameter("Compliance", "C", "Compliance history [N·mm].", GH_ParamAccess.list);
            p.AddNumberParameter("Volume", "V", "Volume-fraction history.", GH_ParamAccess.list);
            p.AddIntegerParameter("Iteration", "i", "Iterations done.", GH_ParamAccess.item);
            p.AddBooleanParameter("Converged", "Done", "True once compliance has stabilised at the target volume.", GH_ParamAccess.item);
            p.AddTextParameter("Info", "I", "Status.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object modelObj = null;
            int mode = 0, maxIt = 100;
            double vf = 0.0, tMin = 3.5, tMax = 7, er = 0.03, radius = 10, pen = 3, ar = 0.02;
            bool run = false, reset = false;
            if (!da.GetData(0, ref modelObj)) return;
            da.GetData(1, ref mode);
            da.GetData(2, ref vf);
            da.GetData(3, ref tMin);
            da.GetData(4, ref tMax);
            da.GetData(5, ref er);
            da.GetData(6, ref radius);
            da.GetData(7, ref maxIt);
            da.GetData(8, ref run);
            da.GetData(9, ref reset);
            da.GetData(10, ref pen);
            da.GetData(11, ref ar);
            bool legend = true;
            da.GetData(13, ref legend);
            var range = ReadRange(da, 12, mode == 1 ? ((double, double)?)null : (tMin, tMax));

            var model = RhinoConvert.Unwrap<ShellModel>(modelObj);
            if (model == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a Shell Model."); return; }

            var settings = new BesoSettings
            {
                Mode = mode == 1 ? BesoMode.Void : BesoMode.Thickness,
                VolumeFraction = vf,
                MinThickness = tMin,
                MaxThickness = tMax,
                EvolutionRate = er,
                FilterRadius = radius * model.UnitScale,
                MaxIterations = maxIt,
                Penalty = pen,
                MaxAdmissionRatio = ar,
            };
            string sig = string.Join("|", new object[] { mode, vf, tMin, tMax, er, radius, maxIt, pen, ar }
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
            Output(da, range, legend);
        }

        private void Output(IGH_DataAccess da, (double lo, double hi)? range, bool legend)
        {
            var thickness = _opt.EffectiveThickness();
            var solid = _opt.Solid();
            da.SetDataList(0, thickness);
            da.SetDataList(1, solid);
            if (_opt.Sensitivity != null) da.SetDataList(2, _opt.Sensitivity);
            da.SetData(3, Preview.FaceColoured(_model.Mesh, thickness, _model.UnitScale, out var used, range,
                hide: solid.Select(s => !s).ToArray()));
            SetLegend("Wall thickness", "mm", used, legend);
            da.SetDataList(4, _opt.ComplianceHistory);
            da.SetDataList(5, _opt.VolumeFractionHistory);
            da.SetData(6, _opt.Iteration);
            da.SetData(7, _opt.Converged);

            string state = _opt.Converged ? "converged"
                : _opt.Iteration >= _opt.Settings.MaxIterations ? "stopped at iteration limit"
                : _opt.Iteration == 0 ? "ready (set Run = true)" : "running / paused";
            string c = _opt.ComplianceHistory.Count > 0 ? $"{_opt.ComplianceHistory.Last():G5} N·mm" : "-";
            da.SetData(8,
                $"{_opt.Settings.Mode} mode, {state}\nIteration {_opt.Iteration}\n" +
                (_opt.Settings.KeepVolume
                    ? $"Redistributing the starting clay: average wall {_opt.TargetAverageThickness:F2} mm\n"
                    : $"Volume fraction {_opt.CurrentVolumeFraction:F3} (target {_opt.Settings.VolumeFraction:F3}" +
                      (_opt.Settings.Mode == BesoMode.Thickness ? $" = average wall {_opt.TargetAverageThickness:F2} mm)\n" : ")\n")) +
                $"Compliance {c}\nThickness {thickness.Where(t => t > 0).DefaultIfEmpty(0).Min():F2}-{thickness.Max():F2} mm" +
                (_opt.Iteration > 0 ? $"\nLast solve: {_opt.Solver.LastTimings.Total:F0} ms ({_opt.Solver.LastTimings})" : ""));
        }
    }
}
