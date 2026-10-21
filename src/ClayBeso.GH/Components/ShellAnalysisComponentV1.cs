using System;
using Grasshopper.Kernel;

namespace ClayBeso.GH.Components
{
    /// <summary>
    /// Shell Analysis as released up to v0.4 (old input/output order). Hidden: kept so older definitions
    /// open with their wires on the right inputs. All behaviour is shared with <see cref="ShellAnalysisComponent"/>.
    /// </summary>
    public class ShellAnalysisComponentV1 : ShellAnalysisComponent
    {
        public ShellAnalysisComponentV1()
            : base("Shell Analysis", "Analyse",
                "Legacy: the older Shell Analysis with a different input order. Kept so older definitions still open; " +
                "place a new Shell Analysis for new work.") { }

        public override Guid ComponentGuid => new Guid("c9f3e5a4-73d6-4d20-8b83-2e4d9f1c6a03");
        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell model (or a Design).", GH_ParamAccess.item);
            p.AddNumberParameter("Thickness", "t", "Optional thickness per face [mm].", GH_ParamAccess.list);
            p.AddNumberParameter("Deform Scale", "DS", "Displacement exaggeration for the preview mesh.", GH_ParamAccess.item, 10.0);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the colour scale.", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            p.AddGenericParameter("Layer Rule", "Rule", "Optional Layer Rule.", GH_ParamAccess.item);
            p.AddNumberParameter("Bond Efficiency", "η", "With a Layer Rule: interface efficiency 0-1.", GH_ParamAccess.item, 1.0);
            p.AddIntegerParameter("Show", "Show", "0 = von Mises stress, 1 = displacement, 2 = buckling mode.", GH_ParamAccess.item, 0);
            p.AddBooleanParameter("Buckling", "Buckling", "Also run a linear buckling analysis.", GH_ParamAccess.item, false);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("Compliance", "C", "Compliance F·u [N·mm].", GH_ParamAccess.item);
            p.AddNumberParameter("Max Displacement", "dMax", "Largest displacement [mm].", GH_ParamAccess.item);
            p.AddVectorParameter("Displacements", "d", "Displacement per vertex [mm].", GH_ParamAccess.list);
            p.AddNumberParameter("Von Mises", "σ", "Von Mises stress per face [MPa].", GH_ParamAccess.list);
            p.AddNumberParameter("Utilisation", "U", "σ / strength per face.", GH_ParamAccess.list);
            p.AddMeshParameter("Preview", "P", "Deformed, coloured mesh.", GH_ParamAccess.item);
            p.AddTextParameter("Info", "I", "Summary.", GH_ParamAccess.item);
            p.AddPointParameter("Max Stress Point", "σMax", "Centre of the face with the highest von Mises stress.", GH_ParamAccess.item);
            p.AddVectorParameter("Force Flow", "FF", "Per face force flow direction.", GH_ParamAccess.list);
            p.AddLineParameter("Flow Lines", "FL", "A short line per face along the force flow.", GH_ParamAccess.list);
            p.AddNumberParameter("Buckling Factor", "λ", "Lowest buckling load factor.", GH_ParamAccess.item);
            p.AddVectorParameter("Buckling Mode", "φ", "Buckling shape per vertex (largest = 1).", GH_ParamAccess.list);
        }
    }
}
