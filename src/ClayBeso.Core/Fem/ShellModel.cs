using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Geometry;

namespace ClayBeso.Fem
{
    /// <summary>Linear-elastic isotropic material. Units: N, mm, MPa.</summary>
    public sealed class ClayMaterial
    {
        /// <summary>Young's modulus [MPa]. Green (wet) clay is roughly 0.1-5 MPa, fired 10-30 GPa.</summary>
        public double E { get; }
        public double Nu { get; }

        /// <summary>Density [kg/m^3].</summary>
        public double Density { get; }

        /// <summary>Allowable stress used for reporting utilisation [MPa]. 0 = not set.</summary>
        public double Strength { get; }

        public ClayMaterial(double e, double nu, double density, double strength = 0)
        {
            if (e <= 0) throw new ArgumentException("E must be positive.");
            if (nu < 0 || nu >= 0.5) throw new ArgumentException("Poisson's ratio must be in [0, 0.5).");
            E = e; Nu = nu; Density = density; Strength = strength;
        }

        /// <summary>Unit weight [N/mm^3] under standard gravity.</summary>
        public double UnitWeight => Density * 9.80665e-9;

        public override string ToString() =>
            $"Material (E = {E:G4} MPa, nu = {Nu:G3}, rho = {Density:G4} kg/m3" +
            (Strength > 0 ? $", f = {Strength:G3} MPa)" : ")");
    }

    /// <summary>
    /// A shell analysis model: triangulated mid-surface, material, supports and loads.
    /// Geometry is in millimetres; <see cref="UnitScale"/> records the factor from the
    /// source document units so results can be mapped back.
    /// </summary>
    public sealed class ShellModel
    {
        public TriMesh Mesh { get; }
        public ClayMaterial Material { get; }
        public ShellElement[] Elements { get; }

        /// <summary>Fixed DOFs, 6 per node (ux, uy, uz, rx, ry, rz).</summary>
        public bool[] Fixed { get; }

        /// <summary>Design-independent nodal loads [N, N*mm], 6 per node.</summary>
        public double[] FixedLoads { get; }

        /// <summary>Self-weight nodal force per element at unit thickness [N/mm] (same on all 3 nodes).</summary>
        public Vec3[] UnitSelfWeight { get; }

        /// <summary>Initial / reference thickness per element [mm].</summary>
        public double[] Thickness { get; }

        /// <summary>Non-design elements: the optimiser leaves them at their initial thickness.</summary>
        public bool[] Frozen { get; }

        /// <summary>Document units -> mm.</summary>
        public double UnitScale { get; set; } = 1.0;

        public bool HasSelfWeight { get; }

        /// <summary>Gravity direction scaled by the gravity factor (zero when self-weight is off).</summary>
        public Vec3 Gravity { get; }

        public int NodeCount => Mesh.VertexCount;
        public int ElementCount => Elements.Length;

        public ShellModel(
            TriMesh mesh,
            ClayMaterial material,
            IList<double> thickness,
            bool[] fixedDofs,
            double[] fixedLoads,
            Vec3 gravity,
            bool[] frozen = null)
        {
            Mesh = mesh;
            Material = material;
            int n = mesh.VertexCount, m = mesh.FaceCount;

            if (thickness.Count != 1 && thickness.Count != m)
                throw new ArgumentException("Thickness must have one value or one per face.");
            Thickness = new double[m];
            for (int e = 0; e < m; e++)
            {
                Thickness[e] = thickness.Count == 1 ? thickness[0] : thickness[e];
                if (Thickness[e] <= 0) throw new ArgumentException("Thickness must be positive.");
            }

            Fixed = fixedDofs ?? new bool[6 * n];
            FixedLoads = fixedLoads ?? new double[6 * n];
            if (Fixed.Length != 6 * n || FixedLoads.Length != 6 * n)
                throw new ArgumentException("Support and load arrays must have 6 entries per node.");
            Frozen = frozen ?? new bool[m];

            Elements = new ShellElement[m];
            UnitSelfWeight = new Vec3[m];
            for (int e = 0; e < m; e++)
            {
                var f = mesh.Faces[e];
                Elements[e] = new ShellElement(
                    mesh.Vertices[f[0]], mesh.Vertices[f[1]], mesh.Vertices[f[2]], f, material.E, material.Nu);
                UnitSelfWeight[e] = gravity * (material.UnitWeight * Elements[e].Area / 3.0);
            }
            HasSelfWeight = gravity.Length > 0 && material.UnitWeight > 0;
            Gravity = gravity;

            // Nodes not attached to any element would make K singular.
            var used = new bool[n];
            foreach (var f in mesh.Faces) foreach (int v in f) used[v] = true;
            for (int v = 0; v < n; v++)
                if (!used[v]) for (int d = 0; d < 6; d++) Fixed[6 * v + d] = true;

            if (!Fixed.Where((fx, i) => used[i / 6]).Any(fx => fx))
                throw new ArgumentException("The model has no supports.");
        }

        /// <summary>True once <see cref="EnableLayerAnisotropy"/> has run: elements carry across-layer stiffness parts.</summary>
        public bool IsLayered { get; private set; }

        private readonly object _layerLock = new object();

        /// <summary>
        /// Give every element horizontal layer axes, so that an across-layer factor kappa
        /// (<see cref="ElementScaling.Across"/>) can weaken the wall across its layers. With all kappa = 1
        /// the results are unchanged. Safe to call more than once.
        /// </summary>
        public void EnableLayerAnisotropy(Vec3 up)
        {
            lock (_layerLock)
            {
                if (IsLayered) return;
                foreach (var el in Elements) el.EnableLayerAxes(up);
                IsLayered = true;
            }
        }

        public double TotalVolume(IList<double> thickness)
        {
            double v = 0;
            for (int e = 0; e < ElementCount; e++) v += Elements[e].Area * thickness[e];
            return v;
        }

        public override string ToString() =>
            $"Shell model: {NodeCount} nodes, {ElementCount} triangles, " +
            $"{Fixed.Count(f => f)} fixed DOFs, self-weight {(HasSelfWeight ? "on" : "off")}";
    }
}
