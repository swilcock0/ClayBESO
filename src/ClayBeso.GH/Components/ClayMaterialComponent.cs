using System;
using ClayBeso.Fem;
using Grasshopper.Kernel;

namespace ClayBeso.GH.Components
{
    public class ClayMaterialComponent : GH_Component
    {
        public ClayMaterialComponent()
            : base("Clay Material", "Clay",
                "Linear-elastic material for the shell analysis. Defaults are for green (wet) clay, " +
                "the state that governs self-weight collapse while printing.",
                Tab.Name, Tab.Model) { }

        public override Guid ComponentGuid => new Guid("a3c1e9d2-51b4-4b0e-8f61-0c2b7d9a4e01");
        protected override System.Drawing.Bitmap Icon => null;
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddNumberParameter("Young's Modulus", "E", "Young's modulus [MPa]. Green clay ~0.1-5 MPa, leather-hard ~10-50 MPa, fired ~10 000-30 000 MPa.", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Poisson", "ν", "Poisson's ratio [-], 0 to <0.5.", GH_ParamAccess.item, 0.35);
            p.AddNumberParameter("Density", "ρ", "Density [kg/m³].", GH_ParamAccess.item, 1900);
            p.AddNumberParameter("Strength", "f", "Optional allowable stress [MPa] for utilisation output. 0 = not used.", GH_ParamAccess.item, 0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("Material", "Mat", "Clay material.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            double e = 1, nu = 0.35, rho = 1900, f = 0;
            da.GetData(0, ref e);
            da.GetData(1, ref nu);
            da.GetData(2, ref rho);
            da.GetData(3, ref f);
            try { da.SetData(0, new ClayMaterial(e, nu, rho, f)); }
            catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); }
        }
    }
}
