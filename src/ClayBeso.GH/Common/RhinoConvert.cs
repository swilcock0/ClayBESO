using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Geometry;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;

namespace ClayBeso.GH.Common
{
    /// <summary>
    /// Conversions between RhinoCommon and the core library. The core always works in
    /// millimetres, so geometry is scaled on the way in and out.
    /// </summary>
    internal static class RhinoConvert
    {
        /// <summary>Factor from the active document's units to millimetres.</summary>
        public static double DocToMm()
        {
            var doc = RhinoDoc.ActiveDoc;
            return doc == null ? 1.0 : RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters);
        }

        public static Vec3 ToVec(Point3d p, double scale = 1) => new Vec3(p.X * scale, p.Y * scale, p.Z * scale);
        public static Vec3 ToVec(Vector3d v) => new Vec3(v.X, v.Y, v.Z);
        public static Point3d ToPoint(Vec3 v, double scale = 1) => new Point3d(v.X / scale, v.Y / scale, v.Z / scale);

        /// <summary>Triangulate, weld and scale a Rhino mesh into a core <see cref="TriMesh"/>.</summary>
        public static TriMesh ToTriMesh(Mesh mesh, double scale)
        {
            var m = mesh.DuplicateMesh();
            m.Faces.ConvertQuadsToTriangles();
            m.Vertices.CombineIdentical(true, true);
            m.Faces.CullDegenerateFaces();
            m.Compact();

            var verts = new List<Vec3>(m.Vertices.Count);
            foreach (var p in m.Vertices) verts.Add(new Vec3(p.X * scale, p.Y * scale, p.Z * scale));
            var faces = new List<int[]>(m.Faces.Count);
            foreach (var f in m.Faces) faces.Add(new[] { f.A, f.B, f.C });
            return new TriMesh(verts, faces);
        }

        /// <summary>Core mesh back to a Rhino mesh in document units.</summary>
        public static Mesh ToRhino(TriMesh mesh, double scale, Func<int, Vec3> displacement = null, double dispScale = 0)
        {
            var m = new Mesh();
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var p = mesh.Vertices[i];
                if (displacement != null) p += displacement(i) * dispScale;
                m.Vertices.Add(p.X / scale, p.Y / scale, p.Z / scale);
            }
            foreach (var f in mesh.Faces) m.Faces.AddFace(f[0], f[1], f[2]);
            m.Normals.ComputeNormals();
            return m;
        }

        /// <summary>Unwrap whatever a generic parameter handed us.</summary>
        public static T Unwrap<T>(object o) where T : class
        {
            switch (o)
            {
                case T t: return t;
                case GH_ObjectWrapper w: return w.Value as T;
                case IGH_Goo g when g.ScriptVariable() is T t2: return t2;
                default: return null;
            }
        }

        public static int NearestVertex(TriMesh mesh, Vec3 p, out double distance)
        {
            int best = -1;
            distance = double.MaxValue;
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                double d = (mesh.Vertices[i] - p).Length;
                if (d < distance) { distance = d; best = i; }
            }
            return best;
        }

        public static List<double> Expand(IList<double> values, int count, string what)
        {
            if (values.Count == count) return values.ToList();
            if (values.Count == 1) return Enumerable.Repeat(values[0], count).ToList();
            throw new ArgumentException($"{what}: expected 1 or {count} values, got {values.Count}.");
        }
    }
}
