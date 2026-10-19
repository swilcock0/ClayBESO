using System;
using ClayBeso.Geometry;

namespace ClayBeso.Fem
{
    /// <summary>
    /// Flat triangular shell: CST membrane + DKT (Batoz, Bathe &amp; Ho 1980) plate bending
    /// + a weak drilling spring. Six DOFs per node (ux, uy, uz, rx, ry, rz) in global axes.
    ///
    /// Stiffness is split by how it scales with thickness so the optimiser can rescale it
    /// without recomputing anything:  K(t) = fm * Km + fb * Kb,
    /// where Km is membrane + drilling at unit thickness (scales with t) and Kb is bending at
    /// unit thickness (scales with t^3).
    /// </summary>
    public sealed class ShellElement
    {
        public const int Dofs = 18;

        /// <summary>Drilling stiffness as a fraction of E*t*A.</summary>
        public const double DrillFactor = 1e-3;

        public int[] Nodes { get; }
        public double Area { get; }

        /// <summary>Local frame: rows are e1, e2, normal.</summary>
        public Vec3 E1 { get; }
        public Vec3 E2 { get; }
        public Vec3 Normal { get; }

        /// <summary>Local in-plane nodal coordinates.</summary>
        public double[] X { get; } = new double[3];
        public double[] Y { get; } = new double[3];

        /// <summary>Global 18x18 membrane+drilling stiffness at unit thickness (row-major).</summary>
        public double[] Km { get; }

        /// <summary>Global 18x18 bending stiffness at unit thickness (row-major).</summary>
        public double[] Kb { get; }

        private readonly double _e, _nu;

        public ShellElement(Vec3 p0, Vec3 p1, Vec3 p2, int[] nodes, double e, double nu)
        {
            Nodes = nodes;
            _e = e;
            _nu = nu;

            var a = p1 - p0;
            var b = p2 - p0;
            var n = Vec3.Cross(a, b);
            double twoA = n.Length;
            if (twoA <= 1e-14 * Math.Max(1.0, a.Length * b.Length))
                throw new ArgumentException("Degenerate (zero-area) triangle in shell mesh.");

            Area = 0.5 * twoA;
            E1 = a.Unit();
            Normal = n / twoA;
            E2 = Vec3.Cross(Normal, E1);

            var pts = new[] { p0, p1, p2 };
            for (int i = 0; i < 3; i++)
            {
                var d = pts[i] - p0;
                X[i] = Vec3.Dot(d, E1);
                Y[i] = Vec3.Dot(d, E2);
            }

            var kmLocal = new double[Dofs * Dofs];
            var kbLocal = new double[Dofs * Dofs];

            // Membrane (u, v) -> local dofs 0, 1 of each node.
            var kcst = CstStiffness(X, Y, Area, e, nu);
            for (int i = 0; i < 6; i++)
            for (int j = 0; j < 6; j++)
                kmLocal[MemDof(i) * Dofs + MemDof(j)] += kcst[i, j];

            // Drilling spring on rz (local dof 5): zero energy for a rigid in-plane rotation.
            double kd = DrillFactor * e * Area;
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                kmLocal[(6 * i + 5) * Dofs + (6 * j + 5)] += i == j ? kd : -0.5 * kd;

            // Bending (w, rx, ry) -> local dofs 2, 3, 4 of each node.
            var kdkt = DktStiffness(X, Y, Area, e, nu);
            for (int i = 0; i < 9; i++)
            for (int j = 0; j < 9; j++)
                kbLocal[BendDof(i) * Dofs + BendDof(j)] += kdkt[i, j];

            Km = ToGlobal(kmLocal);
            Kb = ToGlobal(kbLocal);
        }

        private static int MemDof(int i) => 6 * (i / 2) + (i % 2);
        private static int BendDof(int i) => 6 * (i / 3) + 2 + (i % 3);

        /// <summary>K_global = T^T K_local T with T = blockdiag(R) and R rows = (e1, e2, n).</summary>
        private double[] ToGlobal(double[] kLocal)
        {
            var r = new double[3, 3]
            {
                { E1.X, E1.Y, E1.Z },
                { E2.X, E2.Y, E2.Z },
                { Normal.X, Normal.Y, Normal.Z },
            };

            // tmp = K_local * T
            var tmp = new double[Dofs * Dofs];
            for (int i = 0; i < Dofs; i++)
            for (int bj = 0; bj < 6; bj++)
            for (int c = 0; c < 3; c++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += kLocal[i * Dofs + 3 * bj + k] * r[k, c];
                tmp[i * Dofs + 3 * bj + c] = s;
            }

            // K = T^T * tmp
            var kg = new double[Dofs * Dofs];
            for (int bi = 0; bi < 6; bi++)
            for (int c = 0; c < 3; c++)
            for (int j = 0; j < Dofs; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += r[k, c] * tmp[(3 * bi + k) * Dofs + j];
                kg[(3 * bi + c) * Dofs + j] = s;
            }
            return kg;
        }

        /// <summary>Rotate the global 18 element DOFs into the local frame.</summary>
        public double[] ToLocal(double[] uGlobal)
        {
            var ul = new double[Dofs];
            for (int b = 0; b < 6; b++)
            {
                double x = uGlobal[3 * b], y = uGlobal[3 * b + 1], z = uGlobal[3 * b + 2];
                ul[3 * b] = E1.X * x + E1.Y * y + E1.Z * z;
                ul[3 * b + 1] = E2.X * x + E2.Y * y + E2.Z * z;
                ul[3 * b + 2] = Normal.X * x + Normal.Y * y + Normal.Z * z;
            }
            return ul;
        }

        /// <summary>
        /// Membrane forces per unit width (N = [Nx, Ny, Nxy]) at unit thickness and moments per
        /// unit width (M = [Mx, My, Mxy]) at unit thickness, both in the local frame.
        /// Multiply by fm and fb respectively to get the actual resultants.
        /// </summary>
        public void UnitResultants(double[] uGlobal, out double[] n, out double[] m)
        {
            var ul = ToLocal(uGlobal);

            var um = new double[6];
            var ub = new double[9];
            for (int i = 0; i < 6; i++) um[i] = ul[MemDof(i)];
            for (int i = 0; i < 9; i++) ub[i] = ul[BendDof(i)];

            var bm = CstB(X, Y, Area);
            var eps = new double[3];
            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 6; c++)
                eps[r] += bm[r, c] * um[c];

            var bb = DktB(X, Y, 1.0 / 3.0, 1.0 / 3.0);
            var kap = new double[3];
            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 9; c++)
                kap[r] += bb[r, c] * ub[c];

            var dm = Constitutive(_e, _nu, 1.0);
            var db = Constitutive(_e, _nu, 1.0 / 12.0);
            n = Mul(dm, eps);
            m = Mul(db, kap);
        }

        private static double[] Mul(double[,] d, double[] v)
        {
            var r = new double[3];
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i] += d[i, j] * v[j];
            return r;
        }

        /// <summary>Plane-stress matrix scaled by <paramref name="scale"/>.</summary>
        public static double[,] Constitutive(double e, double nu, double scale)
        {
            double c = scale * e / (1 - nu * nu);
            return new double[3, 3]
            {
                { c, c * nu, 0 },
                { c * nu, c, 0 },
                { 0, 0, c * (1 - nu) / 2 },
            };
        }

        // ---------------------------------------------------------------- CST membrane

        public static double[,] CstB(double[] x, double[] y, double area)
        {
            double y23 = y[1] - y[2], y31 = y[2] - y[0], y12 = y[0] - y[1];
            double x32 = x[2] - x[1], x13 = x[0] - x[2], x21 = x[1] - x[0];
            double s = 1.0 / (2 * area);
            return new double[3, 6]
            {
                { s * y23, 0, s * y31, 0, s * y12, 0 },
                { 0, s * x32, 0, s * x13, 0, s * x21 },
                { s * x32, s * y23, s * x13, s * y31, s * x21, s * y12 },
            };
        }

        public static double[,] CstStiffness(double[] x, double[] y, double area, double e, double nu)
        {
            var b = CstB(x, y, area);
            var d = Constitutive(e, nu, 1.0);
            return BtDB(b, d, 6, area);
        }

        // ---------------------------------------------------------------- DKT bending

        /// <summary>
        /// DKT curvature matrix (3x9) at area coordinates (xi, eta). DOF order per node:
        /// (w, rx, ry) with right-handed rotations about local x and y, i.e. rx = dw/dy, ry = -dw/dx.
        /// </summary>
        public static double[,] DktB(double[] x, double[] y, double xi, double eta)
        {
            double x23 = x[1] - x[2], x31 = x[2] - x[0], x12 = x[0] - x[1];
            double y23 = y[1] - y[2], y31 = y[2] - y[0], y12 = y[0] - y[1];
            double l23 = x23 * x23 + y23 * y23;
            double l31 = x31 * x31 + y31 * y31;
            double l12 = x12 * x12 + y12 * y12;

            double p4 = -6 * x23 / l23, p5 = -6 * x31 / l31, p6 = -6 * x12 / l12;
            double t4 = -6 * y23 / l23, t5 = -6 * y31 / l31, t6 = -6 * y12 / l12;
            double q4 = 3 * x23 * y23 / l23, q5 = 3 * x31 * y31 / l31, q6 = 3 * x12 * y12 / l12;
            double r4 = 3 * y23 * y23 / l23, r5 = 3 * y31 * y31 / l31, r6 = 3 * y12 * y12 / l12;

            double a = 1 - 2 * xi, c = 1 - 2 * eta;

            var hxXi = new[]
            {
                p6 * a + (p5 - p6) * eta,
                q6 * a - (q5 + q6) * eta,
                -4 + 6 * (xi + eta) + r6 * a - eta * (r5 + r6),
                -p6 * a + eta * (p4 + p6),
                q6 * a - eta * (q6 - q4),
                -2 + 6 * xi + r6 * a + eta * (r4 - r6),
                -eta * (p5 + p4),
                eta * (q4 - q5),
                -eta * (r5 - r4),
            };
            var hyXi = new[]
            {
                t6 * a + (t5 - t6) * eta,
                1 + r6 * a - (r5 + r6) * eta,
                -q6 * a + eta * (q5 + q6),
                -t6 * a + eta * (t4 + t6),
                -1 + r6 * a + eta * (r4 - r6),
                -q6 * a - eta * (q4 - q6),
                -eta * (t5 + t4),
                eta * (r4 - r5),
                -eta * (q4 - q5),
            };
            var hxEta = new[]
            {
                -p5 * c - xi * (p6 - p5),
                q5 * c - xi * (q5 + q6),
                -4 + 6 * (xi + eta) + r5 * c - xi * (r5 + r6),
                xi * (p4 + p6),
                xi * (q4 - q6),
                -xi * (r6 - r4),
                p5 * c - xi * (p4 + p5),
                q5 * c + xi * (q4 - q5),
                -2 + 6 * eta + r5 * c + xi * (r4 - r5),
            };
            var hyEta = new[]
            {
                -t5 * c - xi * (t6 - t5),
                1 + r5 * c - xi * (r5 + r6),
                -q5 * c + xi * (q5 + q6),
                xi * (t4 + t6),
                xi * (r4 - r6),
                -xi * (q4 - q6),
                t5 * c - xi * (t4 + t5),
                -1 + r5 * c + xi * (r4 - r5),
                -q5 * c - xi * (q4 - q5),
            };

            double twoA = x31 * y12 - x12 * y31;
            var b = new double[3, 9];
            for (int i = 0; i < 9; i++)
            {
                b[0, i] = (y31 * hxXi[i] + y12 * hxEta[i]) / twoA;
                b[1, i] = (-x31 * hyXi[i] - x12 * hyEta[i]) / twoA;
                b[2, i] = (-x31 * hxXi[i] - x12 * hxEta[i] + y31 * hyXi[i] + y12 * hyEta[i]) / twoA;
            }
            return b;
        }

        private static readonly double[,] Gauss3 =
        {
            { 1.0 / 6.0, 1.0 / 6.0 },
            { 2.0 / 3.0, 1.0 / 6.0 },
            { 1.0 / 6.0, 2.0 / 3.0 },
        };

        /// <summary>DKT stiffness at unit thickness (bending rigidity E/(12(1-nu^2))).</summary>
        public static double[,] DktStiffness(double[] x, double[] y, double area, double e, double nu)
        {
            var d = Constitutive(e, nu, 1.0 / 12.0);
            var k = new double[9, 9];
            for (int g = 0; g < 3; g++)
            {
                var b = DktB(x, y, Gauss3[g, 0], Gauss3[g, 1]);
                // weight 1/6 on the reference triangle, Jacobian 2A -> area/3 per point
                var kg = BtDB(b, d, 9, area / 3.0);
                for (int i = 0; i < 9; i++)
                for (int j = 0; j < 9; j++)
                    k[i, j] += kg[i, j];
            }
            return k;
        }

        private static double[,] BtDB(double[,] b, double[,] d, int n, double scale)
        {
            var db = new double[3, n];
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += d[i, k] * b[k, j];
                db[i, j] = s;
            }
            var r = new double[n, n];
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += b[k, i] * db[k, j];
                r[i, j] = s * scale;
            }
            return r;
        }
    }
}
