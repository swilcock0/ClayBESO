using ClayBeso.Geometry;

namespace ClayBeso.Tests;

internal static class TestMeshes
{
    /// <summary>Structured grid over (u, v) in [0,1]^2 mapped through <paramref name="map"/>.</summary>
    public static TriMesh Grid(int nu, int nv, Func<double, double, Vec3> map, bool closedU = false)
    {
        var verts = new List<Vec3>();
        int cols = closedU ? nu : nu + 1;
        for (int j = 0; j <= nv; j++)
        for (int i = 0; i < cols; i++)
            verts.Add(map((double)i / nu, (double)j / nv));

        int Id(int i, int j) => j * cols + (i % cols);
        var faces = new List<int[]>();
        for (int j = 0; j < nv; j++)
        for (int i = 0; i < nu; i++)
        {
            int a = Id(i, j), b = Id(i + 1, j), c = Id(i + 1, j + 1), d = Id(i, j + 1);
            // alternate the diagonal to avoid a directional bias
            if ((i + j) % 2 == 0) { faces.Add(new[] { a, b, c }); faces.Add(new[] { a, c, d }); }
            else { faces.Add(new[] { a, b, d }); faces.Add(new[] { b, c, d }); }
        }
        return new TriMesh(verts, faces);
    }

    /// <summary>A simple vase: radius varies with height, open top, open bottom ring.</summary>
    public static TriMesh Vase(int nAround, int nUp, double height = 200, double r0 = 50)
    {
        return Grid(nAround, nUp, (u, v) =>
        {
            double th = 2 * Math.PI * u;
            double z = height * v;
            double r = r0 * (1 + 0.35 * Math.Sin(Math.PI * v * 1.3));
            return new Vec3(r * Math.Cos(th), r * Math.Sin(th), z);
        }, closedU: true);
    }
}
