using System;
using Grasshopper.Kernel;

namespace ClayBeso.GH.Components
{
    /// <summary>
    /// Shell BESO as released up to v0.4 (old input/output order). Hidden: kept so older definitions open
    /// with their wires on the right inputs. All behaviour is shared with <see cref="BesoComponent"/>.
    /// </summary>
    public class BesoComponentV1 : BesoComponent
    {
        public BesoComponentV1()
            : base("Shell BESO", "BESO",
                "Legacy: the older Shell BESO with a different input order. Kept so older definitions still open; " +
                "place a new Shell BESO for new work.") { }

        public override Guid ComponentGuid => new Guid("d1a4f6b5-84e7-4e31-9c94-3f5e0a2d7b04");
        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Model", "Model", "Shell model.", GH_ParamAccess.item);
            p.AddIntegerParameter("Mode", "Mode", "0 = Thickness (graded wall), 1 = Void (perforations).", GH_ParamAccess.item, 0);
            p.AddNumberParameter("Volume Fraction", "Vf", "0 = keep the starting clay; > 0: average thickness = Vf × tMax. Void mode: fraction to keep.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Min Thickness", "tMin", "Thinnest printable wall [mm].", GH_ParamAccess.item, 3.5);
            p.AddNumberParameter("Max Thickness", "tMax", "Thickest wall [mm].", GH_ParamAccess.item, 7.0);
            p.AddNumberParameter("Evolution Rate", "ER", "Max relative volume change per iteration.", GH_ParamAccess.item, 0.03);
            p.AddNumberParameter("Filter Radius", "R", "Sensitivity filter radius in document units.", GH_ParamAccess.item, 10.0);
            p.AddIntegerParameter("Max Iterations", "N", "Iteration limit.", GH_ParamAccess.item, 100);
            p.AddBooleanParameter("Run", "Run", "Iterate while true.", GH_ParamAccess.item, false);
            p.AddBooleanParameter("Reset", "Reset", "Restart from the initial design.", GH_ParamAccess.item, false);
            p.AddNumberParameter("Penalty", "p", "Void mode: stiffness penalty exponent.", GH_ParamAccess.item, 3.0);
            p.AddNumberParameter("Admission Ratio", "AR", "Void mode: max fraction of volume re-added per iteration.", GH_ParamAccess.item, 0.02);
            p.AddIntervalParameter("Colour Range", "CR", "Fix the thickness colour scale [mm].", GH_ParamAccess.item);
            p.AddBooleanParameter("Legend", "Lg", "Draw a colour legend in the viewport.", GH_ParamAccess.item, true);
            p.AddNumberParameter("Playback", "Pb", "0 = first iteration, 1 = final.", GH_ParamAccess.item, 1.0);
            p.AddGenericParameter("Layer Rule", "Rule", "Optional Layer Rule.", GH_ParamAccess.item);
            p.AddNumberParameter("Bond Efficiency", "η", "With a Layer Rule: interface efficiency 0-1.", GH_ParamAccess.item, 1.0);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("Thickness", "t", "Wall thickness per face [mm].", GH_ParamAccess.list);
            p.AddBooleanParameter("Solid", "S", "Per face: true if material is kept.", GH_ParamAccess.list);
            p.AddNumberParameter("Sensitivity", "α", "Filtered sensitivity per face.", GH_ParamAccess.list);
            p.AddMeshParameter("Preview", "P", "Mesh coloured by thickness.", GH_ParamAccess.item);
            p.AddNumberParameter("Compliance", "C", "Compliance history [N·mm].", GH_ParamAccess.list);
            p.AddNumberParameter("Volume", "V", "Volume-fraction history.", GH_ParamAccess.list);
            p.AddIntegerParameter("Iteration", "i", "Iterations done.", GH_ParamAccess.item);
            p.AddBooleanParameter("Converged", "Done", "True once converged.", GH_ParamAccess.item);
            p.AddTextParameter("Info", "I", "Status.", GH_ParamAccess.item);
            p.AddGenericParameter("Design", "D", "Model + thickness + Layer Rule + bond.", GH_ParamAccess.item);
        }
    }
}
