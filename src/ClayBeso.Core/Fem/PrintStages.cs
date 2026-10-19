using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Geometry;
using ClayBeso.Printing;

namespace ClayBeso.Fem
{
    public sealed class PrintStageOptions
    {
        /// <summary>Layer heights in phi measured vertically (same setting as the layer field).</summary>
        public bool Vertical { get; set; } = true;

        /// <summary>Also apply the model's point loads at every stage (normally they act on the finished piece).</summary>
        public bool IncludePointLoads { get; set; }

        /// <summary>Clay strength f [MPa] for the interlayer check; 0 = skip it.</summary>
        public double Strength { get; set; }

        public BondModel Bond { get; set; } = new BondModel();

        /// <summary>Weaker across the layers (kappa = eta*c from the realised layer heights).</summary>
        public bool Orthotropic { get; set; } = true;
    }

    /// <summary>The partly printed shell at one stage.</summary>
    public sealed class PrintStageResult
    {
        /// <summary>Printed up to this layer (faces with phi &lt;= Layer on all corners).</summary>
        public int Layer { get; internal set; }

        /// <summary>Highest printed point [mm].</summary>
        public double TopZ { get; internal set; }

        /// <summary>Faces of the full model that exist at this stage.</summary>
        public int[] Faces { get; internal set; }

        public double MaxDisplacement { get; internal set; }
        public double MaxVonMises { get; internal set; }

        /// <summary>Peak interlayer utilisation (NaN when no strength was given).</summary>
        public double MaxUtilisation { get; internal set; }

        /// <summary>Per face of the FULL model (NaN where not printed yet).</summary>
        public double[] VonMises { get; internal set; }
        public double[] Utilisation { get; internal set; }

        /// <summary>Per vertex of the FULL model (zero where not printed yet) [mm].</summary>
        public Vec3[] Displacement { get; internal set; }

        /// <summary>Why the stage could not be analysed (null if it was).</summary>
        public string Skipped { get; internal set; }
    }

    /// <summary>
    /// Analyses the shell as it stands during printing: for each stage k, only the faces printed up
    /// to layer k (by the layer field phi), loaded by their own weight. Overhangs typically fail
    /// part-way up (Vele &amp; Achten 2023), which an analysis of the finished piece cannot show.
    /// The clay is taken as equally stiff at every height (no ageing yet).
    /// </summary>
    public static class PrintStages
    {
        /// <summary>Evenly spaced stage layers: about count stages ending at the last layer.</summary>
        public static int[] EvenStages(IList<double> phi, int count)
        {
            int last = (int)Math.Floor(phi.Where(p => !double.IsInfinity(p) && !double.IsNaN(p)).Max() + 1e-9);
            count = Math.Max(1, Math.Min(count, last));
            return Enumerable.Range(1, count).Select(k => (int)Math.Round((double)k * last / count)).Distinct().ToArray();
        }

        public static List<PrintStageResult> Run(ShellModel model, IList<double> thickness, IList<double> phi,
            IList<int> layers, PrintStageOptions options)
        {
            if (phi.Count != model.NodeCount) throw new ArgumentException("Need one layer-field value per mesh vertex.");
            if (thickness.Count != model.ElementCount) throw new ArgumentException("Need one thickness per face.");
            return layers.Select(k => RunStage(model, thickness, phi, k, options)).ToList();
        }

        private static PrintStageResult RunStage(ShellModel model, IList<double> thickness, IList<double> phi, int layer,
            PrintStageOptions o)
        {
            var mesh = model.Mesh;
            var res = new PrintStageResult
            {
                Layer = layer,
                VonMises = Enumerable.Repeat(double.NaN, model.ElementCount).ToArray(),
                Utilisation = Enumerable.Repeat(double.NaN, model.ElementCount).ToArray(),
                Displacement = new Vec3[model.NodeCount],
                MaxUtilisation = double.NaN,
            };

            // Printed faces and their vertices, renumbered.
            double level = layer + 1e-9;
            var faces = Enumerable.Range(0, mesh.FaceCount)
                .Where(f => mesh.Faces[f].All(v => phi[v] <= level)).ToArray();
            res.Faces = faces;
            if (faces.Length == 0) { res.Skipped = "nothing printed yet"; return res; }

            var map = new Dictionary<int, int>();
            var verts = new List<Vec3>();
            foreach (int f in faces)
                foreach (int v in mesh.Faces[f])
                    if (!map.ContainsKey(v)) { map[v] = verts.Count; verts.Add(mesh.Vertices[v]); }
            var subFaces = faces.Select(f => mesh.Faces[f].Select(v => map[v]).ToArray()).ToList();
            var back = new int[verts.Count];
            foreach (var kv in map) back[kv.Value] = kv.Key;
            res.TopZ = verts.Max(p => p.Z);

            int n = verts.Count;
            var fix = new bool[6 * n];
            var loads = new double[6 * n];
            for (int i = 0; i < n; i++)
                for (int d = 0; d < 6; d++)
                {
                    fix[6 * i + d] = model.Fixed[6 * back[i] + d];
                    if (o.IncludePointLoads) loads[6 * i + d] = model.FixedLoads[6 * back[i] + d];
                }

            ShellModel stage;
            try
            {
                stage = new ShellModel(new TriMesh(verts, subFaces), model.Material,
                    faces.Select(f => thickness[f]).ToArray(), fix, loads, model.Gravity) { UnitScale = model.UnitScale };
            }
            catch (ArgumentException ex)
            {
                res.Skipped = ex.Message;
                return res;
            }

            var subPhi = back.Select(v => phi[v]).ToArray();
            var scaling = ElementScaling.FromThickness(stage.Thickness);
            if (o.Orthotropic)
            {
                stage.EnableLayerAnisotropy(Vec3.UnitZ);
                var h = LayerField.RealisedFaceHeight(stage.Mesh, subPhi, Vec3.UnitZ, o.Vertical);
                for (int e = 0; e < stage.ElementCount; e++)
                    if (!double.IsInfinity(h[e]) && !double.IsNaN(h[e]))
                        scaling.Across[e] = o.Bond.Efficiency * o.Bond.Contact(stage.Thickness[e], h[e]);
            }

            var solver = new ShellSolver(stage);
            var r = solver.Solve(scaling);
            var vm = solver.VonMises(r, scaling);
            res.MaxDisplacement = r.MaxDisplacement;
            res.MaxVonMises = vm.Max();
            for (int e = 0; e < faces.Length; e++) res.VonMises[faces[e]] = vm[e];
            for (int i = 0; i < n; i++) res.Displacement[back[i]] = r.Displacement(i);

            if (o.Strength > 0)
            {
                var check = InterlayerCheck.Compute(stage, r, scaling, subPhi, Vec3.UnitZ, o.Vertical, o.Strength, o.Bond);
                for (int e = 0; e < faces.Length; e++) res.Utilisation[faces[e]] = check.Utilisation[e];
                var valid = check.Utilisation.Where(u => !double.IsNaN(u)).ToArray();
                res.MaxUtilisation = valid.Length > 0 ? valid.Max() : double.NaN;
            }
            return res;
        }
    }
}
