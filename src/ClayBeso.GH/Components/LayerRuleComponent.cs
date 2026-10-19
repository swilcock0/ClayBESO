using System;
using System.Collections.Generic;
using ClayBeso.GH.Common;
using ClayBeso.Printing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace ClayBeso.GH.Components
{
    public class LayerRuleComponent : ClayComponent
    {
        public LayerRuleComponent()
            : base("Layer Rule", "Rule",
                "How wall thickness turns into layer height, as one object shared by Layer Heights, Shell BESO and " +
                "Shell Analysis. Sharing it means BESO optimises with the same layer heights you will print " +
                "(needed for the across-layer stiffness).",
                Tab.Name, Tab.Print) { }

        public override Guid ComponentGuid => new Guid("9e41c2d7-38b5-4f06-a1d9-7c5e2b8f0a63");

        protected override IEnumerable<Dropdown> Dropdowns => new[] { new Dropdown("Mapping", 3, Modes.Mapping) };
        protected override System.Drawing.Bitmap Icon => Icons.Get("LayerRule");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddIntegerParameter("Mapping", "Mapping", "3 = proportional (thicker wall, taller layer), 2 = constant aspect ratio (h = k·t), 0 = constant bead area (fixed flow), 1 = linear inverse (fixed flow).", GH_ParamAccess.item, 3);
            p.AddNumberParameter("Min Layer Height", "Min Height", "Smallest layer height [mm]. At least 0.25× nozzle diameter (0.75 mm for a 3 mm nozzle).", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Max Layer Height", "Max Height", "Largest layer height [mm]. At most 0.75× nozzle diameter (2.25 mm for a 3 mm nozzle).", GH_ParamAccess.item, 2.25);
            p.AddNumberParameter("Bead Area", "A", "Bead cross-section [mm²] for mapping 0. 0 = automatic.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Aspect Ratio", "k", "Layer height / bead width for mapping 2.", GH_ParamAccess.item, 0.4);
            p.AddIntervalParameter("Thickness Range", "Thickness Range", "Mappings 1 and 3: thickness [mm] mapped onto hMin..hMax. Empty = BESO's tMin..tMax inside BESO, or the field's own range elsewhere. Set it explicitly to make every component use the same mapping.", GH_ParamAccess.item);
            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("Layer Rule", "Layer Rule", "Layer rule: connect it to Shell BESO (it then travels on in the Design) or to Print Layers.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            int map = 3;
            double hMin = 1.0, hMax = 2.25, area = 0, k = 0.4;
            var iv = Interval.Unset;
            da.GetData(0, ref map);
            da.GetData(1, ref hMin);
            da.GetData(2, ref hMax);
            da.GetData(3, ref area);
            da.GetData(4, ref k);
            da.GetData(5, ref iv);
            try
            {
                (double, double)? range = iv.IsValid && Math.Abs(iv.Length) > 1e-12
                    ? (Math.Min(iv.T0, iv.T1), Math.Max(iv.T0, iv.T1))
                    : ((double, double)?)null;
                da.SetData(0, new LayerRule(LayerHeightComponent.ToMapping(map), hMin, hMax, area, k, range));
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }
    }
}
