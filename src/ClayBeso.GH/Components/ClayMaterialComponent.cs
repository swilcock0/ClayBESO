using System;
using System.Collections.Generic;
using ClayBeso.Fem;
using ClayBeso.GH.Common;
using Grasshopper.Kernel;

namespace ClayBeso.GH.Components
{
    public class ClayMaterialComponent : ClayComponent
    {
        public ClayMaterialComponent()
            : base("Material", "Material",
                "Linear-elastic, isotropic material for the shell analysis. Right-click for presets (Clay, Wood), which " +
                "fill in the text boxes connected to the inputs. Defaults are for green (wet) clay, the state that " +
                "governs self-weight collapse while printing.",
                Tab.Name, Tab.Model) { }

        public override Guid ComponentGuid => new Guid("a3c1e9d2-51b4-4b0e-8f61-0c2b7d9a4e01");
        protected override System.Drawing.Bitmap Icon => Icons.Get("ClayMaterial");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override IEnumerable<Preset> Presets => new[]
        {
            // Green printing clay (default). Strength: see the Strength input.
            new Preset("Clay (green)",
                ("Young's Modulus", 1.0), ("Poisson", 0.35), ("Density", 1900), ("Strength", 0.03)),
            // Softwood (spruce/pine, strength class C24, EN 338 mean values), as an isotropic approximation
            // with the along-grain modulus. Strength = shear strength, a cautious single value between
            // tension across the grain (0.4 MPa) and bending along it (24 MPa).
            new Preset("Wood (softwood C24)",
                ("Young's Modulus", 11000), ("Poisson", 0.3), ("Density", 420), ("Strength", 4.0)),
        };

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddNumberParameter("Young's Modulus", "Modulus", "Young's modulus [MPa]. Green clay ~0.1-5 MPa, leather-hard ~10-50 MPa, fired ~10 000-30 000 MPa; softwood along the grain ~11 000 MPa (preset Wood).", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Poisson", "ν", "Poisson's ratio [-], 0 to <0.5.", GH_ParamAccess.item, 0.35);
            p.AddNumberParameter("Density", "Density", "Density [kg/m³]. Clay ~1900, softwood ~420.", GH_ParamAccess.item, 1900);
            p.AddNumberParameter("Strength", "Strength", "Strength f [MPa] for the interlayer and utilisation checks. Default 0.03 MPa is a cautious PLACEHOLDER for soft printing clay: about half the unconfined compressive strength printing clays show on a pocket penetrometer (0.6-1.3 kg/cm² = 0.06-0.13 MPa, Friedman-Gerlicz et al. 2024), because interfaces fail in shear and tension. Measure your own clay: penetrometer reading [kg/cm²] × 0.098 = MPa, then halve it (docs/VALIDATION.md §2). The Wood preset uses 4 MPa (C24 shear strength). 0 = no strength checks.", GH_ParamAccess.item, 0.03);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("Material", "Material", "Material for Shell Model.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            double e = 1, nu = 0.35, rho = 1900, f = 0.03;
            da.GetData(In("Young's Modulus"), ref e);
            da.GetData(In("Poisson"), ref nu);
            da.GetData(In("Density"), ref rho);
            da.GetData(In("Strength"), ref f);
            try { da.SetData(Out("Material"), new ClayMaterial(e, nu, rho, f)); }
            catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); }
        }
    }
}
