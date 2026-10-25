using System;
using System.Collections.Generic;

namespace ClayBeso.Printing
{
    /// <summary>
    /// What a printer and nozzle can do, so the student picks a printer instead of a dozen numbers.
    /// Every limit follows from the nozzle diameter d:
    /// wall (bead width) 1.17 d - 2.33 d, layer height d/3 - 0.75 d (within 25-75 % of the nozzle,
    /// Vele, Kurilla &amp; Achten 2023). For d = 3 mm that is the plugin's defaults: wall 3.5-7, layers 1-2.25 mm.
    /// </summary>
    public sealed class PrinterProfile
    {
        public string Name { get; }

        /// <summary>Nozzle diameter [mm].</summary>
        public double Nozzle { get; }

        /// <summary>Thinnest and thickest wall (bead width) [mm]: BESO's tMin and tMax.</summary>
        public double MinWall { get; }
        public double MaxWall { get; }

        /// <summary>Lowest and tallest layer [mm].</summary>
        public double MinLayer { get; }
        public double MaxLayer { get; }

        /// <summary>Wall lean from vertical above which points are flagged [deg].</summary>
        public double MaxOverhang { get; }

        /// <summary>Layer slope along the path above which a nozzle that can't tilt may scrape [deg].</summary>
        public double MaxTilt { get; }

        /// <summary>Nozzle speed along the path [mm/s], for print times.</summary>
        public double Speed { get; }

        public PrinterProfile(string name, double nozzle, double minWall, double maxWall, double minLayer, double maxLayer,
            double maxOverhang = 45, double maxTilt = 30, double speed = 25)
        {
            if (nozzle <= 0) throw new ArgumentException("Nozzle diameter must be positive.");
            if (minWall <= 0 || maxWall <= minWall) throw new ArgumentException("Need 0 < min wall < max wall.");
            if (minLayer <= 0 || maxLayer < minLayer) throw new ArgumentException("Need 0 < min layer <= max layer.");
            Name = name;
            Nozzle = nozzle;
            MinWall = minWall;
            MaxWall = maxWall;
            MinLayer = minLayer;
            MaxLayer = maxLayer;
            MaxOverhang = maxOverhang;
            MaxTilt = maxTilt;
            Speed = speed;
        }

        /// <summary>A printer whose limits all follow from the nozzle diameter (see the class summary).</summary>
        public static PrinterProfile ForNozzle(string name, double nozzle) =>
            new PrinterProfile(name, nozzle,
                minWall: Math.Round(7.0 / 6.0 * nozzle, 2), maxWall: Math.Round(7.0 / 3.0 * nozzle, 2),
                minLayer: Math.Round(nozzle / 3.0, 2), maxLayer: Math.Round(0.75 * nozzle, 2));

        /// <summary>Delta WASP 40100 Clay with the nozzles in use (speed 25 mm/s is a placeholder: set your own).</summary>
        public static IReadOnlyList<PrinterProfile> Presets { get; } = new[]
        {
            ForNozzle("WASP 40100, 3 mm nozzle", 3.0),
            ForNozzle("WASP 40100, 2 mm nozzle", 2.0),
            ForNozzle("WASP 40100, 1.5 mm nozzle", 1.5),
        };

        /// <summary>Starting wall [mm] for thickness optimisation: the middle of the wall range.</summary>
        public double StartWall => 0.5 * (MinWall + MaxWall);

        /// <summary>Thicker wall = taller layer, spanning this printer's ranges.</summary>
        public LayerRule Rule() =>
            new LayerRule(LayerMapping.Proportional, MinLayer, MaxLayer, thicknessRange: (MinWall, MaxWall));

        public override string ToString() =>
            $"{Name}: wall {MinWall:G3}-{MaxWall:G3} mm, layers {MinLayer:G3}-{MaxLayer:G3} mm, " +
            $"max lean {MaxOverhang:G3}°, max tilt {MaxTilt:G3}°, {Speed:G3} mm/s";
    }
}
