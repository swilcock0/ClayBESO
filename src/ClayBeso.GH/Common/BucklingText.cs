using System;
using System.Linq;
using ClayBeso.Geometry;
using Grasshopper.Kernel;

namespace ClayBeso.GH.Common
{
    /// <summary>Shared wording, warnings and preview scaling for buckling results.</summary>
    internal static class BucklingText
    {
        /// <summary>
        /// Below this the linear buckling factor is flagged. Real walls are never perfect, and thin shells
        /// buckle at a fraction of the linear prediction; 3 is a placeholder margin until it is calibrated
        /// with docs/VALIDATION.md section 5 (collapse height).
        /// </summary>
        public const double Margin = 3.0;

        public static string Factor(double f) =>
            double.IsNaN(f) ? "not computed"
            : double.IsPositiveInfinity(f) ? "none (nothing in compression)"
            : $"{f:G3}";

        public static void Warn(GH_Component c, double f, string where)
        {
            if (double.IsNaN(f) || double.IsInfinity(f)) return;
            if (f < 1)
                c.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Buckling factor {f:G3} < 1 ({where}): the shell is predicted to buckle under these loads.");
            else if (f < Margin)
                c.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"Buckling factor {f:G3} ({where}) is below {Margin:G2}. This assumes a perfect shape; real walls, " +
                    "wet clay especially, buckle well below it. Treat it as at risk.");
        }

        /// <summary>Displacement [mm] that draws a mode (largest = 1) with Deform Scale 10 at 5 % of the model size.</summary>
        public static double Amplitude(TriMesh mesh)
        {
            var v = mesh.Vertices;
            double dx = v.Max(p => p.X) - v.Min(p => p.X);
            double dy = v.Max(p => p.Y) - v.Min(p => p.Y);
            double dz = v.Max(p => p.Z) - v.Min(p => p.Z);
            return 0.005 * Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Per face: average mode movement of its corners (0-1); NaN without a mode.</summary>
        public static double[] FaceValues(TriMesh mesh, Vec3[] mode) =>
            mesh.Faces.Select(f => mode == null ? double.NaN : f.Average(v => mode[v].Length)).ToArray();
    }
}
