using System;
using ClayBeso.Geometry;

namespace ClayBeso.Fem
{
    /// <summary>
    /// Per-face principal in-plane forces of a shell analysis: the direction along which force
    /// "flows" through the wall (the force-flow lines of Vele, Kurilla &amp; Achten 2023 follow it).
    /// </summary>
    public sealed class ForceFlowField
    {
        /// <summary>Unit direction of the dominant principal membrane force (sign arbitrary).</summary>
        public Vec3[] Direction { get; internal set; }

        /// <summary>Dominant principal membrane force [N/mm], signed (negative = compression).</summary>
        public double[] Dominant { get; internal set; }

        /// <summary>The other principal membrane force [N/mm].</summary>
        public double[] Minor { get; internal set; }

        /// <summary>
        /// How directional the force is, 0..1: |N1 - N2| / (|N1| + |N2|). 0 = equal in all
        /// directions (no flow direction), 1 = uniaxial.
        /// </summary>
        public double[] Anisotropy { get; internal set; }
    }

    public static class ForceFlow
    {
        public static ForceFlowField Compute(ShellModel model, AnalysisResult r, ElementScaling s)
        {
            int m = model.ElementCount;
            var f = new ForceFlowField
            {
                Direction = new Vec3[m],
                Dominant = new double[m],
                Minor = new double[m],
                Anisotropy = new double[m],
            };
            var ue = new double[18];
            for (int e = 0; e < m; e++)
            {
                var el = model.Elements[e];
                ShellSolver.Gather(el, r.U, ue);
                el.UnitResultants(ue, out var n, out _, s.Across[e]);
                double fm = s.Membrane[e];
                Principal(fm * n[0], fm * n[1], fm * n[2], out double n1, out double n2, out double c, out double sn);
                bool firstDominates = Math.Abs(n1) >= Math.Abs(n2);
                // direction of n1 is (c, sn) in the local frame; n2 is perpendicular to it
                f.Direction[e] = firstDominates ? (el.E1 * c + el.E2 * sn).Unit() : (el.E1 * -sn + el.E2 * c).Unit();
                f.Dominant[e] = firstDominates ? n1 : n2;
                f.Minor[e] = firstDominates ? n2 : n1;
                double sum = Math.Abs(n1) + Math.Abs(n2);
                f.Anisotropy[e] = sum > 0 ? Math.Abs(n1 - n2) / sum : 0;
            }
            return f;
        }

        /// <summary>Principal values n1 &gt;= n2 of (xx, yy, xy) and the unit direction (c, s) of n1.</summary>
        public static void Principal(double xx, double yy, double xy, out double n1, out double n2, out double c, out double s)
        {
            double centre = 0.5 * (xx + yy);
            double radius = Math.Sqrt(0.25 * (xx - yy) * (xx - yy) + xy * xy);
            n1 = centre + radius;
            n2 = centre - radius;
            double theta = 0.5 * Math.Atan2(2 * xy, xx - yy);
            c = Math.Cos(theta);
            s = Math.Sin(theta);
        }

        /// <summary>Angle [deg] between two directions, ignoring their sign (0..90).</summary>
        public static double AxisAngle(Vec3 a, Vec3 b)
        {
            double d = Math.Abs(Vec3.Dot(a.Unit(), b.Unit()));
            return Math.Acos(Math.Min(1, d)) * 180 / Math.PI;
        }
    }
}
