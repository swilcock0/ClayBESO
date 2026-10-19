using System;
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
        public void AssembleAndFactor(double[] fm, double[] fb)
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
                for (int k = 0; k < 324; k++)
                    if (s[k] >= 0) vals[s[k]] += a * km[k] + b * kb[k];
            }

            if (_chol == null)
                _chol = SparseCholesky.Create(_k, ColumnOrdering.MinimumDegreeAtPlusA);
            else
                _chol.Refactorize(_k);
        }

        /// <summary>Solve for the full (6 per node) displacement vector given the full load vector.</summary>
        public double[] Solve(double[] fullLoad)
        {
            var b = new double[EquationCount];
            for (int d = 0; d < _eq.Length; d++)
                if (_eq[d] >= 0) b[_eq[d]] = fullLoad[d];
            var x = new double[EquationCount];
            _chol.Solve(b, x);
            var u = new double[_eq.Length];
            for (int d = 0; d < _eq.Length; d++)
                if (_eq[d] >= 0) u[d] = x[_eq[d]];
            return u;
        }
    }
}
