using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace ClayBeso.GH
{
    public class ClayBesoInfo : GH_AssemblyInfo
    {
        public override string Name => "ClayBESO";
        public override string Description =>
            "Shell BESO (thickness or void) under self-weight and loads, and non-planar variable " +
            "layer heights for 3D printed clay.";
        public override Guid Id => new Guid("6b0b6c1e-3f0a-4c55-9a43-2f7d4c1f8a10");
        public override string AuthorName => "Politecnico di Milano";
        public override string AuthorContact => "";
        public override Bitmap Icon => null;
        public override string Version => "0.1.0";
    }

    /// <summary>Tab and panel names shared by all components.</summary>
    internal static class Tab
    {
        public const string Name = "ClayBESO";
        public const string Model = "1 Model";
        public const string Analyse = "2 Analyse";
        public const string Optimise = "3 Optimise";
        public const string Print = "4 Print";
    }
}
