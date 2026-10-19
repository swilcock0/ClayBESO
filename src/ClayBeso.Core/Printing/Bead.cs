using System;

namespace ClayBeso.Printing
{
    /// <summary>Cross-section of a squashed extrusion bead.</summary>
    public static class Bead
    {
        /// <summary>
        /// Area [mm^2] of a bead of width w and height h, modelled as a rectangle with
        /// semicircular sides (the "stadium" used by Slic3r/PrusaSlicer):
        /// A = (w - h) h + pi h^2 / 4 for w &gt;= h. A bead taller than it is wide is treated as a
        /// rectangle, which is not a stable bead shape anyway.
        /// </summary>
        public static double Area(double width, double height)
        {
            if (width <= 0 || height <= 0 || double.IsNaN(height)) return 0;
            return width >= height
                ? (width - height) * height + Math.PI * height * height / 4.0
                : width * height;
        }

        /// <summary>Bead width that a given cross-section and height produce (inverse of <see cref="Area"/>).</summary>
        public static double Width(double area, double height)
        {
            if (area <= 0 || height <= 0) return 0;
            double w = area / height + height * (1 - Math.PI / 4.0);
            return w >= height ? w : area / height;
        }
    }
}
