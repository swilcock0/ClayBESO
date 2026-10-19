using ClayBeso.Fem;
using ClayBeso.Geometry;

namespace ClayBeso.Tests;

public class ElementTests
{
    private const double E = 1000.0, Nu = 0.3;

    // An irregular triangle, tilted out of the XY plane to exercise the transformation.
    private static readonly Vec3[] Tilted =
    {
        new Vec3(0.3, -0.2, 0.1),
        new Vec3(2.1, 0.4, 0.9),
        new Vec3(0.8, 1.7, -0.4),
    };

    private static double Energy(double[] k, double[] u)
    {
        double s = 0;
        for (int i = 0; i < 18; i++)
        for (int j = 0; j < 18; j++)
            s += u[i] * k[i * 18 + j] * u[j];
        return 0.5 * s;
    }

    private static ShellElement Make(Vec3[] p) => new(p[0], p[1], p[2], new[] { 0, 1, 2 }, E, Nu);

    [Fact]
    public void RigidBodyModesHaveZeroEnergy()
    {
        var el = Make(Tilted);
        var rng = new Random(1);
        for (int trial = 0; trial < 5; trial++)
        {
            var t = new Vec3(rng.NextDouble(), rng.NextDouble(), rng.NextDouble());
            var w = new Vec3(rng.NextDouble(), rng.NextDouble(), rng.NextDouble()) * 0.1;
            var u = new double[18];
            for (int n = 0; n < 3; n++)
            {
                var d = t + Vec3.Cross(w, Tilted[n]); // small rigid motion
                u[6 * n] = d.X; u[6 * n + 1] = d.Y; u[6 * n + 2] = d.Z;
                u[6 * n + 3] = w.X; u[6 * n + 4] = w.Y; u[6 * n + 5] = w.Z;
            }
            double em = Energy(el.Km, u), eb = Energy(el.Kb, u);
            Assert.True(Math.Abs(em) < 1e-9, $"membrane rigid energy {em}");
            Assert.True(Math.Abs(eb) < 1e-9, $"bending rigid energy {eb}");
        }
    }

    [Theory]
    [InlineData(1.0, 0.0, 0.0)] // w = x^2/2
    [InlineData(0.0, 1.0, 0.0)] // w = y^2/2
    [InlineData(0.0, 0.0, 1.0)] // w = x*y
    public void DktReproducesConstantCurvatureExactly(double kxx, double kyy, double kxy)
    {
        // Flat element in the XY plane, so local axes are the global axes up to rotation about z.
        var p = new[] { new Vec3(0, 0, 0), new Vec3(2.0, 0.3, 0), new Vec3(0.6, 1.5, 0) };
        var el = Make(p);

        // w = kxx x^2/2 + kyy y^2/2 + kxy x y ; rx = dw/dy, ry = -dw/dx.
        var u = new double[18];
        for (int n = 0; n < 3; n++)
        {
            double x = p[n].X, y = p[n].Y;
            u[6 * n + 2] = 0.5 * kxx * x * x + 0.5 * kyy * y * y + kxy * x * y;
            u[6 * n + 3] = kyy * y + kxy * x;
            u[6 * n + 4] = -(kxx * x + kxy * y);
        }

        double area = el.Area;
        double d = E / (12 * (1 - Nu * Nu));
        // curvature vector (w,xx, w,yy, 2 w,xy)
        double k1 = kxx, k2 = kyy, k3 = 2 * kxy;
        double exact = 0.5 * area * d * (k1 * k1 + k2 * k2 + 2 * Nu * k1 * k2 + 0.5 * (1 - Nu) * k3 * k3);

        double eb = Energy(el.Kb, u);
        Assert.Equal(exact, eb, 9);
    }

    [Fact]
    public void CstReproducesConstantStrainExactly()
    {
        var el = Make(Tilted);
        // Uniform stretch along local e1: u = eps * (p . e1) e1
        double eps = 1e-3;
        var u = new double[18];
        for (int n = 0; n < 3; n++)
        {
            var d = el.E1 * (eps * Vec3.Dot(Tilted[n] - Tilted[0], el.E1));
            u[6 * n] = d.X; u[6 * n + 1] = d.Y; u[6 * n + 2] = d.Z;
        }
        double exact = 0.5 * el.Area * E / (1 - Nu * Nu) * eps * eps;
        Assert.Equal(exact, Energy(el.Km, u), 12);

        el.UnitResultants(u, out var nRes, out _);
        Assert.Equal(E / (1 - Nu * Nu) * eps, nRes[0], 9);
        Assert.Equal(Nu * E / (1 - Nu * Nu) * eps, nRes[1], 9);
    }
}
