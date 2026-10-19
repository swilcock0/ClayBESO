using System;
using System.Collections.Generic;
using ClayBeso.Fem;
using ClayBeso.GH.Common;
using ClayBeso.Printing;
using Grasshopper.Kernel;

namespace ClayBeso.GH.Components
{
    public class LayerHeightComponent : LegendComponent
    {
        public LayerHeightComponent()
            : base("Layer Heights", "LayerH",
                "Turn a wall-thickness field (e.g. from Shell BESO) into a target layer height per vertex.\n" +
                "Mapping 0 = constant bead area: fixed flow and feed, so a lower layer squashes the bead " +
                "wider. Thick walls get thin layers.\n" +
                "Mapping 1 = linear: thickest wall gets hMin, thinnest gets hMax.\n" +
                "Mapping 2 = constant aspect ratio h = k·t: every bead is squashed the same amount. Needs " +
                "flow control: use the Flow Factor output of Non-planar Layers.",
                Tab.Name, Tab.Print) { }

        public override Guid ComponentGuid => new Guid("e2b5a7c6-95f8-4f42-8da5-4a6f1b3e8c05");
        protected override System.Drawing.Bitmap Icon => Icons.Get("LayerHeights");

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell model (for the mesh).", GH_ParamAccess.item);
            p.AddNumberParameter("Thickness", "t", "Wall thickness per face [mm].", GH_ParamAccess.list);
            p.AddNumberParameter("Min Layer Height", "hMin", "Smallest layer height [mm]. About 0.4× nozzle diameter (0.6 mm for 1.5 mm, 1.2 mm for 3 mm).", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Max Layer Height", "hMax", "Largest stable layer height [mm]. About 0.8× nozzle diameter (1.2 mm for 1.5 mm, 2.4 mm for 3 mm).", GH_ParamAccess.item, 2.5);
            p.AddIntegerParameter("Mapping", "Map", "0 = constant bead area, 1 = linear inverse, 2 = constant aspect ratio (h = k·t).", GH_ParamAccess.item, 0);
            p.AddNumberParameter("Bead Area", "A", "Bead cross-section [mm²] for mapping 0. 0 = automatic.", GH_ParamAccess.item, 0.0);
            p.AddIntegerParameter("Smoothing", "Sm", "Smoothing passes on the height field.", GH_ParamAccess.item, 5);
            p.AddNumberParameter("Aspect Ratio", "k", "Layer height / bead width for mapping 2. Clay beads are typically 0.3-0.5.", GH_ParamAccess.item, 0.4);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the layer-height colour scale [mm]. Empty = hMin to hMax.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            for (int i = 2; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("Layer Heights", "h", "Target layer height per vertex [mm].", GH_ParamAccess.list);
            p.AddNumberParameter("Vertex Thickness", "tv", "Wall thickness averaged to the vertices [mm] (= bead width).", GH_ParamAccess.list);
            p.AddNumberParameter("Bead Area", "A", "Bead cross-section used [mm²].", GH_ParamAccess.item);
            p.AddMeshParameter("Preview", "P", "Mesh coloured by layer height.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object modelObj = null;
            var t = new List<double>();
            double hMin = 1.0, hMax = 2.5, area = 0, k = 0.4;
            int map = 0, smooth = 5;
            if (!da.GetData(0, ref modelObj)) return;
            if (!da.GetDataList(1, t)) return;
            da.GetData(2, ref hMin);
            da.GetData(3, ref hMax);
            da.GetData(4, ref map);
            da.GetData(5, ref area);
            da.GetData(6, ref smooth);
            da.GetData(7, ref k);
            bool legend = true;
            da.GetData(9, ref legend);
            var range = ReadRange(da, 8, (hMin, hMax));

            var model = RhinoConvert.Unwrap<ShellModel>(modelObj);
            if (model == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a Shell Model."); return; }

            try
            {
                var tf = RhinoConvert.Expand(t, model.ElementCount, "Thickness");
                var h = LayerHeightField.Compute(model.Mesh, tf, hMin, hMax,
                    map == 1 ? LayerMapping.LinearInverse : map == 2 ? LayerMapping.ConstantAspectRatio : LayerMapping.ConstantBeadArea,
                    area, smooth, out double used, k);
                da.SetDataList(0, h);
                da.SetDataList(1, model.Mesh.FaceToVertex(tf));
                da.SetData(2, used);
                da.SetData(3, Preview.VertexColoured(model.Mesh, h, model.UnitScale, out var usedRange, range));
                SetLegend("Layer height", "mm", usedRange, legend);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }
    }
}
