using System.Collections.Generic;
using System.Linq;
using ClayBeso.Fem;
using ClayBeso.Geometry;
using Rhino.Geometry;

namespace ClayBeso.GH.Common
{
    /// <summary>Force flow as Grasshopper vectors: direction = force, length 0..1 = how directional.</summary>
    internal static class FlowConvert
    {
        public static IEnumerable<Vector3d> ToVectors(ForceFlowField flow) =>
            flow.Direction.Select((d, e) => new Vector3d(d.X, d.Y, d.Z) * flow.Anisotropy[e]);

        public static Vec3[] FromVectors(IList<Vector3d> v) => v.Select(x => new Vec3(x.X, x.Y, x.Z)).ToArray();

        /// <summary>
        /// A short line per face along the force flow, centred on the face, in document units. Its
        /// length scales with the face size and with how directional the force is, so the lines read
        /// as force-flow trajectories.
        /// </summary>
        public static IEnumerable<Line> ToLines(ShellModel model, ForceFlowField flow)
        {
            double s = model.UnitScale;
            var areas = model.Mesh.FaceAreas;
            for (int e = 0; e < model.ElementCount; e++)
            {
                var c = model.Mesh.FaceCentroid(e);
                var half = flow.Direction[e] * (0.45 * System.Math.Sqrt(areas[e]) * flow.Anisotropy[e]);
                yield return new Line(RhinoConvert.ToPoint(c - half, s), RhinoConvert.ToPoint(c + half, s));
            }
        }
    }
}
