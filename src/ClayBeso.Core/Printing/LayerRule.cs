using System;
using System.Collections.Generic;
using System.Linq;

namespace ClayBeso.Printing
{
    /// <summary>
    /// How a wall thickness t turns into a target layer height h(t). One rule is shared by the
    /// Layer Heights step and the optimiser, so the layer heights BESO assumes are the ones printed.
    /// </summary>
    public sealed class LayerRule
    {
        public LayerMapping Mapping { get; }
        public double HMin { get; }
        public double HMax { get; }

        /// <summary>Bead cross-section [mm^2] for <see cref="LayerMapping.ConstantBeadArea"/>; &lt;= 0 = automatic.</summary>
        public double BeadArea { get; }

        /// <summary>Height/width ratio k for <see cref="LayerMapping.ConstantAspectRatio"/>.</summary>
        public double AspectRatio { get; }

        /// <summary>Thickness interval mapped onto [hMin, hMax] by the linear mappings; null = automatic.</summary>
        public (double lo, double hi)? ThicknessRange { get; }

        public LayerRule(LayerMapping mapping, double hMin, double hMax, double beadArea = 0,
            double aspectRatio = 0.4, (double lo, double hi)? thicknessRange = null)
        {
            if (hMin <= 0 || hMax < hMin) throw new ArgumentException("Need 0 < hMin <= hMax.");
            if (mapping == LayerMapping.ConstantAspectRatio && aspectRatio <= 0)
                throw new ArgumentException("Aspect ratio must be positive.");
            Mapping = mapping;
            HMin = hMin;
            HMax = hMax;
            BeadArea = beadArea;
            AspectRatio = aspectRatio;
            ThicknessRange = thicknessRange;
        }

        public bool IsResolved =>
            (Mapping != LayerMapping.ConstantBeadArea || BeadArea > 0) &&
            (Mapping == LayerMapping.ConstantBeadArea || Mapping == LayerMapping.ConstantAspectRatio || ThicknessRange != null);

        /// <summary>
        /// Fill in the automatic values: the thickness range (default <paramref name="tLo"/>..<paramref name="tHi"/>)
        /// and the bead area (the bead of the median thickness at the middle layer height).
        /// </summary>
        public LayerRule Resolve(double tLo, double tHi, double tMedian)
        {
            var range = ThicknessRange ?? (tLo, tHi);
            double area = BeadArea > 0 ? BeadArea : Bead.Area(tMedian, 0.5 * (HMin + HMax));
            return new LayerRule(Mapping, HMin, HMax, area, AspectRatio, range);
        }

        /// <summary>Resolve the automatic values from a sample of wall thicknesses (zeros = voids, ignored).</summary>
        public LayerRule Resolve(IEnumerable<double> thickness)
        {
            var solid = thickness.Where(t => t > 0).OrderBy(t => t).ToArray();
            if (solid.Length == 0) throw new ArgumentException("Thickness field is empty.");
            return Resolve(solid[0], solid[solid.Length - 1], solid[solid.Length / 2]);
        }

        /// <summary>Target layer height [mm] for wall thickness t (rule must be resolved).</summary>
        public double Height(double t)
        {
            if (!IsResolved) throw new InvalidOperationException("Resolve the layer rule before use.");
            if (t <= 0) return HMax;
            switch (Mapping)
            {
                case LayerMapping.ConstantBeadArea:
                    return Clamp(HeightForArea(BeadArea, t), HMin, HMax);
                case LayerMapping.ConstantAspectRatio:
                    return Clamp(AspectRatio * t, HMin, HMax);
                default:
                    var (lo, hi) = ThicknessRange.Value;
                    double span = hi - lo;
                    double s = Clamp(span > 1e-12 ? (t - lo) / span : 0.5, 0, 1);
                    return Mapping == LayerMapping.Proportional
                        ? HMin + s * (HMax - HMin)
                        : HMax - s * (HMax - HMin);
            }
        }

        /// <summary>Height at which a bead of width w has cross-section A (inverse of Bead.Area in h).</summary>
        internal static double HeightForArea(double area, double width)
        {
            // (w - h) h + pi h^2/4 = A  ->  c h^2 - w h + A = 0, c = 1 - pi/4; take the smaller root.
            double c = 1 - Math.PI / 4.0;
            double disc = width * width - 4 * c * area;
            return disc < 0 ? width : (width - Math.Sqrt(disc)) / (2 * c);
        }

        private static double Clamp(double x, double a, double b) => x < a ? a : (x > b ? b : x);

        public override string ToString() =>
            $"Layer rule: {Mapping}, h {HMin:G3}-{HMax:G3} mm" +
            (Mapping == LayerMapping.ConstantAspectRatio ? $", k {AspectRatio:G3}" : "") +
            (Mapping == LayerMapping.ConstantBeadArea && BeadArea > 0 ? $", A {BeadArea:G3} mm²" : "") +
            (ThicknessRange is (double lo, double hi) ? $", t {lo:G3}-{hi:G3} mm" : "");
    }
}
