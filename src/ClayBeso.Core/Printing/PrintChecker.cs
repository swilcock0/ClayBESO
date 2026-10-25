using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.Geometry;

namespace ClayBeso.Printing
{
    public sealed class PrintCheckSettings
    {
        /// <summary>Clay strength f [MPa]; 0 = no strength (utilisation not reported).</summary>
        public double Strength { get; set; }

        /// <summary>Bond model; null = the design's, else eta = 1.</summary>
        public BondModel Bond { get; set; }

        /// <summary>Weaker across the layers, by the contact of the layers actually printed.</summary>
        public bool Orthotropic { get; set; } = true;

        /// <summary>Number of print stages to check (0 = finished piece only).</summary>
        public int Stages { get; set; } = 8;

        /// <summary>Apply point loads during printing too (they always act on the finished piece).</summary>
        public bool StagePointLoads { get; set; }

        /// <summary>Linear buckling analysis of the finished piece and of every stage.</summary>
        public bool Buckling { get; set; } = true;
    }

    /// <summary>Structural check of a print plan: the finished piece with all loads, and the print stages.</summary>
    public sealed class PrintCheckResult
    {
        public bool HasStrength { get; internal set; }

        /// <summary>The print that was checked.</summary>
        public PrintPlan Plan { get; internal set; }

        public AnalysisResult Final { get; internal set; }
        public ElementScaling FinalScaling { get; internal set; }
        public double[] FinalVonMises { get; internal set; }

        /// <summary>Interlayer check of the finished piece (utilisation is meaningful only with a strength).</summary>
        public InterlayerResult FinalInterlayer { get; internal set; }

        public List<PrintStageResult> Stages { get; internal set; } = new List<PrintStageResult>();

        /// <summary>Index into Stages of the most critical stage (-1 when no stages).</summary>
        public int WorstStage { get; internal set; } = -1;

        /// <summary>Buckling of the finished piece under all loads (null when not computed).</summary>
        public BucklingResult FinalBuckling { get; internal set; }

        /// <summary>Index into Stages of the stage with the lowest buckling factor (-1 if none).</summary>
        public int BucklingStage { get; internal set; } = -1;

        /// <summary>Lowest buckling factor anywhere: finished piece or any stage (NaN when not computed).</summary>
        public double MinBucklingFactor
        {
            get
            {
                if (FinalBuckling == null) return double.NaN;
                double f = FinalBuckling.Critical;
                foreach (var s in Stages)
                    if (s.Skipped == null && !double.IsNaN(s.BucklingFactor)) f = Math.Min(f, s.BucklingFactor);
                return f;
            }
        }

        public double FinalMaxUtilisation =>
            HasStrength ? FinalInterlayer.Utilisation.Where(u => !double.IsNaN(u)).DefaultIfEmpty(0).Max() : double.NaN;

        /// <summary>Largest utilisation anywhere: the finished piece or any stage.</summary>
        public double MaxUtilisation =>
            HasStrength
                ? Math.Max(FinalMaxUtilisation, Stages.Where(s => s.Skipped == null).Select(s => s.MaxUtilisation).DefaultIfEmpty(0).Max())
                : double.NaN;
    }

    public static class PrintChecker
    {
        public static PrintCheckResult Check(PrintPlan plan, PrintCheckSettings s)
        {
            var design = plan.Design;
            var model = design.Model;
            var t = design.Thickness;
            if (t.Any(x => x <= 0))
                throw new ArgumentException("The design has removed faces (void mode): its layers can't be checked as a continuous wall.");
            var bond = s.Bond ?? design.Bond ?? new BondModel();
            var up = Vec3.UnitZ;

            // Finished piece, all loads.
            var scaling = ElementScaling.FromThickness(t);
            if (s.Orthotropic)
            {
                model.EnableLayerAnisotropy(up);
                var hReal = plan.Field.RealisedFaceHeight();
                for (int e = 0; e < t.Length; e++)
                    if (!double.IsInfinity(hReal[e]) && !double.IsNaN(hReal[e]))
                        scaling.Across[e] = bond.Efficiency * bond.Contact(t[e], hReal[e]);
            }
            var solver = new ShellSolver(model);
            BucklingResult buckling = s.Buckling ? Buckling.Compute(solver, scaling) : null;
            var r = buckling?.Analysis ?? solver.Solve(scaling);
            var result = new PrintCheckResult
            {
                Plan = plan,
                FinalBuckling = buckling,
                HasStrength = s.Strength > 0,
                Final = r,
                FinalScaling = scaling,
                FinalVonMises = solver.VonMises(r, scaling),
                // Without a strength, check against f = 1 to get the geometry (angles, contact); utilisation is hidden.
                FinalInterlayer = InterlayerCheck.Compute(model, r, scaling, plan.Field.Phi, up, plan.Settings.Vertical,
                    s.Strength > 0 ? s.Strength : 1.0, bond),
            };

            if (s.Stages > 0)
            {
                var layers = PrintStages.EvenStages(plan.Field.Phi, s.Stages);
                result.Stages = PrintStages.Run(model, t, plan.Field.Phi, layers, new PrintStageOptions
                {
                    Vertical = plan.Settings.Vertical,
                    IncludePointLoads = s.StagePointLoads,
                    Strength = Math.Max(0, s.Strength),
                    Bond = bond,
                    Orthotropic = s.Orthotropic,
                    Buckling = s.Buckling,
                });
                var done = Enumerable.Range(0, result.Stages.Count).Where(i => result.Stages[i].Skipped == null).ToArray();
                if (done.Length > 0)
                    result.WorstStage = result.HasStrength
                        ? done.OrderByDescending(i => result.Stages[i].MaxUtilisation).First()
                        : done.OrderByDescending(i => result.Stages[i].MaxVonMises).First();
                var buckled = done.Where(i => !double.IsNaN(result.Stages[i].BucklingFactor) &&
                                              !double.IsInfinity(result.Stages[i].BucklingFactor)).ToArray();
                if (buckled.Length > 0)
                    result.BucklingStage = buckled.OrderBy(i => result.Stages[i].BucklingFactor).First();
            }
            return result;
        }
    }
}
