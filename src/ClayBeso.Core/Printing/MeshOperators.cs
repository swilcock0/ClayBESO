using System;
using System.Collections.Generic;
using CSparse;
using CSparse.Double.Factorization;
using CSparse.Storage;
using ClayBeso.Geometry;

namespace ClayBeso.Printing
{
    /// <summary>Piecewise-linear (P1) operators on a triangle mesh.</summary>
    internal sealed class MeshOperators
    {
        private readonly TriMesh _mesh;

        /// <summary>Gradient of the three hat functions on each face.</summary>
        public Vec3[][] HatGradients { get; }

        public MeshOperators(TriMesh mesh)
        {
            _mesh = mesh;
            var normals = mesh.FaceNormals;
            var areas = mesh.FaceAreas;
            HatGradients = new Vec3[mesh.FaceCount][];
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                var t = mesh.Faces[f];
                var g = new Vec3[3];
                for (int k = 0; k < 3; k++)
                {
                    // grad N_k = n x e_k / (2A), e_k = edge opposite vertex k, CCW
                    var e = mesh.Vertices[t[(k + 2) % 3]] - mesh.Vertices[t[(k + 1) % 3]];
                    g[k] = Vec3.Cross(normals[f], e) / (2 * areas[f]);
                }
                HatGradients[f] = g;
            }
        }

        public Vec3 FaceGradient(int f, IList<double> phi)
        {
            var t = _mesh.Faces[f];
            var g = HatGradients[f];
            return g[0] * phi[t[0]] + g[1] * phi[t[1]] + g[2] * phi[t[2]];
        }

        /// <summary>
        /// Minimise sum_f A_f |grad phi - target_f|^2 subject to phi = value on the Dirichlet
        /// vertices. target may be null (Laplace equation).
        /// </summary>
        public double[] SolveLeastSquaresGradient(IList<Vec3> target, IDictionary<int, double> dirichlet)
        {
            int n = _mesh.VertexCount;
            var map = new int[n];
            int nf = 0;
            for (int v = 0; v < n; v++) map[v] = dirichlet.ContainsKey(v) ? -1 : nf++;
            var phi = new double[n];
            foreach (var kv in dirichlet) phi[kv.Key] = kv.Value;
            if (nf == 0) return phi;

            var areas = _mesh.FaceAreas;
            var coo = new CoordinateStorage<double>(nf, nf, _mesh.FaceCount * 6);
            var rhs = new double[nf];
            for (int f = 0; f < _mesh.FaceCount; f++)
            {
                var t = _mesh.Faces[f];
                var g = HatGradients[f];
                double a = areas[f];
                for (int i = 0; i < 3; i++)
                {
                    int ri = map[t[i]];
                    if (ri < 0) continue;
                    if (target != null) rhs[ri] += a * Vec3.Dot(g[i], target[f]);
                    for (int j = 0; j < 3; j++)
                    {
                        double kij = a * Vec3.Dot(g[i], g[j]);
                        int cj = map[t[j]];
                        if (cj < 0) rhs[ri] -= kij * phi[t[j]];
                        else if (ri <= cj) coo.At(ri, cj, kij);
                    }
                }
            }

            // Small regularisation keeps isolated free islands (no Dirichlet path) solvable.
            double scale = 0;
            for (int f = 0; f < _mesh.FaceCount; f++) scale += areas[f];
            for (int i = 0; i < nf; i++) coo.At(i, i, 1e-12 * scale / n);

            var k = CSparse.Double.SparseMatrix.OfIndexed(coo);
            var chol = SparseCholesky.Create(k, ColumnOrdering.MinimumDegreeAtPlusA);
            var x = new double[nf];
            chol.Solve(rhs, x);
            for (int v = 0; v < n; v++) if (map[v] >= 0) phi[v] = x[map[v]];
            return phi;
        }
    }
}
