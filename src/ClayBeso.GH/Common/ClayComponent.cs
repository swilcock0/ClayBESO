using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace ClayBeso.GH.Common
{
    /// <summary>A mode input that gets a dropdown (Value List) of named options when the component is placed.</summary>
    public sealed class Dropdown
    {
        /// <summary>Name of the input parameter (also the dropdown's label).</summary>
        public string Input { get; }
        public (string Label, int Value)[] Options { get; }
        public int Default { get; }

        public Dropdown(string input, int defaultValue, params (string Label, int Value)[] options)
        {
            Input = input;
            Default = defaultValue;
            Options = options;
        }
    }

    /// <summary>
    /// Base for ClayBESO components. When a component is newly placed on the canvas, every mode input
    /// it declares in <see cref="Dropdowns"/> gets a connected dropdown, so options are picked by name
    /// instead of by number. Components loaded from a file or pasted are left exactly as they were.
    /// </summary>
    public abstract class ClayComponent : GH_Component
    {
        private bool _deserialised;

        protected ClayComponent(string name, string nickname, string description, string category, string subCategory)
            : base(name, nickname, description, category, subCategory) { }

        /// <summary>Index of the input parameter with this name (-1 if none). Components read inputs by
        /// name, so the order of inputs on the canvas is free to change between component versions.</summary>
        protected int In(string name) => Params.IndexOfInputParam(name);

        /// <summary>Index of the output parameter with this name (-1 if none).</summary>
        protected int Out(string name) => Params.IndexOfOutputParam(name);

        /// <summary>Mode inputs that get an automatic dropdown.</summary>
        protected virtual IEnumerable<Dropdown> Dropdowns => Enumerable.Empty<Dropdown>();

        public override bool Read(GH_IReader reader)
        {
            _deserialised = true;   // opened from a file or pasted: don't touch its wiring
            return base.Read(reader);
        }

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            if (_deserialised) return;
            var lists = Dropdowns.ToList();
            if (lists.Count == 0) return;
            // Defer until the component is laid out on the canvas.
            document.ScheduleSolution(5, doc => AttachDropdowns(doc, lists));
        }

        private void AttachDropdowns(GH_Document doc, List<Dropdown> lists)
        {
            if (OnPingDocument() != doc) return;
            Attributes.ExpireLayout();
            Attributes.PerformLayout();
            bool added = false;
            foreach (var d in lists)
            {
                int i = In(d.Input);
                if (i < 0) continue;
                var input = Params.Input[i];
                if (input.SourceCount > 0) continue;

                var list = new GH_ValueList();
                list.CreateAttributes();
                list.ListMode = GH_ValueListMode.DropDown;
                list.ListItems.Clear();
                foreach (var (label, value) in d.Options)
                    list.ListItems.Add(new GH_ValueListItem(label, value.ToString()));
                list.NickName = d.Input;
                list.Name = d.Input;
                int index = d.Options.ToList().FindIndex(o => o.Value == d.Default);
                list.SelectItem(index < 0 ? 0 : index);

                list.Attributes.ExpireLayout();
                list.Attributes.PerformLayout();
                var b = input.Attributes.Bounds;
                float width = list.Attributes.Bounds.Width;
                list.Attributes.Pivot = new PointF(b.Left - width - 30, b.Top + (b.Height - list.Attributes.Bounds.Height) / 2);
                list.Attributes.ExpireLayout();

                doc.AddObject(list, false);
                input.AddSource(list);
                added = true;
            }
            if (added) ExpireSolution(false);
        }
    }
}

namespace ClayBeso.GH.Common
{
    /// <summary>Option lists shared by several components, so the labels match everywhere.</summary>
    internal static class Modes
    {
        public static readonly (string, int)[] Mapping =
        {
            ("Proportional (thicker = taller)", 3),
            ("Constant aspect ratio", 2),
            ("Constant bead area (fixed flow)", 0),
            ("Linear inverse (fixed flow)", 1),
        };

        public static readonly (string, int)[] LayerMethod =
        {
            ("Conforming", 0),
            ("Eikonal", 1),
            ("Force-aligned", 2),
        };

        public static readonly (string, int)[] BesoMode =
        {
            ("Thickness", 0),
            ("Void", 1),
        };
    }
}
