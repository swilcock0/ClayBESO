using System;
using System.Collections.Generic;
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

        public ElementScaling(int count)
        {
            Membrane = new double[count];
            Bending = new double[count];
            Weight = new double[count];
            Thickness = new double[count];
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

    public sealed class AnalysisResult
    {
        /// <summary>Displacements and rotations, 6 per node [mm, rad].</summary>
        public double[] U { get; internal set; }

        /// <summary>Total load vector, 6 per node.</summary>
        public double[] F { get; internal set; }

        /// <summary>Compliance F.U [N*mm].</summary>
        public double Compliance { get; internal set; }

        /// <summary>u_e^T Km_e u_e per element (unit thickness, no 1/2).</summary>
        public double[] MembraneWork { get; internal set; }

        /// <summary>u_e^T Kb_e u_e per element (unit thickness, no 1/2).</summary>
        public double[] BendingWork { get; internal set; }

        /// <summary>u_e . w_e per element: work of the unit self-weight load.</summary>
        public double[] WeightWork { get; internal set; }

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

        public ShellSolver(ShellModel model)
        {
            _model = model;
            _system = new LinearSystem(model);
        }

        public AnalysisResult Solve(ElementScaling s)
        {
            int m = _model.ElementCount;
            _system.AssembleAndFactor(s.Membrane, s.Bending);

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
                var w = _model.UnitSelfWeight[e];
                double wu = 0;
                for (int a = 0; a < 3; a++)
                    wu += w.X * ue[6 * a] + w.Y * ue[6 * a + 1] + w.Z * ue[6 * a + 2];
                res.WeightWork[e] = wu;
            }
            return res;
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
                el.UnitResultants(ue, out var n, out var mo);
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
