using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using ClayBeso.Geometry;
using Rhino.Geometry;

namespace ClayBeso.GH.Common
{
    /// <summary>False-colour meshes for previewing per-face and per-vertex fields.</summary>
    internal static class Preview
    {
        private static readonly Color[] Ramp =
        {
            Color.FromArgb(49, 54, 149),
            Color.FromArgb(69, 117, 180),
            Color.FromArgb(116, 173, 209),
            Color.FromArgb(171, 217, 233),
            Color.FromArgb(254, 224, 144),
            Color.FromArgb(253, 174, 97),
            Color.FromArgb(244, 109, 67),
            Color.FromArgb(165, 0, 38),
        };

        public static Color ColourAt(double s)
        {
            if (double.IsNaN(s)) return Color.Gray;
            s = Math.Max(0, Math.Min(1, s)) * (Ramp.Length - 1);
            int i = Math.Min((int)s, Ramp.Length - 2);
            double f = s - i;
            var a = Ramp[i];
            var b = Ramp[i + 1];
            return Color.FromArgb(
                (int)(a.R + f * (b.R - a.R)),
                (int)(a.G + f * (b.G - a.G)),
                (int)(a.B + f * (b.B - a.B)));
        }

        private static (double lo, double hi) Range(IEnumerable<double> values)
        {
            var finite = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToArray();
            if (finite.Length == 0) return (0, 1);
            double lo = finite.Min(), hi = finite.Max();
            return hi - lo < 1e-12 ? (lo - 0.5, hi + 0.5) : (lo, hi);
        }

        /// <summary>Unwelded mesh with one flat colour per face.</summary>
        public static Mesh FaceColoured(
            TriMesh mesh, IList<double> faceValues, double scale,
            Func<int, Vec3> displacement = null, double dispScale = 0, IList<bool> hide = null)
        {
            var (lo, hi) = Range(faceValues);
            var m = new Mesh();
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                if (hide != null && hide[f]) continue;
                var c = ColourAt((faceValues[f] - lo) / (hi - lo));
                int start = m.Vertices.Count;
                foreach (int v in mesh.Faces[f])
                {
                    var p = mesh.Vertices[v];
                    if (displacement != null) p += displacement(v) * dispScale;
                    m.Vertices.Add(p.X / scale, p.Y / scale, p.Z / scale);
                    m.VertexColors.Add(c);
                }
                m.Faces.AddFace(start, start + 1, start + 2);
            }
            m.Normals.ComputeNormals();
            return m;
        }

        /// <summary>Welded mesh with smooth per-vertex colours.</summary>
        public static Mesh VertexColoured(TriMesh mesh, IList<double> vertexValues, double scale)
        {
            var (lo, hi) = Range(vertexValues);
            var m = RhinoConvert.ToRhino(mesh, scale);
            foreach (var v in vertexValues) m.VertexColors.Add(ColourAt((v - lo) / (hi - lo)));
            return m;
        }

        /// <summary>Closed solid preview of the wall: mid-surface offset +/- t/2 along vertex normals.</summary>
        public static Mesh Thicken(TriMesh mesh, IList<double> vertexThickness, double scale)
        {
            var n = mesh.VertexNormals;
            int nv = mesh.VertexCount;
            var m = new Mesh();
            for (int side = 0; side < 2; side++)
            {
                double sign = side == 0 ? 0.5 : -0.5;
                for (int v = 0; v < nv; v++)
                {
                    var p = mesh.Vertices[v] + n[v] * (sign * vertexThickness[v]);
                    m.Vertices.Add(p.X / scale, p.Y / scale, p.Z / scale);
                }
            }
            foreach (var f in mesh.Faces)
            {
                m.Faces.AddFace(f[0], f[1], f[2]);
                m.Faces.AddFace(nv + f[0], nv + f[2], nv + f[1]);
            }

            // Close the naked boundary with side strips.
            var edgeCount = new Dictionary<(int, int), int>();
            foreach (var f in mesh.Faces)
            for (int k = 0; k < 3; k++)
            {
                int a = f[k], b = f[(k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                edgeCount[key] = edgeCount.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            foreach (var f in mesh.Faces)
            for (int k = 0; k < 3; k++)
            {
                int a = f[k], b = f[(k + 1) % 3];
                if (edgeCount[a < b ? (a, b) : (b, a)] != 1) continue;
                m.Faces.AddFace(b, a, nv + a, nv + b);
            }
            m.Normals.ComputeNormals();
            return m;
        }
    }
}
