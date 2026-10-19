using System;
using System.Diagnostics;
using ClayBeso.GH.Common;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace ClayBeso.GH.Components
{
    public class HelpComponent : GH_Component
    {
        public const string ReferenceUrl = "https://github.com/swilcock0/ClayBESO/blob/main/docs/COMPONENTS.md";

        private bool _wasPressed;

        public HelpComponent()
            : base("Help", "Help",
                "Opens the ClayBESO component reference (every input and output of every component) in your browser. " +
                "Double-click this component, or connect a Button to Open.",
                Tab.Name, Tab.Help) { }

        public override Guid ComponentGuid => new Guid("a1594953-48d2-4706-9c3c-c8b2dc97e3a4");
        protected override System.Drawing.Bitmap Icon => Icons.Get("Help");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        public override void CreateAttributes() => m_attributes = new HelpAttributes(this);

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddBooleanParameter("Open", "Open", "Opens the component reference when this turns true (connect a Button).", GH_ParamAccess.item, false);
            p[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("URL", "URL", "Address of the component reference.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            bool open = false;
            da.GetData(0, ref open);
            // Open on the press only, so a toggle left on doesn't open a tab on every recompute.
            if (open && !_wasPressed) OpenReference(this);
            _wasPressed = open;
            da.SetData(0, ReferenceUrl);
        }

        internal static void OpenReference(GH_Component owner)
        {
            try
            {
                Process.Start(new ProcessStartInfo(ReferenceUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                owner?.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Could not open the browser ({ex.Message}). Go to {ReferenceUrl}");
            }
        }

        private sealed class HelpAttributes : GH_ComponentAttributes
        {
            public HelpAttributes(HelpComponent owner) : base(owner) { }

            public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e)
            {
                OpenReference(Owner as GH_Component);
                return GH_ObjectResponse.Handled;
            }
        }
    }
}
