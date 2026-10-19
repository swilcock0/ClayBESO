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
    public class ShellModelComponent : GH_Component
    {
        public ShellModelComponent()
            : base("Shell Model", "Shell",
                "Build a shell analysis model from a mesh of the mid-surface (e.g. a vase). " +
                "Quads are split into triangles. Aim for 2 000-20 000 faces.",
                Tab.Name, Tab.Model) { }

        public override Guid ComponentGuid => new Guid("b7e2d4f3-62c5-4c1f-9a72-1d3c8e0b5f02");
        protected override System.Drawing.Bitmap Icon => Icons.Get("ShellModel");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddMeshParameter("Mesh", "M", "Mid-surface mesh of the shell, in document units.", GH_ParamAccess.item);
            p.AddGenericParameter("Material", "Mat", "Clay Material. Default: green clay (E = 1 MPa).", GH_ParamAccess.item);
            p.AddNumberParameter("Thickness", "t", "Wall thickness [mm]: one value, or one per mesh face (after triangulation).", GH_ParamAccess.list, 5.0);
            p.AddBooleanParameter("Fix Base", "Base", "Clamp every vertex within the tolerance of the lowest Z (the print bed).", GH_ParamAccess.item, true);
            p.AddPointParameter("Supports", "S", "Extra support points (snapped to the nearest vertex).", GH_ParamAccess.list);
            p.AddBooleanParameter("Fix Rotations", "Rot", "Also fix rotations at supports (clamped). False = pinned.", GH_ParamAccess.item, true);
            p.AddNumberParameter("Tolerance", "Tol", "Base detection / snapping tolerance, in document units.", GH_ParamAccess.item, 0.5);
            p.AddBooleanParameter("Self-weight", "SW", "Include self-weight (gravity along -Z).", GH_ParamAccess.item, true);
            p.AddNumberParameter("Gravity Factor", "g", "Self-weight multiplier (e.g. 1.5 as a safety factor).", GH_ParamAccess.item, 1.0);
            p.AddPointParameter("Load Points", "LP", "Points where point loads act (snapped to the nearest vertex).", GH_ParamAccess.list);
            p.AddVectorParameter("Load Vectors", "LV", "Point-load forces [N]: one vector, or one per load point.", GH_ParamAccess.list);
            p.AddIntegerParameter("Frozen Faces", "Fz", "Indices of faces the optimiser must not change (e.g. a rim or foot band).", GH_ParamAccess.list);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell model.", GH_ParamAccess.item);
            p.AddMeshParameter("Mesh", "M", "Triangulated analysis mesh. Face indices of all per-face outputs refer to this mesh.", GH_ParamAccess.item);
            p.AddPointParameter("Supports", "S", "Supported vertices.", GH_ParamAccess.list);
            p.AddTextParameter("Info", "I", "Model summary.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Mesh rmesh = null;
            object matObj = null;
            var thick = new List<double>();
            bool fixBase = true, fixRot = true, selfWeight = true;
            double tol = 0.5, gFactor = 1;
            var supports = new List<Point3d>();
            var loadPts = new List<Point3d>();
            var loadVecs = new List<Vector3d>();
            var frozenIdx = new List<int>();

            if (!da.GetData(0, ref rmesh) || rmesh == null) return;
            da.GetData(1, ref matObj);
            da.GetDataList(2, thick);
            da.GetData(3, ref fixBase);
            da.GetDataList(4, supports);
            da.GetData(5, ref fixRot);
            da.GetData(6, ref tol);
            da.GetData(7, ref selfWeight);
            da.GetData(8, ref gFactor);
            da.GetDataList(9, loadPts);
            da.GetDataList(10, loadVecs);
            da.GetDataList(11, frozenIdx);

            try
            {
                double s = RhinoConvert.DocToMm();
                var mesh = RhinoConvert.ToTriMesh(rmesh, s);
                var material = RhinoConvert.Unwrap<ClayMaterial>(matObj) ?? new ClayMaterial(1.0, 0.35, 1900);
                int n = mesh.VertexCount;

                var fix = new bool[6 * n];
                int nFixDofs = fixRot ? 6 : 3;
                var supported = new HashSet<int>();
                if (fixBase)
                {
                    double zMin = mesh.Vertices.Min(v => v.Z);
                    for (int i = 0; i < n; i++)
                        if (mesh.Vertices[i].Z <= zMin + tol * s) supported.Add(i);
                }
                foreach (var pt in supports)
                {
                    int i = RhinoConvert.NearestVertex(mesh, RhinoConvert.ToVec(pt, s), out double d);
                    if (d > tol * s)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Support at {pt} snapped {d / s:G3} units to the mesh.");
                    supported.Add(i);
                }
                foreach (int i in supported)
                    for (int k = 0; k < nFixDofs; k++) fix[6 * i + k] = true;
                if (supported.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No supports: enable Fix Base or give support points.");
                    return;
                }

                var loads = new double[6 * n];
                if (loadPts.Count > 0)
                {
                    if (loadVecs.Count != 1 && loadVecs.Count != loadPts.Count)
                        throw new ArgumentException("Give one load vector, or one per load point.");
                    for (int k = 0; k < loadPts.Count; k++)
                    {
                        int i = RhinoConvert.NearestVertex(mesh, RhinoConvert.ToVec(loadPts[k], s), out _);
                        var v = loadVecs[loadVecs.Count == 1 ? 0 : k];
                        loads[6 * i] += v.X;
                        loads[6 * i + 1] += v.Y;
                        loads[6 * i + 2] += v.Z;
                    }
                }

                var frozen = new bool[mesh.FaceCount];
                foreach (int f in frozenIdx)
                    if (f >= 0 && f < frozen.Length) frozen[f] = true;

                var gravity = selfWeight ? new Vec3(0, 0, -gFactor) : Vec3.Zero;
                var tList = RhinoConvert.Expand(thick, mesh.FaceCount, "Thickness");
                var model = new ShellModel(mesh, material, tList, fix, loads, gravity, frozen) { UnitScale = s };

                if (mesh.FaceCount > 40000)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{mesh.FaceCount} faces: analysis will be slow. Consider remeshing coarser.");

                double weight = material.UnitWeight * model.TotalVolume(tList) * gFactor;
                da.SetData(0, model);
                da.SetData(1, RhinoConvert.ToRhino(mesh, s));
                da.SetDataList(2, supported.Select(i => RhinoConvert.ToPoint(mesh.Vertices[i], s)));
                da.SetData(3, $"{model}\n{material}\nSupported vertices: {supported.Count}\n" +
                              $"Self-weight: {(selfWeight ? weight : 0):G4} N  (geometry scale: 1 unit = {s:G4} mm)");
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }
    }
}
