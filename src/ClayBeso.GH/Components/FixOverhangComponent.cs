using System;
using System.Linq;
using ClayBeso.GH.Common;
using ClayBeso.Printing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace ClayBeso.GH.Components
{
    public class FixOverhangComponent : LegendComponent
    {
        public FixOverhangComponent()
            : base("Fix Overhang", "Fix Overhang",
                "Pulls overhanging parts of a vase back until the wall leans no more than Max Lean from vertical, so it " +
                "prints without support. Points move only sideways, along the radius from the vase's axis, and only as far " +
                "as needed; the base and parts that are already printable stay put.\n" +
                "Made for vases (closed rings around a vertical axis, open top, no floor). Connect the result to Shell Model " +
                "or Clay Print. A shape that narrows faster than the limit allows ends up wider at the top.",
                Tab.Name, Tab.Model) { }

        public override Guid ComponentGuid => new Guid("54a8f225-c288-4aa1-874c-86ea4587fb38");
        protected override System.Drawing.Bitmap Icon => Icons.Get("FixOverhang");
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddMeshParameter("Mesh", "Mesh", "Mid-surface mesh of the wall, standing on the bed (lowest point = bed), in document units.", GH_ParamAccess.item);
            p.AddNumberParameter("Max Lean", "Max Lean", "Largest wall lean from vertical [deg] (planar clay starts to slump around 45°).", GH_ParamAccess.item, 45.0);
            p.AddNumberParameter("Tolerance", "Tol", "Band above the lowest point that is the base and stays put (document units).", GH_ParamAccess.item, 0.5);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddMeshParameter("Mesh", "Mesh", "The corrected mesh (document units), same vertices and faces.", GH_ParamAccess.item);
            p.AddMeshParameter("Preview", "Preview", "The corrected mesh coloured by how far each point moved.", GH_ParamAccess.item);
            p.AddNumberParameter("Moved", "Moved", "Per vertex: how far it moved (document units).", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "How much moved.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Mesh rmesh = null;
            double maxLean = 45, tol = 0.5;
            bool legend = true;
            if (!da.GetData(In("Mesh"), ref rmesh) || rmesh == null) return;
            da.GetData(In("Max Lean"), ref maxLean);
            da.GetData(In("Tolerance"), ref tol);
            da.GetData(In("Legend"), ref legend);
            try
            {
                double s = RhinoConvert.DocToMm();
                var mesh = RhinoConvert.ToTriMesh(rmesh, s);
                var fix = OverhangFix.Apply(mesh, maxLean, baseTolerance: tol * s);
                var moved = fix.Moved.Select(d => d / s).ToArray();
                da.SetData(Out("Mesh"), RhinoConvert.ToRhino(fix.Mesh, s));
                da.SetData(Out("Preview"), Preview.VertexColoured(fix.Mesh, moved, s, out var used, (0.0, Math.Max(1e-9, moved.Max()))));
                SetLegend("Moved", "document units", used, legend);
                da.SetDataList(Out("Moved"), moved);
                da.SetData(Out("Info"), fix.MovedCount == 0
                    ? $"Already printable: nothing leans more than {maxLean:G3}°."
                    : $"{fix.MovedCount} of {mesh.VertexCount} points moved, at most {fix.MaxMove / s:G3} units, " +
                      $"to keep the wall within {maxLean:G3}° of vertical.");
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }
    }
}
