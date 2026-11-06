using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.GH.Common;
using ClayBeso.Printing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace ClayBeso.GH.Components
{
    public class CheckLayersComponent : LegendComponent
    {
        public CheckLayersComponent()
            : base("Check Layers", "Check Layers",
                "Checks layers you designed yourself. Give the points of each layer (one branch per layer, lowest first, " +
                "or one spiral) and, if you have them, the layer heights. It builds the wall through your points and runs " +
                "every check Clay Print does: layer height limits, layer contact, overhang, bead support and nozzle tilt at " +
                "every point, and the structure (bond between the layers, every print stage, buckling) with the base on " +
                "the bed under its own weight.\n" +
                "Faces that fail are grouped into areas: Issues says what is wrong and what to change, Issue Points and " +
                "Issue Areas show where. Your layers are not changed or re-sliced, so change your definition and watch " +
                "the issues go.",
                Tab.Name, Tab.Start) { }

        public override Guid ComponentGuid => new Guid("a9af5ab1-4a15-4a44-bb7d-83d9e4688711");
        protected override Bitmap Icon => Icons.Get("PrintCheck");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        private static readonly (string, int)[] WallOptions =
        {
            ("One bead", 0),
            ("Ideal thickness", 1),
        };

        private static readonly (string, int)[] ShowOptions =
        {
            ("Problems", 0),
            ("Layer height", 1),
            ("Layer contact", 2),
            ("Wall lean", 3),
            ("Bead support", 4),
            ("Layer tilt", 5),
            ("Layer bond", 6),
            ("While printing", 7),
            ("Buckling shape", 8),
            ("Wall thickness", 9),
            ("Height vs spacing", 10),
        };

        protected override IEnumerable<Dropdown> Dropdowns => new[]
        {
            new Dropdown("Printer", 0, PrinterProfile.Presets.Select((p, i) => (p.Name, i)).ToArray()),
            new Dropdown("Wall", 0, WallOptions),
            new Dropdown("Show", 0, ShowOptions),
        };

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddPointParameter("Points", "Points", "The points of your layers, in print order (document units): one branch per layer, " +
                "lowest layer first (e.g. {0;0}, {0;1}, ...). A closed layer may repeat its first point at the end. " +
                "One branch that winds round several times is read as a spiral and split into turns.", GH_ParamAccess.tree);
            p.AddNumberParameter("Heights", "Heights", "Optional: the layer height at each point [mm], same branches as Points " +
                "(or one value per layer, one value for all, or one flat list in the same order). It is the bead's own height: " +
                "the distance down to the layer below. Empty = measured from the spacing of your layers.", GH_ParamAccess.tree);
            p.AddIntegerParameter("Printer", "Printer", "Printer and nozzle (a dropdown appears when you place the component). Sets the bead " +
                "width (1.5 × nozzle), layer height, overhang and tilt limits.", GH_ParamAccess.item, 0);
            p.AddIntegerParameter("Wall", "Wall", "0 = one bead everywhere (1.5 × nozzle wide). 1 = ideal thickness: the wall the optimisation " +
                "gives this shape with the same clay, within the printer's wall range. Where it is wider than one bead you need a " +
                "woven (back-and-forth) path; Show = Wall thickness shows where.", GH_ParamAccess.item, 0);
            p.AddIntegerParameter("Show", "Show", "What the Mesh is coloured by: 0 = problems (worst check, 1 = at its limit), 1 = layer height, " +
                "2 = layer contact (height / bead width), 3 = wall lean, 4 = bead support, 5 = layer tilt, 6 = layer bond, " +
                "7 = while printing, 8 = buckling shape, 9 = wall thickness, 10 = given height minus the space to the layer below " +
                "(below 0 = gap under the bead, above 0 = squashed; dropdown appears when placed).", GH_ParamAccess.item, 0);
            p.AddGenericParameter("Material", "Material", "Optional: a Material component. Default: green clay (E = 1 MPa, 1900 kg/m³, " +
                "strength 0.03 MPa placeholder).", GH_ParamAccess.item);
            p.AddIntervalParameter("Colour Range", "Colour Range", "Fix the colour scale. Empty = automatic for what is shown.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Legend", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddIntegerParameter("Score", "Score", "Printability 1-100, as in Clay Print. Use it to compare versions of the design.", GH_ParamAccess.item);
            p.AddTextParameter("Issues", "Issues", "One line per problem area, worst first: what fails, in which layers (branch numbers), " +
                "the measured value against its limit, and what to change.", GH_ParamAccess.list);
            p.AddPointParameter("Issue Points", "Issue Points", "Worst point of each problem area (document units), in the same order as Issues.", GH_ParamAccess.list);
            p.AddMeshParameter("Issue Areas", "Issue Areas", "The faces of each problem area (document units), in the same order as Issues. " +
                "Use List Item to look at one.", GH_ParamAccess.list);
            p.AddMeshParameter("Mesh", "Mesh", "The wall through your points (document units), coloured by Show.", GH_ParamAccess.item);
            p.AddTextParameter("Report", "Report", "Each aspect of the score, weakest first, with what to change.", GH_ParamAccess.item);
            p.AddTextParameter("Info", "Info", "What was checked: layers, mesh, printer, wall, buckling, notes.", GH_ParamAccess.item);
            p.AddNumberParameter("Values", "Values", "Per face of Mesh: the shown quantity (NaN where undefined).", GH_ParamAccess.list);
            p.AddGenericParameter("Print", "Print", "For advanced use: the print (connect to Print Playback).", GH_ParamAccess.item);
            p.AddGenericParameter("Check", "Check", "For advanced use: the check result (connect to Print Playback).", GH_ParamAccess.item);
        }

        // Changing only Show, Colour Range or Legend must not re-run the analysis.
        private LayerReviewResult _result;
        private string _key;
        private double _seconds;

        protected override void SolveInstance(IGH_DataAccess da)
        {
            int printerIndex = 0, wall = 0, show = 0;
            object matObj = null;
            bool legend = true;
            if (!da.GetDataTree(In("Points"), out GH_Structure<GH_Point> ptsTree) || ptsTree == null || ptsTree.DataCount == 0) return;
            da.GetDataTree(In("Heights"), out GH_Structure<GH_Number> hTree);
            da.GetData(In("Printer"), ref printerIndex);
            da.GetData(In("Wall"), ref wall);
            da.GetData(In("Show"), ref show);
            da.GetData(In("Material"), ref matObj);
            da.GetData(In("Legend"), ref legend);

            try
            {
                if (printerIndex < 0 || printerIndex >= PrinterProfile.Presets.Count)
                    throw new ArgumentException($"Printer must be 0-{PrinterProfile.Presets.Count - 1}.");
                if (wall < 0 || wall > 1) throw new ArgumentException("Wall must be 0 (one bead) or 1 (ideal thickness).");
                var printer = PrinterProfile.Presets[printerIndex];
                var material = RhinoConvert.Unwrap<ClayMaterial>(matObj) ?? new ClayMaterial(1.0, 0.35, 1900, 0.03);
                double s = RhinoConvert.DocToMm();

                var layers = new List<IList<Vec3>>();
                foreach (var branch in ptsTree.Branches)
                {
                    var pts = branch.Where(g => g != null).Select(g => RhinoConvert.ToVec(g.Value, s)).ToList();
                    if (pts.Count > 0) layers.Add(pts);
                }
                var heights = Heights(hTree, layers);

                string key = Key(layers, heights, printerIndex, wall, material);
                if (_result == null || key != _key)
                {
                    _result = null;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = LayerReview.Run(layers, heights, material, printer, new LayerReviewOptions { Wall = (WallMode)wall });
                    r.Model.UnitScale = s;
                    _seconds = sw.Elapsed.TotalSeconds;
                    _result = r;
                    _key = key;
                }
                var res = _result;

                da.SetData(Out("Score"), res.Score.Total);
                da.SetDataList(Out("Issues"), res.Issues.Select((i, n) => $"{n + 1}. {i}"));
                da.SetDataList(Out("Issue Points"), res.Issues.Select(i => RhinoConvert.ToPoint(i.Location, s)));
                da.SetDataList(Out("Issue Areas"), res.Issues.Select(i => AreaMesh(res.Mesh, i.Faces, s)));

                var (values, title, unit, auto) = Shown(res, show);
                var range = ReadRange(da, "Colour Range", auto);
                da.SetData(Out("Mesh"), Preview.FaceColoured(res.Mesh, values, s, out var used, range));
                SetLegend(title, unit, used, legend);
                da.SetDataList(Out("Values"), values);
                da.SetData(Out("Report"), res.Score.Report());
                da.SetData(Out("Info"), Info(res));
                da.SetData(Out("Print"), res.Plan);
                da.SetData(Out("Check"), res.Check);

                if (res.Issues.Count > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"{res.Issues.Count} problem area{(res.Issues.Count > 1 ? "s" : "")}: " +
                        string.Join(", ", res.Issues.GroupBy(i => i.Title).Select(g => $"{g.Key} ({g.Count()})")) + ". See Issues.");
                foreach (var w in res.Warnings.Where(w => w.Contains("differ")))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, w);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        /// <summary>
        /// Heights tree → one list per layer: same branches as Points, one value per layer, one value for all,
        /// or one flat list in point order. Null when nothing is connected.
        /// </summary>
        private static IList<IList<double>> Heights(GH_Structure<GH_Number> tree, List<IList<Vec3>> layers)
        {
            if (tree == null || tree.DataCount == 0) return null;
            var branches = tree.Branches.Select(b => (IList<double>)b.Where(g => g != null).Select(g => g.Value).ToList())
                .Where(b => b.Count > 0).ToList();
            var all = branches.SelectMany(b => b).ToList();
            int points = layers.Sum(l => l.Count);
            if (branches.Count == layers.Count) return branches;
            if (all.Count == 1) return layers.Select(_ => (IList<double>)new[] { all[0] }).ToList();
            if (all.Count == layers.Count) return all.Select(h => (IList<double>)new[] { h }).ToList();
            if (all.Count == points)
            {
                var result = new List<IList<double>>();
                int at = 0;
                foreach (var l in layers) { result.Add(all.GetRange(at, l.Count)); at += l.Count; }
                return result;
            }
            throw new ArgumentException($"Heights doesn't match Points: {branches.Count} branches with {all.Count} values, but Points has " +
                $"{layers.Count} layers with {points} points. Give one height per point (same branches), one per layer, or one for all.");
        }

        private static (double[] values, string title, string unit, (double, double)? range) Shown(LayerReviewResult r, int show)
        {
            var p = r.Printer;
            switch (show)
            {
                case 0: return (r.Severity, "Worst check (1 = at its limit)", "× limit", (0.0, 2.0));
                case 1: return (r.LayerHeight, "Layer height", "mm", (p.MinLayer, p.MaxLayer));
                case 2: return (r.ContactRatio, "Layer height / bead width", "-", (0.0, PrintPlan.PoorContactRatio));
                case 3: return (r.Lean, "Wall lean", "°", (0.0, p.MaxOverhang));
                case 4: return (r.BeadOffset, "Bead past the one below", "× width", (0.0, 0.5));
                case 5: return (r.Tilt, "Layer tilt along the path", "°", (0.0, p.MaxTilt));
                case 6: return (r.Interlayer, "Interlayer utilisation", "-", (0.0, 1.0));
                case 7: return (r.StageUtilisation, "Interlayer utilisation while printing", "-", (0.0, 1.0));
                case 8: return (r.BucklingShape, "Buckling shape" + (double.IsNaN(r.BucklingFactor) ? "" : $" (factor {r.BucklingFactor:G3})"), "-", (0.0, 1.0));
                case 9: return (r.Thickness, "Wall thickness", "mm", (p.MinWall, p.MaxWall));
                case 10:
                {
                    double m = r.HeightVsSpacing.Where(x => !double.IsNaN(x)).Select(Math.Abs).DefaultIfEmpty(0).Max();
                    m = Math.Max(m, 0.05);
                    return (r.HeightVsSpacing, "Height − space below (< 0 = gap)", "mm", (-m, m));
                }
                default: throw new ArgumentException("Show must be 0-10.");
            }
        }

        /// <summary>The faces of one problem area, in red.</summary>
        private static Mesh AreaMesh(TriMesh mesh, int[] faces, double scale)
        {
            var m = new Mesh();
            var used = new Dictionary<int, int>();
            foreach (int f in faces)
            {
                var ids = mesh.Faces[f].Select(v =>
                {
                    if (!used.TryGetValue(v, out int id))
                    {
                        var q = mesh.Vertices[v];
                        id = used[v] = m.Vertices.Add(q.X / scale, q.Y / scale, q.Z / scale);
                        m.VertexColors.Add(Color.FromArgb(220, 30, 30));
                    }
                    return id;
                }).ToArray();
                m.Faces.AddFace(ids[0], ids[1], ids[2]);
            }
            m.Normals.ComputeNormals();
            return m;
        }

        private static string Key(List<IList<Vec3>> layers, IList<IList<double>> heights, int printer, int wall, ClayMaterial m)
        {
            double h = 0;
            int i = 0;
            foreach (var l in layers)
                foreach (var p in l) { i++; h += (i % 7 + 1) * p.X + (i % 11 + 1) * p.Y + (i % 13 + 1) * p.Z; }
            double hh = 0;
            if (heights != null) foreach (var l in heights) foreach (var x in l) { i++; hh += (i % 5 + 1) * x; }
            return $"{layers.Count}/{i}/{h:R}/{hh:R}/{heights != null}/{printer}/{wall}/{m.E:R}/{m.Nu:R}/{m.Density:R}/{m.Strength:R}";
        }

        private string Info(LayerReviewResult r)
        {
            var lines = new List<string>
            {
                r.Printer.ToString(),
                $"{r.LayerCount} {(r.Spiral ? "spiral turns" : "layers")}, {r.Paths.Sum(p => p.Points.Count)} points; wall {r.Mesh.FaceCount} faces; computed in {_seconds:F1} s",
                r.Wall == WallMode.Ideal
                    ? $"Ideal wall {r.Design.Thickness.Min():F1}-{r.Design.Thickness.Max():F1} mm after {r.Iterations} iterations{(r.Converged ? "" : " (not converged)")}: " +
                      $"{1 / r.ComplianceRatio - 1:P0} stiffer than one {r.Printer.BeadWidth:G3} mm bead with the same clay. " +
                      $"{r.Thickness.Count(t => t > 1.15 * r.Printer.BeadWidth) / (double)r.Thickness.Length:P0} of the wall wants more than one bead."
                    : $"One bead {r.Printer.BeadWidth:G3} mm wide everywhere (1.5 × nozzle, a placeholder: measure your bead)",
                double.IsNaN(r.BucklingFactor) ? "Buckling: not computed"
                    : double.IsInfinity(r.BucklingFactor) ? "Buckling: nothing in compression"
                    : $"Buckling factor {r.BucklingFactor:G3} ({(r.BucklingLayer >= 0 ? $"printed up to layer {r.BucklingLayer}" : "finished piece")}; aim for {LayerReview.BucklingMargin:G2}+)",
                r.Issues.Count == 0 ? "No problem areas found." : $"{r.Issues.Count} problem areas (see Issues).",
                r.SquashedPoints == 0 ? null
                    : $"Note: at {r.SquashedPoints} points the given height is more than {LayerReview.SpacingTolerance:P0} taller than the space " +
                      "below, so the bead is pushed into the one below (extra clay; not counted as a problem). Show = Height vs spacing shows where.",
            };
            lines.RemoveAll(l => l == null);
            lines.AddRange(r.Warnings);
            lines.Add("Limits are placeholders until calibrated (docs/VALIDATION.md). Only the base layer is on the bed; a floor is not modelled.");
            return string.Join("\n", lines);
        }
    }
}
