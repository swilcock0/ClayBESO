using System;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace ClayBeso.GH.Common
{
    /// <summary>
    /// A component whose preview is a false-colour mesh, with a colour-bar legend drawn in the
    /// top-left corner of the Rhino viewports. Legends of several components sit side by side,
    /// in canvas order.
    /// </summary>
    public abstract class LegendComponent : GH_Component
    {
        private sealed class Legend
        {
            public string Title;
            public string Unit;
            public double Min, Max;
        }

        private Legend _legend;

        protected LegendComponent(string name, string nickname, string description, string category, string subCategory)
            : base(name, nickname, description, category, subCategory) { }

        protected override void BeforeSolveInstance()
        {
            _legend = null;
            base.BeforeSolveInstance();
        }

        /// <summary>Call from SolveInstance with the range the preview colours were mapped to.</summary>
        protected void SetLegend(string title, string unit, (double lo, double hi) range, bool show)
        {
            _legend = show ? new Legend { Title = title, Unit = unit, Min = range.lo, Max = range.hi } : null;
        }

        /// <summary>
        /// Reads an optional colour-range interval input. Returns the fallback when nothing valid
        /// is connected (fallback null = automatic min-max of the data).
        /// </summary>
        protected static (double lo, double hi)? ReadRange(IGH_DataAccess da, int index, (double lo, double hi)? fallback)
        {
            var iv = Interval.Unset;
            if (da.GetData(index, ref iv) && iv.IsValid && Math.Abs(iv.Length) > 1e-12)
                return (Math.Min(iv.T0, iv.T1), Math.Max(iv.T0, iv.T1));
            return fallback;
        }

        private bool LegendActive => _legend != null && !Hidden && !Locked;

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            base.DrawViewportWires(args);
            if (!LegendActive) return;

            // Slot = position among the visible legends in this document, so they don't overlap.
            int slot = 0;
            var doc = OnPingDocument();
            if (doc != null)
            {
                var active = doc.Objects.OfType<LegendComponent>().Where(c => c.LegendActive).ToList();
                slot = Math.Max(0, active.IndexOf(this));
            }
            Draw(args, _legend, slot);
        }

        // TopOffset keeps the panel clear of Rhino's viewport title ("Perspective" etc.) in the top-left.
        private const int PanelWidth = 150, PanelHeight = 236, Margin = 12, TopOffset = 40;
        private const int BarWidth = 22, BarHeight = 170, Steps = 48, Ticks = 5;

        private static void Draw(IGH_PreviewArgs args, Legend lg, int slot)
        {
            var d = args.Display;
            int x0 = Margin + slot * (PanelWidth + Margin);
            int y0 = TopOffset;
            var text = Color.FromArgb(30, 30, 30);

            d.Draw2dRectangle(new Rectangle(x0, y0, PanelWidth, PanelHeight),
                Color.FromArgb(160, 160, 160), 1, Color.FromArgb(225, 255, 255, 255));

            d.Draw2dText(lg.Title, text, new Point2d(x0 + 10, y0 + 8), false, 13);
            d.Draw2dText($"[{lg.Unit}]", Color.FromArgb(90, 90, 90), new Point2d(x0 + 10, y0 + 25), false, 11);

            // Colour bar, maximum at the top.
            int bx = x0 + 12, by = y0 + 48;
            for (int i = 0; i < Steps; i++)
            {
                int top = by + i * BarHeight / Steps;
                int bottom = by + (i + 1) * BarHeight / Steps;
                var c = Preview.ColourAt(1.0 - (i + 0.5) / Steps);
                d.Draw2dRectangle(new Rectangle(bx, top, BarWidth, bottom - top), c, 0, c);
            }
            d.Draw2dRectangle(new Rectangle(bx, by, BarWidth, BarHeight), Color.FromArgb(90, 90, 90), 1, Color.Transparent);

            for (int k = 0; k < Ticks; k++)
            {
                double s = (double)k / (Ticks - 1);
                double value = lg.Max - s * (lg.Max - lg.Min);
                int y = by + (int)Math.Round(s * BarHeight);
                d.Draw2dLine(new System.Drawing.Point(bx + BarWidth, y), new System.Drawing.Point(bx + BarWidth + 5, y), text, 1);
                d.Draw2dText(Format(value), text, new Point2d(bx + BarWidth + 9, y - 7), false, 12);
            }
        }

        private static string Format(double v)
        {
            double a = Math.Abs(v);
            if (a != 0 && (a < 1e-3 || a >= 1e5)) return v.ToString("0.00E+0");
            return a >= 100 ? v.ToString("0") : a >= 10 ? v.ToString("0.0") : v.ToString("0.###");
        }
    }
}
