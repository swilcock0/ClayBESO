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

    /// <summary>
    /// The example vase (examples/TestVase.ghx, in mm): three ellipses lofted, 10 x 20 at the base,
    /// 60 x 10 at z = 32 and 10 x 20 at z = 60.8. The loft is approximated by quadratic interpolation
    /// of the radii. A flat, strongly overhanging shape.
    /// </summary>
    public static TriMesh EllipseLoftVase(int nAround = 40, int nUp = 40)
    {
        double h = 60.8, s1 = 32 / 60.8;
        double Lagrange(double s, double f0, double f1, double f2) =>
            f0 * (s - s1) * (s - 1) / (s1 * 1) + f1 * s * (s - 1) / (s1 * (s1 - 1)) + f2 * s * (s - s1) / (1 - s1);
        return Grid(nAround, nUp, (u, v) =>
        {
            double t = 2 * Math.PI * u;
            return new Vec3(Lagrange(v, 10, 60, 10) * Math.Cos(t), Lagrange(v, 20, 10, 20) * Math.Sin(t), h * v);
        }, closedU: true);
    }
}
