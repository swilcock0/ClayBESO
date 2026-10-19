using System;
using System.Collections.Generic;
using ClayBeso.Fem;
using ClayBeso.GH.Common;
using Grasshopper.Kernel;

namespace ClayBeso.GH.Components
{
    public class WallPreviewComponent : GH_Component
    {
        public WallPreviewComponent()
            : base("Wall Preview", "Wall",
                "Thicken the mid-surface by the per-face thickness to preview the printed wall as a solid.",
                Tab.Name, Tab.Print) { }

        public override Guid ComponentGuid => new Guid("0a7d9e18-b71a-4164-8fa7-6c8b3d5a0e07");
        protected override System.Drawing.Bitmap Icon => Icons.Get("WallPreview");
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell Model, or the Design output of Shell BESO.", GH_ParamAccess.item);
            p.AddNumberParameter("Thickness", "Thickness", "Wall thickness per face [mm]. Default: the design's or model's thickness.", GH_ParamAccess.list);
            p[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddMeshParameter("Wall", "Wall", "Closed mesh of the thickened wall (document units).", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object modelObj = null;
            var t = new List<double>();
            if (!da.GetData(0, ref modelObj)) return;
            da.GetDataList(1, t);
            try
            {
                var design = PrintLayersComponent.ToDesign(modelObj, t);
                var model = design.Model;
                da.SetData(0, Preview.Thicken(model.Mesh, model.Mesh.FaceToVertex(design.Thickness), model.UnitScale));
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }
    }
}
