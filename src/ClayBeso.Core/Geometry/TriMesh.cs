using System;
using System.Collections.Generic;

namespace ClayBeso.Geometry
{
    /// <summary>Indexed triangle mesh with the adjacency the solver and slicer need.</summary>
    public sealed class TriMesh
    {
        public Vec3[] Vertices { get; }
        public int[][] Faces { get; }

        public int VertexCount => Vertices.Length;
        public int FaceCount => Faces.Length;

        private double[] _faceAreas;
        private Vec3[] _faceNormals;
        private Vec3[] _vertexNormals;
        private List<int>[] _vertexFaces;

        public TriMesh(IList<Vec3> vertices, IList<int[]> faces)
        {
            Vertices = new Vec3[vertices.Count];
            vertices.CopyTo(Vertices, 0);
            Faces = new int[faces.Count][];
            for (int i = 0; i < faces.Count; i++)
            {
                var f = faces[i];
                if (f.Length != 3) throw new ArgumentException("TriMesh only accepts triangles.");
                Faces[i] = new[] { f[0], f[1], f[2] };
            }
        }

        public double[] FaceAreas
        {
            get
            {
                if (_faceAreas != null) return _faceAreas;
                _faceAreas = new double[FaceCount];
                for (int i = 0; i < FaceCount; i++)
                    _faceAreas[i] = 0.5 * RawNormal(i).Length;
                return _faceAreas;
            }
        }

        public Vec3[] FaceNormals
        {
            get
            {
                if (_faceNormals != null) return _faceNormals;
                _faceNormals = new Vec3[FaceCount];
                for (int i = 0; i < FaceCount; i++)
                    _faceNormals[i] = RawNormal(i).Unit();
                return _faceNormals;
            }
        }

        /// <summary>Area-weighted vertex normals.</summary>
        public Vec3[] VertexNormals
        {
            get
            {
                if (_vertexNormals != null) return _vertexNormals;
                var acc = new Vec3[VertexCount];
                for (int i = 0; i < FaceCount; i++)
                {
                    var n = RawNormal(i);
                    foreach (int v in Faces[i]) acc[v] += n;
                }
                for (int v = 0; v < VertexCount; v++) acc[v] = acc[v].Unit();
                _vertexNormals = acc;
                return _vertexNormals;
            }
        }

        public List<int>[] VertexFaces
        {
            get
            {
                if (_vertexFaces != null) return _vertexFaces;
                _vertexFaces = new List<int>[VertexCount];
                for (int v = 0; v < VertexCount; v++) _vertexFaces[v] = new List<int>(6);
                for (int i = 0; i < FaceCount; i++)
                    foreach (int v in Faces[i]) _vertexFaces[v].Add(i);
                return _vertexFaces;
            }
        }

        public Vec3 FaceCentroid(int f)
        {
            var t = Faces[f];
            return (Vertices[t[0]] + Vertices[t[1]] + Vertices[t[2]]) / 3.0;
        }

        private Vec3 RawNormal(int f)
        {
            var t = Faces[f];
            return Vec3.Cross(Vertices[t[1]] - Vertices[t[0]], Vertices[t[2]] - Vertices[t[0]]);
        }

        /// <summary>Area-weighted average of a per-face field onto the vertices.</summary>
        public double[] FaceToVertex(IList<double> faceValues)
        {
            var areas = FaceAreas;
            var sum = new double[VertexCount];
            var w = new double[VertexCount];
            for (int f = 0; f < FaceCount; f++)
            {
                foreach (int v in Faces[f])
                {
                    sum[v] += faceValues[f] * areas[f];
                    w[v] += areas[f];
                }
            }
            for (int v = 0; v < VertexCount; v++) sum[v] = w[v] > 0 ? sum[v] / w[v] : 0;
            return sum;
        }

        /// <summary>Unique neighbouring vertices of each vertex.</summary>
        public List<int>[] VertexNeighbours()
        {
            var sets = new HashSet<int>[VertexCount];
            for (int v = 0; v < VertexCount; v++) sets[v] = new HashSet<int>();
            foreach (var t in Faces)
            {
                for (int k = 0; k < 3; k++)
                {
                    sets[t[k]].Add(t[(k + 1) % 3]);
                    sets[t[k]].Add(t[(k + 2) % 3]);
                }
            }
            var result = new List<int>[VertexCount];
            for (int v = 0; v < VertexCount; v++) result[v] = new List<int>(sets[v]);
            return result;
        }
    }
}
