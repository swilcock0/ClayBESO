using System;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.Optimisation;

namespace ClayBeso.Printing
{
    /// <summary>Choices for <see cref="ClayPrint.Run"/>. The defaults are the plugin's usual workflow.</summary>
    public sealed class ClayPrintOptions
    {
        /// <summary>Optimise the wall thickness (BESO thickness mode). False = uniform wall, faster.</summary>
        public bool Optimise { get; set; } = true;

        /// <summary>First pull overhangs back to the printer's max lean (<see cref="OverhangFix"/>). Changes the shape.</summary>
        public bool FixOverhang { get; set; }

        /// <summary>Band above the lowest point that is clamped to the bed [mm].</summary>
        public double BaseTolerance { get; set; } = 0.5;

        /// <summary>Print stages checked for strength and buckling while printing.</summary>
        public int Stages { get; set; } = 6;

        /// <summary>Include linear buckling (finished piece and stages).</summary>
        public bool Buckling { get; set; } = true;

        public int MaxIterations { get; set; } = 50;
    }

    /// <summary>Everything one <see cref="ClayPrint.Run"/> produced.</summary>
    public sealed class ClayPrintResult
    {
        public PrinterProfile Printer { get; internal set; }

        /// <summary>The mid-surface that was analysed (corrected when FixOverhang is on) [mm].</summary>
        public TriMesh Shape { get; internal set; }

        /// <summary>The overhang correction, or null when FixOverhang is off.</summary>
        public OverhangFixResult Fix { get; internal set; }
        public ShellModel Model { get; internal set; }
        public ShellDesign Design { get; internal set; }
        public PrintPlan Plan { get; internal set; }
        public PrintCheckResult Check { get; internal set; }
        public PrintScore Score { get; internal set; }
        public PrintTimeline Timeline { get; internal set; }

        /// <summary>BESO iterations (0 when not optimised) and whether it converged.</summary>
        public int Iterations { get; internal set; }
        public bool Converged { get; internal set; }

        /// <summary>Compliance of the printed wall / a uniform wall with the same clay: below 1 = stiffer (1 when not optimised).</summary>
        public double ComplianceRatio { get; internal set; } = 1;
    }

    /// <summary>
    /// The whole workflow in one call, for students: a mid-surface mesh and a printer in, a printable,
    /// checked and scored print out. Base clamped to the bed, self-weight on, starting wall in the middle
    /// of the printer's range, thickness BESO with the printer's layer rule, conforming layers, Print Check.
    /// </summary>
    public static class ClayPrint
    {
        public static ClayPrintResult Run(TriMesh mesh, ClayMaterial material, PrinterProfile printer, ClayPrintOptions options = null)
        {
            var o = options ?? new ClayPrintOptions();
            OverhangFixResult fix = null;
            if (o.FixOverhang)
            {
                fix = OverhangFix.Apply(mesh, printer.MaxOverhang, baseTolerance: o.BaseTolerance);
                mesh = fix.Mesh;
            }
            var model = BedModel(mesh, material, printer.StartWall, o.BaseTolerance);
            var rule = printer.Rule();
            var result = new ClayPrintResult { Printer = printer, Model = model, Shape = mesh, Fix = fix };

            if (o.Optimise)
            {
                var opt = new BesoOptimizer(model, new BesoSettings
                {
                    Mode = BesoMode.Thickness,
                    MinThickness = printer.MinWall,
                    MaxThickness = printer.MaxWall,
                    FilterRadius = SensitivityFilter.AutoRadius(mesh),
                    MaxIterations = o.MaxIterations,
                    LayerRule = rule,
                });
                while (opt.Step()) { }
                var t = opt.EffectiveThickness();
                result.Design = new ShellDesign(model, t, opt.Rule, opt.Settings.Bond, "Clay Print (optimised wall)");
                result.Iterations = opt.Iteration;
                result.Converged = opt.Converged;
                var s = opt.ScalingFor(opt.Design);
                var uniform = opt.ScalingFor(Enumerable.Repeat(printer.StartWall, model.ElementCount).ToArray());
                result.ComplianceRatio = opt.Solver.Solve(s).Compliance / opt.Solver.Solve(uniform).Compliance;
            }
            else
            {
                result.Design = new ShellDesign(model, model.Thickness, rule, new BondModel(), "Clay Print (uniform wall)");
            }

            result.Plan = PrintPlanner.Plan(result.Design, new PrintSettings
            {
                BaseTolerance = o.BaseTolerance,
                MaxOverhang = printer.MaxOverhang,
                MaxTilt = printer.MaxTilt,
            }, rule);
            result.Check = PrintChecker.Check(result.Plan, new PrintCheckSettings
            {
                Strength = material.Strength,
                Stages = o.Stages,
                Buckling = o.Buckling,
            });
            result.Score = PrintScore.Evaluate(result.Plan, result.Check);
            result.Timeline = new PrintTimeline(result.Plan, printer.Speed);
            return result;
        }

        /// <summary>Shell on the print bed: every vertex within <paramref name="baseTolerance"/> of the lowest point clamped, self-weight on.</summary>
        public static ShellModel BedModel(TriMesh mesh, ClayMaterial material, double thickness, double baseTolerance = 0.5)
        {
            int n = mesh.VertexCount;
            var fix = new bool[6 * n];
            double zMin = mesh.Vertices.Min(p => p.Z);
            for (int i = 0; i < n; i++)
                if (mesh.Vertices[i].Z <= zMin + baseTolerance)
                    for (int d = 0; d < 6; d++) fix[6 * i + d] = true;
            return new ShellModel(mesh, material, new[] { thickness }, fix, null, new Vec3(0, 0, -1));
        }
    }
}
