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
        public override Bitmap Icon => Common.Icons.Get("ClayBeso");
        public override string Version => ReleaseVersion;

        /// <summary>
        /// The plugin's release version, read from the built assembly. Its only source is
        /// &lt;Version&gt; in ClayBeso.GH.csproj (the SDK appends "+commit", which is cut off here).
        /// </summary>
        public static string ReleaseVersion { get; } = ReadVersion();

        private static string ReadVersion()
        {
            var asm = typeof(ClayBesoInfo).Assembly;
            var info = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            string v = info?.InformationalVersion ?? asm.GetName().Version?.ToString(3) ?? "unknown";
            int plus = v.IndexOf('+');
            return plus >= 0 ? v.Substring(0, plus) : v;
        }
    }

    /// <summary>Registers the tab icon and short name shown when Grasshopper's tabs are collapsed.</summary>
    public class ClayBesoPriority : GH_AssemblyPriority
    {
        public override GH_LoadingInstruction PriorityLoad()
        {
            var icon = Common.Icons.Get("ClayBeso");
            if (icon != null) Grasshopper.Instances.ComponentServer.AddCategoryIcon(Tab.Name, icon);
            Grasshopper.Instances.ComponentServer.AddCategorySymbolName(Tab.Name, 'C');
            return GH_LoadingInstruction.Proceed;
        }
    }

    /// <summary>Tab and panel names shared by all components.</summary>
    internal static class Tab
    {
        public const string Name = "ClayBESO";
        public const string Start = "0 Start";
        public const string Model = "1 Model";
        public const string Analyse = "2 Analyse";
        public const string Optimise = "3 Optimise";
        public const string Print = "4 Print";
        public const string Help = "5 Help";
    }
}
