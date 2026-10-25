using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Printing;

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
        /// Thickness mode: 0 (default) keeps the starting volume (the Shell Model thickness), so the
        /// optimiser only redistributes the clay and doesn't reduce it.
        /// </summary>
        public double VolumeFraction { get; set; } = 0;

        /// <summary>Thickness mode with no volume fraction: redistribute the starting volume.</summary>
        public bool KeepVolume => Mode == BesoMode.Thickness && VolumeFraction <= 0;

        /// <summary>Evolutionary ratio: maximum relative volume change per iteration.</summary>
        public double EvolutionRate { get; set; } = 0.03;

        /// <summary>Sensitivity filter radius [mm]. Roughly 2-3 element sizes is a good start.</summary>
        public double FilterRadius { get; set; } = 10;

        public int MaxIterations { get; set; } = 100;

        /// <summary>Relative change of compliance over the convergence window.</summary>
        public double Tolerance { get; set; } = 1e-3;

        /// <summary>Iterations compared by the convergence check. null = 3 in thickness mode, 5 in void mode.</summary>
        public int? ConvergenceWindow { get; set; }

        /// <summary>
        /// Average each iteration's sensitivities with the previous ones (Huang &amp; Xie). Damps
        /// oscillation of the discrete void mode but only slows the continuous thickness mode.
        /// null = on in void mode, off in thickness mode.
        /// </summary>
        public bool? HistoryAveraging { get; set; }

        public int EffectiveConvergenceWindow => ConvergenceWindow ?? (Mode == BesoMode.Thickness ? 3 : 5);
        public bool EffectiveHistoryAveraging => HistoryAveraging ?? Mode == BesoMode.Void;

        // --- thickness mode
        public double MinThickness { get; set; } = 3.5;
        public double MaxThickness { get; set; } = 7;

        /// <summary>Maximum thickness change per iteration as a fraction of (tMax - tMin).</summary>
        public double MoveLimit { get; set; } = 0.3;

        /// <summary>Optimality-criteria damping exponent.</summary>
        public double Damping { get; set; } = 0.5;

        /// <summary>
        /// Thickness mode: filter the thickness itself (density filter) rather than only the
        /// sensitivities. The wall thickness is then always a smooth field, an area-weighted average over
        /// the filter radius, so it can't jump between neighbouring faces (no staircase edges, no jumps in
        /// bead width along a layer). Sensitivities follow the filter exactly (chain rule).
        /// </summary>
        public bool ThicknessFilter { get; set; } = true;

        // --- void mode

        /// <summary>
        /// Void mode stiffness penalty p. The interpolation (Huang &amp; Xie 2011) makes a removed face
        /// behave like a wall x_min times as thick: membrane stiffness and weight x_min, bending x_min^3
        /// (see <see cref="BesoOptimizer.VoidStiffness"/>).
        /// </summary>
        public double Penalty { get; set; } = 3;

        /// <summary>Void mode: density x_min of removed faces (their weight and stiffness factor).</summary>
        public double MinDensity { get; set; } = 1e-3;

        /// <summary>Maximum fraction of the design volume re-admitted per iteration.</summary>
        public double MaxAdmissionRatio { get; set; } = 0.02;

        // --- printed layers

        /// <summary>
        /// When set, the wall is weaker across its (horizontal) layers: across-layer stiffness is scaled
        /// by kappa = eta * (t - h(t)) / t, with h(t) from this rule. Automatic values in the rule
        /// (thickness range, bead area) are taken from tMin..tMax and the starting wall.
        /// </summary>
        public LayerRule LayerRule { get; set; }

        /// <summary>Bond model (efficiency eta, minimum contact) used with <see cref="LayerRule"/>.</summary>
        public BondModel Bond { get; set; } = new BondModel();

        /// <summary>Thickness mode: the target average wall thickness Vf * tMax [mm].</summary>
        public double TargetAverageThickness => VolumeFraction * MaxThickness;

        /// <summary>Thickness mode: volume fractions at or below tMin/tMax leave no design freedom.</summary>
        public double MinimumVolumeFraction => MinThickness / MaxThickness;

        public void Validate()
        {
            if (VolumeFraction < 0 || VolumeFraction > 1) throw new ArgumentException("Volume fraction must be in [0, 1].");
            if (Mode == BesoMode.Void && VolumeFraction <= 0)
                throw new ArgumentException("Void mode needs a volume fraction in (0, 1]: it removes material.");
            if (EvolutionRate <= 0) throw new ArgumentException("Evolution rate must be positive.");
            if (Mode == BesoMode.Void && (MinDensity <= 0 || MinDensity >= 1))
                throw new ArgumentException("Void mode minimum density must be in (0, 1).");
            if (Mode == BesoMode.Void && Penalty < 1) throw new ArgumentException("Void mode penalty must be at least 1.");
            if (Mode == BesoMode.Thickness)
            {
                if (MinThickness <= 0 || MaxThickness <= MinThickness)
                    throw new ArgumentException("Need 0 < tMin < tMax.");
                // The target is an average thickness of Vf * tMax. If that is not above tMin, the
                // only feasible design is a uniform tMin wall and there is nothing to optimise.
                if (!KeepVolume && VolumeFraction <= MinimumVolumeFraction)
                    throw new ArgumentException(
                        $"Volume fraction {VolumeFraction:G3} means an average wall of {TargetAverageThickness:G3} mm, " +
                        $"which is not thicker than tMin = {MinThickness:G3} mm, so every face would end at tMin. " +
                        $"Use a volume fraction above tMin/tMax = {MinimumVolumeFraction:G3} (e.g. {0.5 * (MinimumVolumeFraction + 1):G2}).");
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
        private double _designArea;

        public BesoSettings Settings { get; }
        public ShellModel Model => _model;
        public ShellSolver Solver => _solver;

        /// <summary>Design variable: thickness [mm] in thickness mode, density x in void mode.</summary>
        public double[] Design { get; }

        public ElementScaling Scaling { get; private set; }
        public AnalysisResult LastResult { get; private set; }

        /// <summary>
        /// Filtered sensitivity per element (per unit volume) from the last step: what the update ranked
        /// or resized by (void mode: stiffness part only, see <see cref="RawSensitivity"/>).
        /// </summary>
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

            if (settings.LayerRule != null)
            {
                if (settings.Bond == null) throw new ArgumentException("A layer rule needs a bond model.");
                model.EnableLayerAnisotropy(Vec3.UnitZ);
                var start = _design.Select(e => settings.Mode == BesoMode.Thickness ? Design[e] : model.Thickness[e])
                    .OrderBy(t => t).ToArray();
                double median = start[start.Length / 2];
                _rule = settings.Mode == BesoMode.Thickness
                    ? settings.LayerRule.Resolve(settings.MinThickness, settings.MaxThickness, median)
                    : settings.LayerRule.Resolve(start[0], start[start.Length - 1], median);
            }

            _referenceVolume = _design.Sum(e => _model.Elements[e].Area * ReferenceThickness(e));
            _designArea = _design.Sum(e => _model.Elements[e].Area);
            if (settings.KeepVolume)
            {
                _targetVolume = DesignVolume;
                double avg = _targetVolume / _designArea;
                double span = settings.MaxThickness - settings.MinThickness;
                if (avg <= settings.MinThickness + 1e-3 * span || avg >= settings.MaxThickness - 1e-3 * span)
                    throw new ArgumentException(
                        $"The starting wall (Shell Model thickness, average {avg:G3} mm) must lie strictly between " +
                        $"tMin = {settings.MinThickness:G3} and tMax = {settings.MaxThickness:G3} mm, otherwise there is " +
                        "nothing to redistribute.");
            }
            else
            {
                _targetVolume = settings.VolumeFraction * _referenceVolume;
            }
            Scaling = BuildScaling();
        }

        private bool UseThicknessFilter =>
            Settings.Mode == BesoMode.Thickness && Settings.ThicknessFilter && Settings.FilterRadius > 0;

        private double[] _volumeWeight;   // dV_design/dx_j (filtered: column sums of area-weighted W)

        /// <summary>
        /// Wall thickness [mm] for a vector of design variables. With the thickness filter this is the
        /// filtered field (frozen faces keep their own value); otherwise the design itself.
        /// </summary>
        public double[] Physical(double[] design)
        {
            if (!UseThicknessFilter) return design;
            var t = _filter.Apply(design);
            for (int e = 0; e < t.Length; e++) if (_model.Frozen[e]) t[e] = design[e];
            return t;
        }

        private double[] VolumeWeights()
        {
            if (_volumeWeight != null) return _volumeWeight;
            int m = _model.ElementCount;
            var w = new double[m];
            if (UseThicknessFilter)
            {
                var rowArea = new double[m];
                foreach (int e in _design) rowArea[e] = _model.Elements[e].Area;
                w = _filter.ApplyTranspose(rowArea);
            }
            else
            {
                foreach (int e in _design) w[e] = _model.Elements[e].Area;
            }
            return _volumeWeight = w;
        }

        /// <summary>Thickness mode: average wall thickness the design converges to [mm].</summary>
        public double TargetAverageThickness => _targetVolume / _designArea;

        private double ReferenceThickness(int e) =>
            Settings.Mode == BesoMode.Thickness ? Settings.MaxThickness : _model.Thickness[e];

        /// <summary>Volume of the design elements for the current design [mm^3].</summary>
        public double DesignVolume
        {
            get
            {
                var t = Settings.Mode == BesoMode.Thickness ? Physical(Design) : Design;
                return _design.Sum(e => ElementVolume(e, t[e]));
            }
        }

        public double CurrentVolumeFraction => DesignVolume / _referenceVolume;

        private double ElementVolume(int e, double x) =>
            _model.Elements[e].Area * (Settings.Mode == BesoMode.Thickness ? x : (x >= 1 ? _model.Thickness[e] : 0));

        /// <summary>Effective wall thickness per element for downstream use (0 for voids).</summary>
        public double[] EffectiveThickness() => EffectiveThickness(Design);

        /// <summary>Effective wall thickness of any design snapshot, e.g. from <see cref="DesignHistory"/>.</summary>
        public double[] EffectiveThickness(double[] design)
        {
            var t = new double[_model.ElementCount];
            for (int e = 0; e < t.Length; e++)
                t[e] = Settings.Mode == BesoMode.Thickness ? 0 : (design[e] >= 1 ? _model.Thickness[e] : 0);
            return Settings.Mode == BesoMode.Thickness ? (double[])Physical(design).Clone() : t;
        }

        /// <summary>Solid/void flag per element (all true in thickness mode).</summary>
        public bool[] Solid() => Solid(Design);

        public bool[] Solid(double[] design) => design.Select(x => Settings.Mode == BesoMode.Thickness || x >= 1).ToArray();

        /// <summary>
        /// The design analysed at each iteration (index i matches <see cref="ComplianceHistory"/>[i]),
        /// for playing back the optimisation.
        /// </summary>
        public List<double[]> DesignHistory { get; } = new List<double[]>();

        /// <summary>
        /// Design at a point of the run: 0 = first iteration, 1 = the current (final) design.
        /// </summary>
        public double[] DesignAt(double fraction, out int iteration)
        {
            if (fraction >= 1 || DesignHistory.Count == 0)
            {
                iteration = Iteration;
                return Design;
            }
            int i = (int)Math.Round(Math.Max(0, fraction) * (DesignHistory.Count - 1));
            iteration = i + 1;
            return DesignHistory[i];
        }

        private ElementScaling BuildScaling() => ScalingFor(Design);

        /// <summary>Stiffness, weight and across-layer factors for any design vector.</summary>
        public ElementScaling ScalingFor(double[] design)
        {
            int m = _model.ElementCount;
            var s = new ElementScaling(m);
            var tp = Settings.Mode == BesoMode.Thickness ? Physical(design) : null;
            for (int e = 0; e < m; e++)
            {
                if (Settings.Mode == BesoMode.Thickness)
                {
                    s.SetThickness(e, tp[e]);
                    if (_rule != null) s.Across[e] = Kappa(tp[e]);
                }
                else
                {
                    double t = _model.Thickness[e];
                    s.Membrane[e] = VoidStiffness(design[e]) * t;
                    s.Bending[e] = VoidStiffness(design[e], bending: true) * t * t * t;
                    s.Weight[e] = design[e] * t;
                    s.Thickness[e] = t;
                    if (_rule != null) s.Across[e] = Kappa(t);
                }
            }
            return s;
        }

        /// <summary>
        /// Void mode stiffness factors (Huang &amp; Xie 2011, self-weight loads):
        /// k(x) = c (1 - x^p) + x^p with c chosen so that k(1) = 1 and k(x_min) = k_min. A removed face
        /// behaves like a wall x_min times as thick, like Karamba's soft-kill: membrane k_min = x_min (as
        /// light as it is stiff, since the weight factor is x) and bending k_min = x_min^3 (for p = 3 that
        /// is plain x^3). With plain x^p for the membrane, removed faces were x_min^(p-1) = 10^6 times
        /// floppier than their weight and sagged far enough under gravity to dominate the compliance.
        /// </summary>
        /// <param name="bending">false = membrane factor, true = bending factor.</param>
        public double VoidStiffness(double x, bool bending = false)
        {
            double xp = Math.Pow(x, Settings.Penalty), c = VoidOffset(bending);
            return c * (1 - xp) + xp;
        }

        /// <summary>dk/dx of <see cref="VoidStiffness"/>.</summary>
        public double VoidStiffnessSlope(double x, bool bending = false) =>
            (1 - VoidOffset(bending)) * Settings.Penalty * Math.Pow(x, Settings.Penalty - 1);

        private double VoidOffset(bool bending)
        {
            double p = Settings.Penalty, xmin = Settings.MinDensity, xmp = Math.Pow(xmin, p);
            double kmin = bending ? xmin * xmin * xmin : xmin;
            return (kmin - xmp) / (1 - xmp);
        }

        // ---------------------------------------------------------------- printed layers

        private readonly LayerRule _rule;   // resolved; null = isotropic wall

        /// <summary>The resolved layer rule BESO uses for h(t), or null when layers are ignored.</summary>
        public LayerRule Rule => _rule;

        /// <summary>Across-layer stiffness factor kappa(t) = eta * c(t, h(t)), c = (t - h)/t.</summary>
        public double Kappa(double t) => Settings.Bond.Efficiency * Settings.Bond.Contact(t, _rule.Height(t));

        /// <summary>d kappa / dt by central difference (h(t) is piecewise linear or smooth).</summary>
        private double KappaSlope(double t)
        {
            double d = 1e-6 * Math.Max(1.0, t);
            return (Kappa(t + d) - Kappa(t - d)) / (2 * d);
        }

        /// <summary>Run one analysis + update. Returns false once finished.</summary>
        public bool Step()
        {
            if (Finished) return false;

            DesignHistory.Add((double[])Design.Clone());
            var r = _solver.Solve(Scaling);
            LastResult = r;
            ComplianceHistory.Add(r.Compliance);
            VolumeFractionHistory.Add(CurrentVolumeFraction);

            // Void mode ranks faces by stiffness only (see RawSensitivity); the analysis still has the weight.
            var filtered = Settings.Mode == BesoMode.Void
                ? _filter.Apply(RawSensitivity(r, includeWeight: false))
                : DesignSensitivity(r, out _);
            if (_previousSensitivity != null && Settings.EffectiveHistoryAveraging)
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
        /// <paramref name="includeWeight"/> = false drops the weight term (void mode ranking): switching a
        /// whole face off is far outside the range where this linear estimate holds, and with the weight
        /// term the void update cut the load path to a rim load, leaving it hanging on removed faces
        /// (compliance up 2-4x instead of down; tools/KarambaCheck). Thickness mode changes the wall in
        /// small steps and keeps the exact gradient.
        /// </summary>
        public double[] RawSensitivity(AnalysisResult r, bool includeWeight = true)
        {
            int m = _model.ElementCount;
            var a = new double[m];
            var tp = Settings.Mode == BesoMode.Thickness ? Physical(Design) : null;
            for (int e = 0; e < m; e++)
            {
                double area = _model.Elements[e].Area;
                double em = r.MembraneWork[e], eb = r.BendingWork[e], wu = includeWeight ? r.WeightWork[e] : 0;
                if (Settings.Mode == BesoMode.Thickness)
                {
                    double t = tp[e];
                    // fm = t, fb = t^3, fw = t ; dV/dt = A. With printed layers the stiffness is
                    // t (Km + (k-1) Km1) + t^3 (Kb + (k-1) Kb1) with k = kappa(t), which adds
                    // k'(t) (t u'Km1u + t^3 u'Kb1u). em, eb are already the effective works.
                    double layered = _rule == null ? 0
                        : KappaSlope(t) * (t * r.AcrossMembraneWork[e] + t * t * t * r.AcrossBendingWork[e]);
                    a[e] = (em + 3 * t * t * eb + layered - 2 * wu) / area;
                }
                else
                {
                    double t = _model.Thickness[e];
                    double dkm = VoidStiffnessSlope(Design[e]), dkb = VoidStiffnessSlope(Design[e], bending: true);
                    // fm = km(x) t, fb = kb(x) t^3, fw = x t ; dV/dx = A t
                    a[e] = (dkm * t * em + dkb * t * t * t * eb - 2 * t * wu) / (area * t);
                }
            }
            return a;
        }

        /// <summary>
        /// Sensitivity per unit volume with respect to the DESIGN variables (what the update uses), and
        /// dV/dx per variable. With the thickness filter: dC/dx = W^T dC/dt and dV/dx = W^T A.
        /// Without it, the classic filtered sensitivities.
        /// </summary>
        public double[] DesignSensitivity(AnalysisResult r, out double[] volumeWeight)
        {
            volumeWeight = VolumeWeights();
            var raw = RawSensitivity(r);
            if (!UseThicknessFilter) return _filter.Apply(raw);
            int m = _model.ElementCount;
            var g = new double[m];                    // -dC/dt on the design rows
            foreach (int e in _design) g[e] = raw[e] * _model.Elements[e].Area;
            var gx = _filter.ApplyTranspose(g);       // -dC/dx
            var alpha = new double[m];
            foreach (int j in _design) alpha[j] = volumeWeight[j] > 0 ? gx[j] / volumeWeight[j] : 0;
            return alpha;
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
            int n = Settings.EffectiveConvergenceWindow;
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
            double span = st.MaxThickness - st.MinThickness;
            double move = st.MoveLimit * span;
            int nd = _design.Length;
            var t0 = _design.Select(e => Design[e]).ToArray();
            var w = VolumeWeights();
            // Volume of the design faces = (contribution of frozen variables) + sum_j w_j x_j.
            double fixedPart = Enumerable.Range(0, _model.ElementCount).Where(j => _model.Frozen[j]).Sum(j => w[j] * Design[j]);
            var lo = new double[nd];
            var hi = new double[nd];
            for (int k = 0; k < nd; k++)
            {
                lo[k] = Math.Max(st.MinThickness, t0[k] - move);
                hi[k] = Math.Min(st.MaxThickness, t0[k] + move);
            }

            // The update scales each face by (alpha/lambda)^eta, so faces with alpha <= 0 can only
            // shrink. Under self-weight a lightly loaded face can have alpha < 0 (it weighs more than
            // it stiffens). If the other faces can't take the target volume even at their upper
            // bounds, the update would silently lose clay. In that case shift every sensitivity by a
            // constant: the optimality condition (equal alpha on all faces between the bounds) is
            // unchanged, but the volume can be met again.
            double aMin = _design.Min(e => alpha[e]);
            double aMax = _design.Max(e => alpha[e]);
            double reachable = 0;
            for (int k = 0; k < nd; k++)
                reachable += w[_design[k]] * (alpha[_design[k]] > 0 ? hi[k] : lo[k]);
            if (fixedPart + reachable < targetVolume * (1 - 1e-9) && aMax > aMin)
            {
                double shift = -aMin + 0.05 * (aMax - aMin);
                alpha = (double[])alpha.Clone();
                foreach (int e in _design) alpha[e] += shift;
                aMax += shift;
            }
            if (aMax <= 0) return; // all sensitivities equal and non-positive: nothing to rank

            double Volume(double lambda)
            {
                double v = fixedPart;
                for (int k = 0; k < _design.Length; k++)
                {
                    int e = _design[k];
                    double ratio = Math.Max(alpha[e], 0) / lambda;
                    double t = Math.Min(hi[k], Math.Max(lo[k], t0[k] * Math.Pow(ratio, st.Damping)));
                    v += w[e] * t;
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
