using System;
using System.Collections.Generic;
using System.Diagnostics;
using ClayBeso.Geometry;

namespace ClayBeso.Fem
{
    /// <summary>
    /// How each element's stiffness and self-weight scale for the current design:
    /// K_e = Membrane[e] * Km_e + Bending[e] * Kb_e,   f_e = Weight[e] * w_e.
    /// For a plain shell of thickness t: Membrane = t, Bending = t^3, Weight = t.
    /// </summary>
    public sealed class ElementScaling
    {
        public double[] Membrane { get; }
        public double[] Bending { get; }
        public double[] Weight { get; }

        /// <summary>Physical wall thickness used for stress recovery [mm].</summary>
        public double[] Thickness { get; }

        /// <summary>
        /// Across-layer stiffness factor kappa per element (1 = isotropic). Only has an effect on a
        /// model with <see cref="ShellModel.EnableLayerAnisotropy"/>.
        /// </summary>
        public double[] Across { get; }

        public ElementScaling(int count)
        {
            Membrane = new double[count];
            Bending = new double[count];
            Weight = new double[count];
            Thickness = new double[count];
            Across = new double[count];
            for (int e = 0; e < count; e++) Across[e] = 1.0;
        }

        public static ElementScaling FromThickness(IList<double> t)
        {
            var s = new ElementScaling(t.Count);
            for (int e = 0; e < t.Count; e++) s.SetThickness(e, t[e]);
            return s;
        }

        public void SetThickness(int e, double t)
        {
            Membrane[e] = t;
            Bending[e] = t * t * t;
            Weight[e] = t;
            Thickness[e] = t;
        }
    }

    /// <summary>Per-phase timing of one linear solve [ms].</summary>
    public sealed class SolveTimings
    {
        public double Assembly { get; internal set; }
        public double Factorisation { get; internal set; }
        public double Substitution { get; internal set; }
        public double PostProcessing { get; internal set; }

        /// <summary>CG iterations preconditioned by an older factorisation (0 = direct solve).</summary>
        public int CgIterations { get; internal set; }

        /// <summary>True if this solve computed a new factorisation.</summary>
        public bool Refactorised { get; internal set; }

        /// <summary>Factorisations so far for this solver.</summary>
        public int FactorisationCount { get; internal set; }
        public double Total => Assembly + Factorisation + Substitution + PostProcessing;

        public override string ToString() =>
            $"assembly {Assembly:F0} ms, factorisation {Factorisation:F0} ms, " +
            $"solve {Substitution:F0} ms ({(Refactorised ? "new factor" : CgIterations + " CG it")}), post {PostProcessing:F0} ms";
    }

    public sealed class AnalysisResult
    {
        /// <summary>Displacements and rotations, 6 per node [mm, rad].</summary>
        public double[] U { get; internal set; }

        /// <summary>Total load vector, 6 per node.</summary>
        public double[] F { get; internal set; }

        /// <summary>Compliance F.U [N*mm].</summary>
        public double Compliance { get; internal set; }

        /// <summary>u_e^T Km_e u_e per element, with Km_e the effective membrane stiffness for the element's kappa (unit thickness, no 1/2).</summary>
        public double[] MembraneWork { get; internal set; }

        /// <summary>u_e^T Kb_e u_e per element, with Kb_e the effective bending stiffness for the element's kappa (unit thickness, no 1/2).</summary>
        public double[] BendingWork { get; internal set; }

        /// <summary>u_e . w_e per element: work of the unit self-weight load.</summary>
        public double[] WeightWork { get; internal set; }

        /// <summary>u_e^T Km1_e u_e per element: across-layer membrane part (0 if not layered).</summary>
        public double[] AcrossMembraneWork { get; internal set; }

        /// <summary>u_e^T Kb1_e u_e per element: across-layer bending part (0 if not layered).</summary>
        public double[] AcrossBendingWork { get; internal set; }

        public Vec3 Displacement(int node) => new Vec3(U[6 * node], U[6 * node + 1], U[6 * node + 2]);

        public double MaxDisplacement
        {
            get
            {
                double m = 0;
                for (int i = 0; i < U.Length / 6; i++) m = Math.Max(m, Displacement(i).Length);
                return m;
            }
        }
    }

    /// <summary>Linear static solver for a <see cref="ShellModel"/>.</summary>
    public sealed class ShellSolver
    {
        private readonly ShellModel _model;
        private readonly LinearSystem _system;

        public ShellModel Model => _model;
        public int EquationCount => _system.EquationCount;
        public int FactorNonZeros => _system.FactorNonZeros;

        /// <summary>
        /// Reuse the last factorisation as a CG preconditioner when the stiffness changes (optimisation
        /// loops). Results are identical to a direct solve to a relative residual of 1e-10.
        /// </summary>
        public bool ReuseFactorisation
        {
            get => _system.ReuseFactorisation;
            set => _system.ReuseFactorisation = value;
        }
        public int MatrixNonZeros => _system.MatrixNonZeros;

        /// <summary>Wall-clock time of each phase of the last <see cref="Solve"/> [ms].</summary>
        public SolveTimings LastTimings { get; } = new SolveTimings();

        public ShellSolver(ShellModel model)
        {
            _model = model;
            _system = new LinearSystem(model);
        }

        public AnalysisResult Solve(ElementScaling s)
        {
            var clock = Stopwatch.StartNew();
            int m = _model.ElementCount;
            _system.Assemble(s.Membrane, s.Bending, s.Across);
            LastTimings.Assembly = Lap(clock);

            var f = (double[])_model.FixedLoads.Clone();
            for (int e = 0; e < m; e++)
            {
                var w = _model.UnitSelfWeight[e] * s.Weight[e];
                foreach (int v in _model.Elements[e].Nodes)
                {
                    f[6 * v] += w.X;
                    f[6 * v + 1] += w.Y;
                    f[6 * v + 2] += w.Z;
                }
            }

            var u = _system.Solve(f);
            double solveMs = Lap(clock);
            LastTimings.Factorisation = _system.LastRefactorised ? _system.LastFactorMs : 0;
            LastTimings.Substitution = solveMs - LastTimings.Factorisation;
            LastTimings.CgIterations = _system.LastIterations;
            LastTimings.Refactorised = _system.LastRefactorised;
            LastTimings.FactorisationCount = _system.FactorisationCount;
            for (int d = 0; d < u.Length; d++)
                if (double.IsNaN(u[d]) || double.IsInfinity(u[d]))
                    throw new InvalidOperationException(
                        "Solver failed: the structure is a mechanism. Check the supports.");

            var res = new AnalysisResult
            {
                U = u,
                F = f,
                MembraneWork = new double[m],
                BendingWork = new double[m],
                WeightWork = new double[m],
                AcrossMembraneWork = new double[m],
                AcrossBendingWork = new double[m],
            };

            double c = 0;
            for (int d = 0; d < u.Length; d++) c += f[d] * u[d];
            res.Compliance = c;

            var ue = new double[18];
            for (int e = 0; e < m; e++)
            {
                var el = _model.Elements[e];
                Gather(el, u, ue);
                res.MembraneWork[e] = Quad(el.Km, ue);
                res.BendingWork[e] = Quad(el.Kb, ue);
                if (el.Km1 != null)
                {
                    // Work of the effective stiffness Km + (kappa - 1) Km1, plus the across-layer parts
                    // on their own for the kappa sensitivity.
                    double dk = s.Across[e] - 1;
                    double em1 = Quad(el.Km1, ue), eb1 = Quad(el.Kb1, ue);
                    res.AcrossMembraneWork[e] = em1;
                    res.AcrossBendingWork[e] = eb1;
                    res.MembraneWork[e] += dk * em1;
                    res.BendingWork[e] += dk * eb1;
                }
                var w = _model.UnitSelfWeight[e];
                double wu = 0;
                for (int a = 0; a < 3; a++)
                    wu += w.X * ue[6 * a] + w.Y * ue[6 * a + 1] + w.Z * ue[6 * a + 2];
                res.WeightWork[e] = wu;
            }
            LastTimings.PostProcessing = Lap(clock);
            return res;
        }

        private static double Lap(Stopwatch sw)
        {
            double ms = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            return ms;
        }

        public static void Gather(ShellElement el, double[] u, double[] ue)
        {
            for (int a = 0; a < 3; a++)
            for (int d = 0; d < 6; d++)
                ue[6 * a + d] = u[6 * el.Nodes[a] + d];
        }

        private static double Quad(double[] k, double[] u)
        {
            double s = 0;
            for (int i = 0; i < 18; i++)
            {
                double r = 0;
                int o = i * 18;
                for (int j = 0; j < 18; j++) r += k[o + j] * u[j];
                s += u[i] * r;
            }
            return s;
        }

        /// <summary>
        /// Von Mises of the membrane part (N/t) and of the bending part (6M/t^2) of one element's
        /// stress [MPa], to see which one drives it. Membrane stress scales with 1/t, bending with 1/t^2.
        /// </summary>
        public (double membrane, double bending) StressParts(AnalysisResult r, ElementScaling s, int e)
        {
            var ue = new double[18];
            var el = _model.Elements[e];
            Gather(el, r.U, ue);
            el.UnitResultants(ue, out var n, out var mo, s.Across[e]);
            double t = s.Thickness[e];
            double Vm(double a, double b, double c) => Math.Sqrt(Math.Max(0, a * a + b * b - a * b + 3 * c * c));
            double km = s.Membrane[e] / t, kb = 6 * s.Bending[e] / (t * t);
            return (Vm(km * n[0], km * n[1], km * n[2]), Vm(kb * mo[0], kb * mo[1], kb * mo[2]));
        }

        /// <summary>
        /// Von Mises stress per element [MPa], the larger of the top and bottom surfaces.
        /// </summary>
        public double[] VonMises(AnalysisResult r, ElementScaling s)
        {
            int m = _model.ElementCount;
            var vm = new double[m];
            var ue = new double[18];
            for (int e = 0; e < m; e++)
            {
                var el = _model.Elements[e];
                Gather(el, r.U, ue);
                el.UnitResultants(ue, out var n, out var mo, s.Across[e]);
                double t = s.Thickness[e];
                double best = 0;
                for (int side = -1; side <= 1; side += 2)
                {
                    double sx = s.Membrane[e] * n[0] / t + side * 6 * s.Bending[e] * mo[0] / (t * t);
                    double sy = s.Membrane[e] * n[1] / t + side * 6 * s.Bending[e] * mo[1] / (t * t);
                    double txy = s.Membrane[e] * n[2] / t + side * 6 * s.Bending[e] * mo[2] / (t * t);
                    double v = Math.Sqrt(Math.Max(0, sx * sx + sy * sy - sx * sy + 3 * txy * txy));
                    best = Math.Max(best, v);
                }
                vm[e] = best;
            }
            return vm;
        }
    }
}
