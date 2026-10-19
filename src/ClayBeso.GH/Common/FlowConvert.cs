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
    }
}
