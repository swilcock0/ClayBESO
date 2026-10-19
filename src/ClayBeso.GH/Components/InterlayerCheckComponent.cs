using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.Geometry;
using ClayBeso.GH.Common;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace ClayBeso.GH.Components
{
    public class InterlayerCheckComponent : LegendComponent
    {
        public InterlayerCheckComponent()
            : base("Interlayer Check", "Interlayer",
                "Check the bond between printed layers. Stresses are split into along-layer and across-layer " +
                "using the real (non-planar) layer directions. Each interface carries them only through its " +
                "bonded strip, c = (w - h)/w of the wall (bead width w = wall thickness, h = layer height), so " +
                "taller layers on the same wall bond over less.\n" +
                "Utilisation = worst of bond failure (tension + shear vs η·f) and bearing of the narrowed strip " +
                "(compression vs f). Above 1 = expected to fail.",
                Tab.Name, Tab.Analyse) { }

        public override Guid ComponentGuid => new Guid("5c2e8f1a-7d34-4b9e-a6c1-2f8e0d4b7a19");
        protected override System.Drawing.Bitmap Icon => Icons.Get("InterlayerCheck");
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell model.", GH_ParamAccess.item);
            p.AddNumberParameter("Thickness", "t", "Wall thickness per face [mm] (e.g. from Shell BESO). Default: the model's thickness.", GH_ParamAccess.list);
            p.AddNumberParameter("Layer Field", "φ", "Layer field per vertex, from the Layer Field output of Non-planar Layers.", GH_ParamAccess.list);
            p.AddBooleanParameter("Vertical", "Vz", "Same setting as on Non-planar Layers (layer height measured in Z).", GH_ParamAccess.item, true);
            p.AddNumberParameter("Strength", "f", "Bulk clay strength [MPa]. 0 = use the Strength of the Clay Material.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Bond Efficiency", "η", "Interface strength per unit contact area relative to the bulk (0-1). Calibrate from tests; 1 = only the contact geometry weakens the interface.", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Min Contact", "cMin", "Lower bound on the contact fraction (a round bead still touches over a small strip).", GH_ParamAccess.item, 0.05);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the utilisation colour scale. Empty = 0 to the maximum (or 1 if lower).", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            p.AddBooleanParameter("Orthotropic", "Orth", "Analyse the wall as weaker across its layers (stiffness × η·c, with c from the realised layer heights), so stresses redistribute as they would in the print. False = isotropic wall, stresses only checked at the interfaces.", GH_ParamAccess.item, true);
            p[1].Optional = true;
            for (int i = 3; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("Utilisation", "U", "Per face: worst of bond and bearing utilisation (NaN where the layer direction is undefined, e.g. a flat floor).", GH_ParamAccess.list);
            p.AddNumberParameter("Bond Utilisation", "Ub", "Per face: sqrt(σn+² + τ²) / (η f) on the contact strip.", GH_ParamAccess.list);
            p.AddNumberParameter("Bearing Utilisation", "Uc", "Per face: compression on the contact strip / f.", GH_ParamAccess.list);
            p.AddNumberParameter("Contact", "c", "Per face: bonded fraction of the wall, (w - h)/w.", GH_ParamAccess.list);
            p.AddNumberParameter("Layer Height", "h", "Per face: realised layer height [mm].", GH_ParamAccess.list);
            p.AddNumberParameter("Tension", "σt", "Per face: largest tensile stress across the interface [MPa].", GH_ParamAccess.list);
            p.AddNumberParameter("Compression", "σc", "Per face: largest compressive stress across the interface [MPa].", GH_ParamAccess.list);
            p.AddNumberParameter("Shear", "τ", "Per face: largest shear stress along the interface [MPa].", GH_ParamAccess.list);
            p.AddMeshParameter("Preview", "P", "Mesh coloured by utilisation (grey = not checked).", GH_ParamAccess.item);
            p.AddPointParameter("Worst Point", "Umax", "Centre of the face with the highest utilisation.", GH_ParamAccess.item);
            p.AddTextParameter("Info", "I", "Summary.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object modelObj = null;
            var thick = new List<double>();
            var phi = new List<double>();
            bool vertical = true, legend = true;
            double strength = 0, eta = 1.0, cMin = 0.05;
            if (!da.GetData(0, ref modelObj)) return;
            da.GetDataList(1, thick);
            if (!da.GetDataList(2, phi)) return;
            da.GetData(3, ref vertical);
            da.GetData(4, ref strength);
            da.GetData(5, ref eta);
            da.GetData(6, ref cMin);
            da.GetData(8, ref legend);
            bool orthotropic = true;
            da.GetData(9, ref orthotropic);

            var model = RhinoConvert.Unwrap<ShellModel>(modelObj);
            if (model == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a Shell Model."); return; }

            try
            {
                if (strength <= 0) strength = model.Material.Strength;
                if (strength <= 0)
                    throw new ArgumentException("Set a clay strength f, here or on the Clay Material component.");

                var t = thick.Count > 0 ? RhinoConvert.Expand(thick, model.ElementCount, "Thickness") : model.Thickness.ToList();
                if (t.Any(x => x <= 0)) throw new ArgumentException("Thickness must be positive.");
                var scaling = ElementScaling.FromThickness(t);
                var bond = new BondModel(eta, cMin);
                if (orthotropic)
                {
                    // Weaker across the layers, by the contact of the layers actually printed.
                    model.EnableLayerAnisotropy(Vec3.UnitZ);
                    var hReal = Printing.LayerField.RealisedFaceHeight(model.Mesh, phi, Vec3.UnitZ, vertical);
                    for (int e = 0; e < t.Count; e++)
                        if (!double.IsInfinity(hReal[e]) && !double.IsNaN(hReal[e]))
                            scaling.Across[e] = bond.Efficiency * bond.Contact(t[e], hReal[e]);
                }
                var r = new ShellSolver(model).Solve(scaling);
                var check = InterlayerCheck.Compute(model, r, scaling, phi, Vec3.UnitZ, vertical, strength, bond);

                var u = check.Utilisation;
                var valid = Enumerable.Range(0, u.Length).Where(check.IsValid).ToArray();
                if (valid.Length == 0) throw new ArgumentException("No face has a defined layer direction. Is the Layer Field from this model?");
                int worst = valid.OrderByDescending(e => u[e]).First();
                double uMax = u[worst];
                var range = ReadRange(da, 7, (0.0, Math.Max(1.0, uMax)));
                double s = model.UnitScale;

                da.SetDataList(0, u);
                da.SetDataList(1, check.BondUtilisation);
                da.SetDataList(2, check.BearingUtilisation);
                da.SetDataList(3, check.Contact);
                da.SetDataList(4, check.LayerHeight);
                da.SetDataList(5, check.Tension);
                da.SetDataList(6, check.Compression);
                da.SetDataList(7, check.Shear);
                da.SetData(8, Preview.FaceColoured(model.Mesh, u, s, out var used, range));
                SetLegend("Interlayer utilisation", "-", used, legend);
                var centre = model.Mesh.FaceCentroid(worst);
                da.SetData(9, RhinoConvert.ToPoint(centre, s));

                int failing = valid.Count(e => u[e] > 1);
                string mode = check.BondUtilisation[worst] >= check.BearingUtilisation[worst] ? "bond (tension/shear)" : "bearing (compression)";
                double zMin = model.Mesh.Vertices.Min(v => v.Z), zTop = model.Mesh.Vertices.Max(v => v.Z);
                da.SetData(10,
                    $"Max utilisation {uMax:G3} ({mode}) at face {worst}, {(centre.Z - zMin) / Math.Max(1e-9, zTop - zMin):P0} of the height\n" +
                    $"  wall {t[worst]:G3} mm, layer {check.LayerHeight[worst]:G3} mm, contact {check.Contact[worst]:P0}\n" +
                    $"Faces over 1: {failing} of {valid.Length} checked ({u.Length - valid.Length} without a layer direction)\n" +
                    $"Contact range {valid.Min(e => check.Contact[e]):P0}-{valid.Max(e => check.Contact[e]):P0}, " +
                    $"f = {strength:G3} MPa, η = {eta:G3}");

                if (failing > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{failing} faces exceed the interlayer capacity (utilisation > 1).");
                if (valid.Any(e => check.Contact[e] <= cMin + 1e-9))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        "Some faces have layer height ≥ wall thickness, so the contact is clamped at cMin. Check the layer heights there.");
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }
    }
}
