using System;
using System.Collections.Generic;
using ClayBeso.Geometry;
using ClayBeso.Printing;

namespace ClayBeso.Fem
{
    /// <summary>
    /// How well consecutive layers are bonded. A bead of width w and height h has rounded sides
    /// (stadium cross-section, see <see cref="Bead.Area"/>), so the flat strip it shares with the
    /// next layer is only w - h wide: contact fraction c = (w - h) / w. Taller layers on the same
    /// wall bond over a narrower strip. The bond efficiency eta (0..1) scales the strength of that
    /// strip relative to the bulk clay and is meant to be calibrated from tests.
    /// </summary>
    public sealed class BondModel
    {
        /// <summary>Interface strength per unit contact area relative to the bulk clay (calibrate from tests).</summary>
        public double Efficiency { get; }

        /// <summary>Lower bound on the contact fraction (a round bead still touches over a small strip).</summary>
        public double MinContact { get; }

        public BondModel(double efficiency = 1.0, double minContact = 0.05)
        {
            if (efficiency <= 0 || efficiency > 1) throw new ArgumentException("Bond efficiency must be in (0, 1].");
            if (minContact <= 0 || minContact > 1) throw new ArgumentException("Minimum contact must be in (0, 1].");
            Efficiency = efficiency;
            MinContact = minContact;
        }

        /// <summary>Contact fraction c = (w - h) / w, clamped to [MinContact, 1].</summary>
        public double Contact(double width, double height)
        {
            if (width <= 0) return MinContact;
            double c = (width - height) / width;
            return c < MinContact ? MinContact : (c > 1 ? 1 : c);
        }
    }

    /// <summary>Per-face results of the interlayer check.</summary>
    public sealed class InterlayerResult
    {
        /// <summary>Realised layer height [mm] (NaN where the layer direction is undefined, e.g. a flat floor).</summary>
        public double[] LayerHeight { get; internal set; }

        /// <summary>Contact fraction c = (w - h) / w.</summary>
        public double[] Contact { get; internal set; }

        /// <summary>Largest tensile normal stress on the interface contact strip [MPa] (0 if all compressive).</summary>
        public double[] Tension { get; internal set; }

        /// <summary>Largest compressive normal stress on the interface contact strip [MPa], as a positive number.</summary>
        public double[] Compression { get; internal set; }

        /// <summary>Largest shear stress on the interface contact strip [MPa].</summary>
        public double[] Shear { get; internal set; }

        /// <summary>Worst of bond (tension + shear) and bearing (compression) utilisation.</summary>
        public double[] Utilisation { get; internal set; }

        /// <summary>Bond utilisation: sqrt(&lt;sigma_n&gt;+^2 + tau^2) / (eta f).</summary>
        public double[] BondUtilisation { get; internal set; }

        /// <summary>Bearing utilisation of the narrowed contact strip: &lt;-sigma_n&gt;+ / f.</summary>
        public double[] BearingUtilisation { get; internal set; }

        /// <summary>Unit stacking direction per face (across the layers, within the wall).</summary>
        public Vec3[] StackingDirection { get; internal set; }

        public bool IsValid(int face) => !double.IsNaN(Utilisation[face]);
    }

    /// <summary>
    /// Checks the layer interfaces of a printed shell. Stress resultants from the analysis are
    /// rotated into the layer axes (along the bead / across the layers, taken from the gradient of
    /// the layer field phi) and carried by the bonded contact strip only, which is c*t wide:
    /// sigma_n = N_ss/(c t) +- 6 M_ss/(c t)^2,  tau = N_as/(c t) +- 6 M_as/(c t)^2.
    /// </summary>
    public static class InterlayerCheck
    {
        /// <param name="phi">Layer field per vertex (layer k is phi = k), from the Non-planar Layers step.</param>
        /// <param name="vertical">Same setting as used for the layer field (layer height measured in Z).</param>
        /// <param name="strength">Bulk clay strength f [MPa].</param>
        public static InterlayerResult Compute(
            ShellModel model,
            AnalysisResult result,
            ElementScaling scaling,
            IList<double> phi,
            Vec3 up,
            bool vertical,
            double strength,
            BondModel bond)
        {
            if (strength <= 0) throw new ArgumentException("The interlayer check needs a positive clay strength f.");
            if (phi.Count != model.NodeCount) throw new ArgumentException("Need one layer-field value per mesh vertex.");

            var mesh = model.Mesh;
            int m = model.ElementCount;
            var ops = new MeshOperators(mesh);
            var heights = LayerField.RealisedFaceHeight(mesh, phi, up, vertical);

            var res = new InterlayerResult
            {
                LayerHeight = new double[m],
                Contact = new double[m],
                Tension = new double[m],
                Compression = new double[m],
                Shear = new double[m],
                Utilisation = new double[m],
                BondUtilisation = new double[m],
                BearingUtilisation = new double[m],
                StackingDirection = new Vec3[m],
            };

            var ue = new double[18];
            for (int e = 0; e < m; e++)
            {
                var el = model.Elements[e];
                var tri = el.Nodes;
                var g = ops.FaceGradient(e, phi);
                bool finite = !double.IsInfinity(phi[tri[0]]) && !double.IsInfinity(phi[tri[1]]) && !double.IsInfinity(phi[tri[2]]);
                double h = heights[e];
                if (!finite || g.Length < 1e-12 || double.IsInfinity(h) || double.IsNaN(h))
                {
                    SetInvalid(res, e);
                    continue;
                }

                // Stacking direction in the element's local frame.
                double sx = Vec3.Dot(g, el.E1), sy = Vec3.Dot(g, el.E2);
                double sl = Math.Sqrt(sx * sx + sy * sy);
                if (sl < 1e-12) { SetInvalid(res, e); continue; }
                sx /= sl; sy /= sl;
                res.StackingDirection[e] = (el.E1 * sx + el.E2 * sy).Unit();

                ShellSolver.Gather(el, result.U, ue);
                el.UnitResultants(ue, out var nUnit, out var mUnit, scaling.Across[e]);
                double t = scaling.Thickness[e];
                var nRes = new[] { scaling.Membrane[e] * nUnit[0], scaling.Membrane[e] * nUnit[1], scaling.Membrane[e] * nUnit[2] };
                var mRes = new[] { scaling.Bending[e] * mUnit[0], scaling.Bending[e] * mUnit[1], scaling.Bending[e] * mUnit[2] };

                // Normal and shear component on the interface (normal s, in-plane tangent a = n x s).
                double nss = Normal(nRes, sx, sy), nas = Shear(nRes, sx, sy);
                double mss = Normal(mRes, sx, sy), mas = Shear(mRes, sx, sy);

                double c = bond.Contact(t, h);
                double width = c * t;
                double tension = 0, compression = 0, shear = 0, bondU = 0, bearingU = 0;
                for (int side = -1; side <= 1; side += 2)
                {
                    double sn = nss / width + side * 6 * mss / (width * width);
                    double tau = Math.Abs(nas / width + side * 6 * mas / (width * width));
                    tension = Math.Max(tension, sn);
                    compression = Math.Max(compression, -sn);
                    shear = Math.Max(shear, tau);
                    double tPos = Math.Max(sn, 0);
                    bondU = Math.Max(bondU, Math.Sqrt(tPos * tPos + tau * tau) / (bond.Efficiency * strength));
                    bearingU = Math.Max(bearingU, Math.Max(-sn, 0) / strength);
                }

                res.LayerHeight[e] = h;
                res.Contact[e] = c;
                res.Tension[e] = tension;
                res.Compression[e] = compression;
                res.Shear[e] = shear;
                res.BondUtilisation[e] = bondU;
                res.BearingUtilisation[e] = bearingU;
                res.Utilisation[e] = Math.Max(bondU, bearingU);
            }
            return res;
        }

        /// <summary>Normal component s.S.s of a symmetric 2D tensor given as (xx, yy, xy).</summary>
        private static double Normal(double[] s, double sx, double sy) =>
            s[0] * sx * sx + s[1] * sy * sy + 2 * s[2] * sx * sy;

        /// <summary>Shear component a.S.s with a = (-sy, sx).</summary>
        private static double Shear(double[] s, double sx, double sy) =>
            (s[1] - s[0]) * sx * sy + s[2] * (sx * sx - sy * sy);

        private static void SetInvalid(InterlayerResult r, int e)
        {
            r.LayerHeight[e] = r.Contact[e] = r.Tension[e] = r.Compression[e] = r.Shear[e] = double.NaN;
            r.Utilisation[e] = r.BondUtilisation[e] = r.BearingUtilisation[e] = double.NaN;
        }
    }
}
