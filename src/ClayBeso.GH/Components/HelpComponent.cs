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

        private bool _last;
        private bool _seen;

        public HelpComponent()
            : base("Help", "Help",
                "Opens the ClayBESO component reference (every input and output of every component) in your browser, " +
                "and shows the installed ClayBESO version.\n" +
                "Double-click this component, or connect a Boolean Toggle (every click opens the page) or a Button " +
                "(each press opens it) to Open.",
                Tab.Name, Tab.Help) { }

        public override Guid ComponentGuid => new Guid("a1594953-48d2-4706-9c3c-c8b2dc97e3a4");
        protected override System.Drawing.Bitmap Icon => Icons.Get("Help");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        public override void CreateAttributes() => m_attributes = new HelpAttributes(this);

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddBooleanParameter("Open", "Open", "Opens the component reference: on every click of a Boolean Toggle, or on each press of a Button.", GH_ParamAccess.item, false);
            p[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("URL", "URL", "Address of the component reference.", GH_ParamAccess.item);
            p.AddTextParameter("Version", "V", "Installed ClayBESO release version (e.g. 0.3.0).", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            bool open = false;
            da.GetData(0, ref open);

            // A toggle opens the page whenever it is clicked (either way); a button only when pressed,
            // not again on release. Nothing opens on the first solve, e.g. when the file is opened with
            // a toggle saved as true.
            bool fromToggle = false;
            foreach (var src in Params.Input[0].Sources)
                if (src is Grasshopper.Kernel.Special.GH_BooleanToggle) fromToggle = true;
            bool clicked = _seen && (fromToggle ? open != _last : open && !_last);
            if (clicked) OpenReference(this);
            _last = open;
            _seen = true;

            Message = "v" + ClayBesoInfo.ReleaseVersion;
            da.SetData(0, ReferenceUrl);
            da.SetData(1, ClayBesoInfo.ReleaseVersion);
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
