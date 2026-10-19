using System;
using System.Diagnostics;
using CSparse;
using CSparse.Double.Factorization;
using CSparse.Storage;

namespace ClayBeso.Fem
{
    /// <summary>
    /// Sparse global stiffness for a fixed mesh/support layout. The sparsity pattern and the
    /// symbolic Cholesky analysis are built once; each optimisation iteration only rewrites
    /// the values and refactorises numerically.
    /// </summary>
    internal sealed class LinearSystem
    {
        private readonly ShellModel _model;
        private readonly int[] _eq;          // global dof -> equation index, -1 if fixed
        private readonly int[][] _slot;      // per element: 18x18 -> index into Values, -1 if unused
        private readonly CompressedColumnStorage<double> _k;
        private SparseCholesky _chol;

        public int EquationCount { get; }

        /// <summary>Non-zeros in the Cholesky factor L (0 before the first factorisation).</summary>
        public int FactorNonZeros => _chol?.NonZerosCount ?? 0;

        /// <summary>Non-zeros stored in the upper triangle of K.</summary>
        public int MatrixNonZeros => _k.NonZerosCount;

        public LinearSystem(ShellModel model)
        {
            _model = model;
            int nd = 6 * model.NodeCount;
            _eq = new int[nd];
            int neq = 0;
            for (int d = 0; d < nd; d++) _eq[d] = model.Fixed[d] ? -1 : neq++;
            EquationCount = neq;
            if (neq == 0) throw new InvalidOperationException("All DOFs are fixed.");

            // Pattern: upper triangle only (CSparse's Cholesky reads the upper part).
            var coo = new CoordinateStorage<double>(neq, neq, model.ElementCount * 171);
            foreach (var el in model.Elements)
            {
                var g = ElementEquations(el);
                for (int i = 0; i < 18; i++)
                for (int j = 0; j < 18; j++)
                    if (g[i] >= 0 && g[j] >= 0 && g[i] <= g[j])
                        coo.At(g[i], g[j], 1.0);
            }
            _k = CSparse.Double.SparseMatrix.OfIndexed(coo);

            var cp = _k.ColumnPointers;
            var ri = _k.RowIndices;
            for (int c = 0; c < neq; c++) Array.Sort(ri, cp[c], cp[c + 1] - cp[c]);

            _slot = new int[model.ElementCount][];
            for (int e = 0; e < model.ElementCount; e++)
            {
                var g = ElementEquations(model.Elements[e]);
                var s = new int[324];
                for (int i = 0; i < 18; i++)
                for (int j = 0; j < 18; j++)
                {
                    int r = g[i], c = g[j];
                    s[i * 18 + j] = r >= 0 && c >= 0 && r <= c
                        ? Array.BinarySearch(ri, cp[c], cp[c + 1] - cp[c], r)
                        : -1;
                }
                _slot[e] = s;
            }
        }

        public int[] ElementEquations(ShellElement el)
        {
            var g = new int[18];
            for (int a = 0; a < 3; a++)
            for (int d = 0; d < 6; d++)
                g[6 * a + d] = _eq[6 * el.Nodes[a] + d];
            return g;
        }

        /// <summary>K = sum_e fm[e] * Km_e + fb[e] * Kb_e, then factorise.</summary>
        /// <summary>K = sum_e fm[e] * Km_e + fb[e] * Kb_e (values only; the pattern is fixed).</summary>
        public void Assemble(double[] fm, double[] fb, double[] across = null)
        {
            var vals = _k.Values;
            Array.Clear(vals, 0, vals.Length);
            var els = _model.Elements;
            for (int e = 0; e < els.Length; e++)
            {
                var s = _slot[e];
                var km = els[e].Km;
                var kb = els[e].Kb;
                double a = fm[e], b = fb[e];
                double dk = across == null ? 0 : across[e] - 1;
                var km1 = els[e].Km1;
                var kb1 = els[e].Kb1;
                if (dk == 0 || km1 == null)
                {
                    for (int k = 0; k < 324; k++)
                        if (s[k] >= 0) vals[s[k]] += a * km[k] + b * kb[k];
                }
                else
                {
                    for (int k = 0; k < 324; k++)
                        if (s[k] >= 0) vals[s[k]] += a * (km[k] + dk * km1[k]) + b * (kb[k] + dk * kb1[k]);
                }
            }
            _factorCurrent = false;
        }

        /// <summary>Cholesky factorisation of the assembled matrix (symbolic analysis reused).</summary>
        public void Factor()
        {
            var sw = Stopwatch.StartNew();
            if (_chol == null)
                _chol = SparseCholesky.Create(_k, ColumnOrdering.MinimumDegreeAtPlusA);
            else
                _chol.Refactorize(_k);
            _factorCurrent = true;
            _refactorNext = false;
            LastFactorMs = sw.Elapsed.TotalMilliseconds;
        }

        // ------------------------------------------------------------------ reanalysis
        //
        // During optimisation K changes a little each iteration. Instead of refactorising
        // (the dominant cost), the last factorisation preconditions a conjugate-gradient solve
        // of the new K. That costs one triangular solve per CG iteration and typically needs a
        // handful of iterations. The matrix is refactorised once CG gets as slow as a fresh
        // factorisation would have been.

        private bool _factorCurrent;   // _chol is the factorisation of the current values
        private bool _refactorNext;
        private double[] _lastX;

        /// <summary>Reuse an older factorisation as a CG preconditioner when K has changed.</summary>
        public bool ReuseFactorisation { get; set; } = true;

        /// <summary>Relative residual |K x - b| / |b| that CG must reach.</summary>
        public double Tolerance { get; set; } = 1e-10;

        public int MaxIterations { get; set; } = 200;

        public int LastIterations { get; private set; }
        public bool LastRefactorised { get; private set; }
        public double LastFactorMs { get; private set; }
        public int FactorisationCount { get; private set; }

        /// <summary>Solve for the full (6 per node) displacement vector given the full load vector.</summary>
        public double[] Solve(double[] fullLoad)
        {
            var b = new double[EquationCount];
            for (int d = 0; d < _eq.Length; d++)
                if (_eq[d] >= 0) b[_eq[d]] = fullLoad[d];
            var x = new double[EquationCount];

            LastIterations = 0;
            LastRefactorised = false;
            bool solved = false;
            if (!_factorCurrent && _chol != null && ReuseFactorisation && !_refactorNext)
            {
                var sw = Stopwatch.StartNew();
                solved = Pcg(b, x);
                // Once CG costs about as much as refactorising, refactorise next time.
                if (solved && sw.Elapsed.TotalMilliseconds > 0.5 * LastFactorMs) _refactorNext = true;
            }
            if (!solved)
            {
                if (!_factorCurrent)
                {
                    Factor();
                    FactorisationCount++;
                    LastRefactorised = true;
                }
                _chol.Solve(b, x);
            }
            _lastX = x;

            var u = new double[_eq.Length];
            for (int d = 0; d < _eq.Length; d++)
                if (_eq[d] >= 0) u[d] = x[_eq[d]];
            return u;
        }

        /// <summary>Preconditioned CG, warm-started from the previous solution. False if it did not converge.</summary>
        private bool Pcg(double[] b, double[] x)
        {
            int n = b.Length;
            double bnorm = Math.Sqrt(Dot(b, b));
            if (bnorm == 0) return true;

            var r = new double[n];
            if (_lastX != null && _lastX.Length == n)
            {
                Array.Copy(_lastX, x, n);
                MultiplyK(x, r);
                for (int i = 0; i < n; i++) r[i] = b[i] - r[i];
            }
            else
            {
                Array.Copy(b, r, n);
            }
            if (Math.Sqrt(Dot(r, r)) <= Tolerance * bnorm) return true;

            var z = new double[n];
            var q = new double[n];
            _chol.Solve(r, z);
            var p = (double[])z.Clone();
            double rz = Dot(r, z);

            for (int k = 1; k <= MaxIterations; k++)
            {
                MultiplyK(p, q);
                double pq = Dot(p, q);
                if (pq <= 0) return false; // lost positive definiteness: fall back to a fresh factor
                double alpha = rz / pq;
                for (int i = 0; i < n; i++) { x[i] += alpha * p[i]; r[i] -= alpha * q[i]; }
                LastIterations = k;
                if (Math.Sqrt(Dot(r, r)) <= Tolerance * bnorm) return true;

                _chol.Solve(r, z);
                double rzNew = Dot(r, z);
                double beta = rzNew / rz;
                rz = rzNew;
                for (int i = 0; i < n; i++) p[i] = z[i] + beta * p[i];
            }
            return false;
        }

        /// <summary>y = K x using the stored upper triangle.</summary>
        private void MultiplyK(double[] x, double[] y)
        {
            Array.Clear(y, 0, y.Length);
            var cp = _k.ColumnPointers;
            var ri = _k.RowIndices;
            var v = _k.Values;
            for (int j = 0; j < EquationCount; j++)
            {
                double xj = x[j], sum = 0;
                for (int k = cp[j]; k < cp[j + 1]; k++)
                {
                    int i = ri[k];
                    y[i] += v[k] * xj;
                    if (i != j) sum += v[k] * x[i];
                }
                y[j] += sum;
            }
        }

        private static double Dot(double[] a, double[] b)
        {
            double s = 0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }
    }
}
