using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Fem;

namespace ClayBeso.Optimisation
{
    public enum BesoMode
    {
        /// <summary>
        /// Graded BESO: wall thickness varies continuously in [tMin, tMax]. No holes, so the
        /// result can always be printed as a continuous wall.
        /// </summary>
        Thickness = 0,

        /// <summary>Classic soft-kill BESO: elements switch between solid and void (perforations).</summary>
        Void = 1,
    }

    public sealed class BesoSettings
    {
        public BesoMode Mode { get; set; } = BesoMode.Thickness;

        /// <summary>
        /// Target material volume as a fraction of the reference volume (design area x tMax in
        /// thickness mode, design area x wall thickness in void mode).
        /// </summary>
        public double VolumeFraction { get; set; } = 0.5;

        /// <summary>Evolutionary ratio: maximum relative volume change per iteration.</summary>
        public double EvolutionRate { get; set; } = 0.03;

        /// <summary>Sensitivity filter radius [mm]. Roughly 2-3 element sizes is a good start.</summary>
        public double FilterRadius { get; set; } = 10;

        public int MaxIterations { get; set; } = 100;

        /// <summary>Relative change of compliance over the convergence window.</summary>
        public double Tolerance { get; set; } = 1e-3;

        public int ConvergenceWindow { get; set; } = 5;

        // --- thickness mode
        public double MinThickness { get; set; } = 3.5;
        public double MaxThickness { get; set; } = 7;

        /// <summary>Maximum thickness change per iteration as a fraction of (tMax - tMin).</summary>
        public double MoveLimit { get; set; } = 0.1;

        /// <summary>Optimality-criteria damping exponent.</summary>
        public double Damping { get; set; } = 0.5;

        // --- void mode
        public double Penalty { get; set; } = 3;
        public double MinDensity { get; set; } = 1e-3;

        /// <summary>Maximum fraction of the design volume re-admitted per iteration.</summary>
        public double MaxAdmissionRatio { get; set; } = 0.02;

        public void Validate()
        {
            if (VolumeFraction <= 0 || VolumeFraction > 1) throw new ArgumentException("Volume fraction must be in (0, 1].");
            if (EvolutionRate <= 0) throw new ArgumentException("Evolution rate must be positive.");
            if (Mode == BesoMode.Thickness)
            {
                if (MinThickness <= 0 || MaxThickness <= MinThickness)
                    throw new ArgumentException("Need 0 < tMin < tMax.");
            }
        }
    }

    /// <summary>
    /// Bi-directional evolutionary optimisation of a shell for minimum compliance at a target
    /// volume, including design-dependent self-weight. Call <see cref="Step"/> repeatedly.
    /// </summary>
    public sealed class BesoOptimizer
    {
        private readonly ShellModel _model;
        private readonly ShellSolver _solver;
        private readonly SensitivityFilter _filter;
        private readonly int[] _design;          // indices of non-frozen elements
        private double[] _previousSensitivity;
        private double _targetVolume;            // design-element volume at convergence
        private double _referenceVolume;

        public BesoSettings Settings { get; }
        public ShellModel Model => _model;
        public ShellSolver Solver => _solver;

        /// <summary>Design variable: thickness [mm] in thickness mode, density x in void mode.</summary>
        public double[] Design { get; }

        public ElementScaling Scaling { get; private set; }
        public AnalysisResult LastResult { get; private set; }

        /// <summary>Filtered sensitivity per element (per unit volume) from the last step.</summary>
        public double[] Sensitivity { get; private set; }

        public int Iteration { get; private set; }
        public bool Converged { get; private set; }
        public List<double> ComplianceHistory { get; } = new List<double>();
        public List<double> VolumeFractionHistory { get; } = new List<double>();

        public bool Finished => Converged || Iteration >= Settings.MaxIterations;

        public BesoOptimizer(ShellModel model, BesoSettings settings, ShellSolver solver = null)
        {
            settings.Validate();
            _model = model;
            Settings = settings;
            _solver = solver ?? new ShellSolver(model);
            _filter = new SensitivityFilter(model.Mesh, settings.FilterRadius);

            int m = model.ElementCount;
            _design = Enumerable.Range(0, m).Where(e => !model.Frozen[e]).ToArray();
            if (_design.Length == 0) throw new ArgumentException("All elements are frozen; nothing to optimise.");

            Design = new double[m];
            for (int e = 0; e < m; e++)
            {
                if (settings.Mode == BesoMode.Thickness)
                    Design[e] = model.Frozen[e]
                        ? model.Thickness[e]
                        : Math.Min(settings.MaxThickness, Math.Max(settings.MinThickness, model.Thickness[e]));
                else
                    Design[e] = 1.0;
            }

            _referenceVolume = _design.Sum(e => _model.Elements[e].Area * ReferenceThickness(e));
            _targetVolume = settings.VolumeFraction * _referenceVolume;
            Scaling = BuildScaling();
        }

        private double ReferenceThickness(int e) =>
            Settings.Mode == BesoMode.Thickness ? Settings.MaxThickness : _model.Thickness[e];

        /// <summary>Volume of the design elements for the current design [mm^3].</summary>
        public double DesignVolume => _design.Sum(e => ElementVolume(e, Design[e]));

        public double CurrentVolumeFraction => DesignVolume / _referenceVolume;

        private double ElementVolume(int e, double x) =>
            _model.Elements[e].Area * (Settings.Mode == BesoMode.Thickness ? x : (x >= 1 ? _model.Thickness[e] : 0));

        /// <summary>Effective wall thickness per element for downstream use (0 for voids).</summary>
        public double[] EffectiveThickness()
        {
            var t = new double[_model.ElementCount];
            for (int e = 0; e < t.Length; e++)
                t[e] = Settings.Mode == BesoMode.Thickness ? Design[e] : (Design[e] >= 1 ? _model.Thickness[e] : 0);
            return t;
        }

        /// <summary>Solid/void flag per element (all true in thickness mode).</summary>
        public bool[] Solid() => Design.Select(x => Settings.Mode == BesoMode.Thickness || x >= 1).ToArray();

        private ElementScaling BuildScaling()
        {
            int m = _model.ElementCount;
            var s = new ElementScaling(m);
            for (int e = 0; e < m; e++)
            {
                if (Settings.Mode == BesoMode.Thickness)
                {
                    s.SetThickness(e, Design[e]);
                }
                else
                {
                    double t = _model.Thickness[e];
                    double xp = Math.Pow(Design[e], Settings.Penalty);
                    s.Membrane[e] = xp * t;
                    s.Bending[e] = xp * t * t * t;
                    s.Weight[e] = Design[e] * t;
                    s.Thickness[e] = t;
                }
            }
            return s;
        }

        /// <summary>Run one analysis + update. Returns false once finished.</summary>
        public bool Step()
        {
            if (Finished) return false;

            var r = _solver.Solve(Scaling);
            LastResult = r;
            ComplianceHistory.Add(r.Compliance);
            VolumeFractionHistory.Add(CurrentVolumeFraction);

            var raw = RawSensitivity(r);
            var filtered = _filter.Apply(raw);
            if (_previousSensitivity != null)
                for (int e = 0; e < filtered.Length; e++)
                    filtered[e] = 0.5 * (filtered[e] + _previousSensitivity[e]);
            _previousSensitivity = filtered;
            Sensitivity = filtered;

            Iteration++;
            Converged = CheckConvergence();
            if (Converged) return false;

            double next = NextVolume();
            if (Settings.Mode == BesoMode.Thickness) UpdateThickness(filtered, next);
            else UpdateVoid(filtered, next);

            Scaling = BuildScaling();
            return !Finished;
        }

        /// <summary>
        /// alpha_e = -dC/dx_e / dV_e/dx_e. With design-dependent self-weight
        /// dC/dx = 2 u^T df/dx - u^T dK/dx u, so heavy, lightly-strained elements can score negative.
        /// </summary>
        public double[] RawSensitivity(AnalysisResult r)
        {
            int m = _model.ElementCount;
            var a = new double[m];
            for (int e = 0; e < m; e++)
            {
                double area = _model.Elements[e].Area;
                double em = r.MembraneWork[e], eb = r.BendingWork[e], wu = r.WeightWork[e];
                if (Settings.Mode == BesoMode.Thickness)
                {
                    double t = Design[e];
                    // fm = t, fb = t^3, fw = t ; dV/dt = A
                    a[e] = (em + 3 * t * t * eb - 2 * wu) / area;
                }
                else
                {
                    double t = _model.Thickness[e];
                    double p = Settings.Penalty;
                    double dk = p * Math.Pow(Design[e], p - 1);
                    // fm = x^p t, fb = x^p t^3, fw = x t ; dV/dx = A t
                    a[e] = (dk * (t * em + t * t * t * eb) - 2 * t * wu) / (area * t);
                }
            }
            return a;
        }

        private double NextVolume()
        {
            double v = DesignVolume;
            double er = Settings.EvolutionRate;
            return v > _targetVolume
                ? Math.Max(_targetVolume, v * (1 - er))
                : Math.Min(_targetVolume, v * (1 + er));
        }

        private bool CheckConvergence()
        {
            int n = Settings.ConvergenceWindow;
            var h = ComplianceHistory;
            if (h.Count < 2 * n) return false;
            if (Math.Abs(DesignVolume - _targetVolume) > 1e-3 * _targetVolume) return false;
            double recent = 0, before = 0;
            for (int i = 0; i < n; i++)
            {
                recent += h[h.Count - 1 - i];
                before += h[h.Count - 1 - n - i];
            }
            return Math.Abs(recent - before) <= Settings.Tolerance * Math.Abs(recent);
        }

        /// <summary>Optimality-criteria resizing with a bisection on the Lagrange multiplier.</summary>
        private void UpdateThickness(double[] alpha, double targetVolume)
        {
            var st = Settings;
            double move = st.MoveLimit * (st.MaxThickness - st.MinThickness);
            double aMax = _design.Max(e => alpha[e]);
            var t0 = _design.Select(e => Design[e]).ToArray();
            var lo = new double[_design.Length];
            var hi = new double[_design.Length];
            for (int k = 0; k < _design.Length; k++)
            {
                lo[k] = Math.Max(st.MinThickness, t0[k] - move);
                hi[k] = Math.Min(st.MaxThickness, t0[k] + move);
            }

            if (aMax <= 0)
            {
                // Every element's weight costs more than its stiffness gives: thin uniformly.
                for (int k = 0; k < _design.Length; k++) Design[_design[k]] = lo[k];
                return;
            }

            double Volume(double lambda)
            {
                double v = 0;
                for (int k = 0; k < _design.Length; k++)
                {
                    int e = _design[k];
                    double ratio = Math.Max(alpha[e], 0) / lambda;
                    double t = Math.Min(hi[k], Math.Max(lo[k], t0[k] * Math.Pow(ratio, st.Damping)));
                    v += _model.Elements[e].Area * t;
                }
                return v;
            }

            // Volume(lambda) decreases with lambda; bisect geometrically.
            double l1 = aMax * 1e-12, l2 = aMax * 1e12;
            for (int it = 0; it < 200 && l2 / l1 > 1 + 1e-9; it++)
            {
                double mid = Math.Sqrt(l1 * l2);
                if (Volume(mid) > targetVolume) l1 = mid; else l2 = mid;
            }
            double lambdaStar = Math.Sqrt(l1 * l2);
            for (int k = 0; k < _design.Length; k++)
            {
                int e = _design[k];
                double ratio = Math.Max(alpha[e], 0) / lambdaStar;
                Design[e] = Math.Min(hi[k], Math.Max(lo[k], t0[k] * Math.Pow(ratio, st.Damping)));
            }
        }

        /// <summary>Huang &amp; Xie BESO update: rank by sensitivity, cap re-admitted volume.</summary>
        private void UpdateVoid(double[] alpha, double targetVolume)
        {
            var st = Settings;
            var ranked = _design.OrderByDescending(e => alpha[e]).ToArray();
            double Vol(int e) => _model.Elements[e].Area * _model.Thickness[e];

            var keep = new HashSet<int>();
            double v = 0;
            foreach (int e in ranked)
            {
                if (v >= targetVolume) break;
                keep.Add(e);
                v += Vol(e);
            }

            double added = keep.Where(e => Design[e] < 1).Sum(Vol);
            double addCap = st.MaxAdmissionRatio * _referenceVolume;
            if (added > addCap)
            {
                keep.Clear();
                v = 0;
                double a = 0;
                foreach (int e in ranked.Where(e => Design[e] < 1))
                {
                    if (a + Vol(e) > addCap) break;
                    keep.Add(e);
                    a += Vol(e);
                }
                v = a;
                foreach (int e in ranked.Where(e => Design[e] >= 1))
                {
                    if (v >= targetVolume) break;
                    keep.Add(e);
                    v += Vol(e);
                }
            }

            foreach (int e in _design) Design[e] = keep.Contains(e) ? 1.0 : st.MinDensity;
        }
    }
}
