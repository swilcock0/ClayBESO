using System;
using System.Collections.Generic;
using System.Linq;

namespace ClayBeso.Printing
{
    /// <summary>One aspect of printability, scored 0-100.</summary>
    public sealed class ScoreItem
    {
        public string Name { get; internal set; }

        /// <summary>0 (fails) to 100 (comfortable).</summary>
        public double Score { get; internal set; }

        /// <summary>The measured value, in words (e.g. "95 % of points lean less than 31°").</summary>
        public string Measured { get; internal set; }

        /// <summary>What to change when the score is low.</summary>
        public string Advice { get; internal set; }

        public bool Fails => Score < PrintScore.FailBelow;

        public override string ToString() => $"{Name}: {Score:F0}  ({Measured})";
    }

    /// <summary>
    /// A 1-100 printability score for a print plan (and, if given, its structural check).
    /// <para>
    /// Each aspect gets a sub-score from 100 (at its "good" value) to 0 (at its "bad" value), linear in
    /// between. The total is the geometric mean of the sub-scores, so improving anything raises it, while
    /// one weak aspect pulls it down much more than an average would. An aspect that fails (sub-score
    /// below <see cref="FailBelow"/>) caps the total at <see cref="FailCap"/>: a piece that is predicted
    /// to buckle or come apart is not printable, however good the rest is.
    /// </para>
    /// <para>
    /// The good/bad limits are engineering placeholders until the tests in docs/VALIDATION.md calibrate
    /// them (overhang: section 7, buckling: section 5, bond: section 4). Compare designs with the score;
    /// don't read it as a probability of success.
    /// </para>
    /// </summary>
    public sealed class PrintScore
    {
        public const double FailBelow = 10;
        public const double FailCap = 25;

        /// <summary>Buckling factor that scores 100. Twice the margin Print Check warns below (3).</summary>
        public const double BucklingGood = 6;

        public int Total { get; private set; }
        public List<ScoreItem> Items { get; } = new List<ScoreItem>();
        public IEnumerable<ScoreItem> Failing => Items.Where(i => i.Fails);

        /// <summary>The aspect that costs the most points.</summary>
        public ScoreItem Weakest => Items.OrderBy(i => i.Score).FirstOrDefault();

        /// <summary>Score between good (100) and bad (0), linear, clamped.</summary>
        public static double Ramp(double value, double good, double bad)
        {
            if (double.IsNaN(value)) return double.NaN;
            double s = 100 * (value - bad) / (good - bad);
            return Math.Max(0, Math.Min(100, s));
        }

        /// <param name="check">Optional structural check of the same plan (adds buckling and layer bond).</param>
        public static PrintScore Evaluate(PrintPlan plan, PrintCheckResult check = null)
        {
            var st = plan.Settings;
            var score = new PrintScore();
            int points = Math.Max(1, plan.PointCount);
            void Add(string name, double s, string measured, string advice)
            {
                if (!double.IsNaN(s)) score.Items.Add(new ScoreItem { Name = name, Score = s, Measured = measured, Advice = advice });
            }

            // Wall lean: the 95th percentile, so a few points at a sharp rim don't decide it.
            var leans = plan.Overhangs.SelectMany(o => o.Lean).Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
            if (leans.Length > 0)
            {
                double l95 = leans[(int)Math.Min(leans.Length - 1, Math.Floor(0.95 * leans.Length))];
                Add("Overhang", Ramp(l95, st.MaxOverhang - 15, st.MaxOverhang + 10),
                    $"95 % of the wall leans less than {l95:F0}° (limit {st.MaxOverhang:G3}°)",
                    "Make the wall steeper where it leans most (Print Layers, Show = Wall lean), or try Force-aligned layers.");
            }

            double ratioShare = (double)plan.RatioFlags / points;
            Add("Bead support", Ramp(ratioShare, 0, 0.05),
                $"{ratioShare:P1} of the beads sit more than half past the one below",
                "Reduce the overhang or make the wall thicker there, so each bead rests on the one below.");

            double tiltShare = (double)plan.TiltFlags / points;
            Add("Nozzle tilt", Ramp(tiltShare, 0, 0.05),
                $"{tiltShare:P1} of the path slopes more than {st.MaxTilt:G3}° along the layer",
                "Smooth the top edge or the shape where layers climb steeply along the path.");

            double contactShare = (double)plan.PoorContactFlags / points;
            Add("Layer contact", Ramp(contactShare, 0, 0.10),
                $"{contactShare:P1} of the beads barely overlap the layer below",
                "Use lower layers or a thicker wall there (lower Max Layer Height or a smaller nozzle).");

            var err = plan.HeightError.Where(e => !double.IsNaN(e)).Select(Math.Abs).ToArray();
            if (err.Length > 0)
            {
                double mean = err.Average();
                Add("Layer heights", Ramp(mean, 0.05, 0.30),
                    $"layers are on average {mean:P0} off their target height",
                    "Avoid sharp changes of wall thickness, or try Eikonal layers.");
            }

            if (plan.Paths.Count > 0)
            {
                double open = (double)plan.OpenPaths / plan.Paths.Count;
                Add("Closed layers", Ramp(open, 0, 0.25),
                    $"{plan.OpenPaths} of {plan.Paths.Count} layers are partial (open)",
                    "Make the rim level, or use Conforming layers, so every layer is a closed loop.");
            }

            if (check != null)
            {
                double lambda = check.MinBucklingFactor;
                if (!double.IsNaN(lambda))
                {
                    double s = double.IsPositiveInfinity(lambda) ? 100
                        : lambda <= 1 ? 0
                        : Ramp(Math.Log(lambda), Math.Log(BucklingGood), 0);
                    Add("Buckling", s,
                        double.IsPositiveInfinity(lambda) ? "nothing in compression"
                            : $"lowest buckling factor {lambda:G3} (finished piece or a print stage; aim for {BucklingGood:G2}+)",
                        "Make the wall thicker or more curved where it buckles, or the piece lower (Print Check, Show = Buckling mode).");
                }
                if (check.HasStrength)
                {
                    double u = check.MaxUtilisation;
                    Add("Layer bond", Ramp(u, 0.5, 1.0),
                        $"highest interlayer utilisation {u:P0}",
                        "Thicken the wall where layers are most loaded (Print Check, Show = Interlayer utilisation), or use lower layers.");
                }
            }

            if (score.Items.Count == 0)
            {
                score.Total = 100;
                return score;
            }
            double logMean = score.Items.Average(i => Math.Log(Math.Max(1.0, i.Score) / 100));
            double total = 100 * Math.Exp(logMean);
            if (score.Failing.Any()) total = Math.Min(total, FailCap);
            score.Total = (int)Math.Max(1, Math.Min(100, Math.Round(total)));
            return score;
        }

        /// <summary>Plain-language report: total, then every aspect, weakest first, with advice for the weak ones.</summary>
        public string Report()
        {
            var lines = new List<string> { $"Printability {Total}/100" };
            if (Failing.Any())
                lines.Add($"Fails on: {string.Join(", ", Failing.Select(i => i.Name))} (score capped at {FailCap:G2}).");
            foreach (var i in Items.OrderBy(i => i.Score))
            {
                lines.Add($"  {i.Score,3:F0}  {i.Name}: {i.Measured}");
                if (i.Score < 70) lines.Add($"       → {i.Advice}");
            }
            if (!Items.Any(i => i.Name == "Buckling"))
                lines.Add("  (connect a Print Check to include buckling and layer bond)");
            lines.Add("Limits are placeholders until calibrated (docs/VALIDATION.md): use the score to compare designs.");
            return string.Join("\n", lines);
        }
    }
}
