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
                "Mapping 3 (default) = proportional: thinnest wall gets hMin, thickest gets hMax. More demand " +
                "means a wider bead AND a taller layer, with the flow raised to supply both (Flow Factor " +
                "output of Non-planar Layers).\n" +
                "Mapping 2 = constant aspect ratio h = k·t: also rises with thickness, same bead shape everywhere.\n" +
                "Mappings 0 and 1 are for fixed flow: 0 = constant bead area (thick walls by squashing the bead " +
                "with a lower layer), 1 = linear inverse (thickest wall gets hMin).",
                Tab.Name, Tab.Print) { }

        public override Guid ComponentGuid => new Guid("e2b5a7c6-95f8-4f42-8da5-4a6f1b3e8c05");
        protected override System.Drawing.Bitmap Icon => Icons.Get("LayerHeights");

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell model (for the mesh).", GH_ParamAccess.item);
            p.AddNumberParameter("Thickness", "t", "Wall thickness per face [mm].", GH_ParamAccess.list);
            p.AddNumberParameter("Min Layer Height", "hMin", "Smallest layer height [mm]. About 0.4× nozzle diameter (0.6 mm for 1.5 mm, 1.2 mm for 3 mm).", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Max Layer Height", "hMax", "Largest stable layer height [mm]. About 0.8× nozzle diameter (1.2 mm for 1.5 mm, 2.4 mm for 3 mm).", GH_ParamAccess.item, 2.5);
            p.AddIntegerParameter("Mapping", "Map", "3 = proportional (default: thicker wall, taller layer), 2 = constant aspect ratio (h = k·t), 0 = constant bead area (fixed flow), 1 = linear inverse (fixed flow).", GH_ParamAccess.item, 3);
            p.AddNumberParameter("Bead Area", "A", "Bead cross-section [mm²] for mapping 0. 0 = automatic.", GH_ParamAccess.item, 0.0);
            p.AddIntegerParameter("Smoothing", "Sm", "Smoothing passes on the height field.", GH_ParamAccess.item, 5);
            p.AddNumberParameter("Aspect Ratio", "k", "Layer height / bead width for mapping 2. Clay beads are typically 0.3-0.5.", GH_ParamAccess.item, 0.4);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the layer-height colour scale [mm]. Empty = hMin to hMax.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            p.AddIntervalParameter("Thickness Range", "tR", "Mappings 1 and 3: thickness interval [mm] mapped onto hMin..hMax, e.g. BESO's tMin To tMax. Empty = min-max of the field (a nearly uniform wall would then still use the full height range).", GH_ParamAccess.item);
            p.AddGenericParameter("Layer Rule", "Rule", "Optional Layer Rule. When connected it replaces Mapping, hMin, hMax, Bead Area, Aspect Ratio and Thickness Range, so these heights match what Shell BESO assumed.", GH_ParamAccess.item);
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
            int map = 3, smooth = 5;
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
            var tRange = ReadRange(da, 10, null);
            object ruleObj = null;
            da.GetData(11, ref ruleObj);
            var rule = RhinoConvert.Unwrap<LayerRule>(ruleObj);
            if (rule != null) { hMin = rule.HMin; hMax = rule.HMax; range = ReadRange(da, 8, (hMin, hMax)); }
            if (rule != null && rule.ThicknessRange == null &&
                (rule.Mapping == LayerMapping.Proportional || rule.Mapping == LayerMapping.LinearInverse))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "The Layer Rule has no Thickness Range, so here it is taken from this thickness field, while Shell BESO " +
                    "uses its tMin..tMax. Set the Thickness Range on the Layer Rule so both use the same mapping.");

            var model = RhinoConvert.Unwrap<ShellModel>(modelObj);
            if (model == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a Shell Model."); return; }

            try
            {
                var tf = RhinoConvert.Expand(t, model.ElementCount, "Thickness");
                var h = rule != null
                    ? LayerHeightField.Compute(model.Mesh, tf, rule, smooth, out double used)
                    : LayerHeightField.Compute(model.Mesh, tf, hMin, hMax, ToMapping(map), area, smooth, out used, k, tRange);
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

        internal static LayerMapping ToMapping(int map)
        {
            switch (map)
            {
                case 0: return LayerMapping.ConstantBeadArea;
                case 1: return LayerMapping.LinearInverse;
                case 2: return LayerMapping.ConstantAspectRatio;
                case 3: return LayerMapping.Proportional;
                default: throw new ArgumentException($"Mapping must be 0-3, got {map}.");
            }
        }
    }
}
