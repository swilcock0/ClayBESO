using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;

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

    /// <summary>A named set of values for number inputs, offered on the component's right-click menu.</summary>
    public sealed class Preset
    {
        public string Name { get; }
        /// <summary>Input name and value, in the units the input states.</summary>
        public (string Input, double Value)[] Values { get; }

        public Preset(string name, params (string Input, double Value)[] values)
        {
            Name = name;
            Values = values;
        }
    }

    /// <summary>
    /// Base for ClayBESO components. When a component is newly placed on the canvas, every mode input
    /// it declares in <see cref="Dropdowns"/> gets a connected dropdown, so options are picked by name
    /// instead of by number. If it declares <see cref="Presets"/>, every input of the first (default)
    /// preset gets a connected text box (panel) holding its value, and the right-click menu offers the
    /// presets, which rewrite those text boxes. Components loaded from a file or pasted are left exactly
    /// as they were.
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

        /// <summary>Right-click presets. The first one is the default and sets the spawned text boxes.</summary>
        protected virtual IEnumerable<Preset> Presets => Enumerable.Empty<Preset>();

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
            var preset = Presets.FirstOrDefault();
            if (lists.Count == 0 && preset == null) return;
            // Defer until the component is laid out on the canvas.
            document.ScheduleSolution(5, doc =>
            {
                if (OnPingDocument() != doc) return;
                Attributes.ExpireLayout();
                Attributes.PerformLayout();
                bool added = AttachDropdowns(doc, lists);
                if (preset != null) added |= AttachTextBoxes(doc, preset);
                if (added) ExpireSolution(false);
            });
        }

        private static string Format(double v) => v.ToString("G", CultureInfo.InvariantCulture);

        private bool AttachTextBoxes(GH_Document doc, Preset preset)
        {
            bool added = false;
            foreach (var (name, value) in preset.Values)
            {
                int i = In(name);
                if (i < 0) continue;
                var input = Params.Input[i];
                if (input.SourceCount > 0) continue;

                var panel = new GH_Panel();
                panel.CreateAttributes();
                panel.NickName = name;
                panel.UserText = Format(value);
                panel.Properties.Multiline = false;
                panel.Properties.DrawIndices = false;
                panel.Properties.DrawPaths = false;

                var b = input.Attributes.Bounds;
                const float width = 70, height = 20;
                var pivot = new PointF(b.Left - width - 30, b.Top + (b.Height - height) / 2);
                panel.Attributes.Pivot = pivot;
                panel.Attributes.Bounds = new RectangleF(pivot, new SizeF(width, height));

                doc.AddObject(panel, false);
                input.AddSource(panel);
                added = true;
            }
            return added;
        }

        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalMenuItems(menu);
            var presets = Presets.ToList();
            if (presets.Count == 0) return;
            Menu_AppendSeparator(menu);
            foreach (var p in presets)
                Menu_AppendItem(menu, "Preset: " + p.Name, (s, e) => ApplyPreset(p));
        }

        /// <summary>
        /// Writes a preset's values into the connected text boxes (or the input's own value when nothing is
        /// connected). Inputs wired to anything else (a slider, another component) are left alone.
        /// </summary>
        private void ApplyPreset(Preset preset)
        {
            var doc = OnPingDocument();
            if (doc == null) return;
            var skipped = new List<string>();
            foreach (var (name, value) in preset.Values)
            {
                int i = In(name);
                if (i < 0) continue;
                var input = Params.Input[i];
                if (input.SourceCount == 1 && input.Sources[0] is GH_Panel panel)
                {
                    doc.UndoUtil.RecordGenericObjectEvent("Preset " + preset.Name, panel);
                    panel.UserText = Format(value);
                    panel.Attributes.ExpireLayout();
                    panel.ExpireSolution(false);
                }
                else if (input.SourceCount == 0 && input is Param_Number number)
                {
                    doc.UndoUtil.RecordGenericObjectEvent("Preset " + preset.Name, number);
                    number.PersistentData.Clear();
                    number.PersistentData.Append(new GH_Number(value));
                    number.ExpireSolution(false);
                }
                else skipped.Add(name);
            }
            ExpireSolution(true);
            if (skipped.Count > 0)
                MessageBox.Show(
                    $"Preset {preset.Name}: {string.Join(", ", skipped)} not changed, because they are connected to " +
                    "something other than a single text box (panel). Set them by hand.",
                    NickName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private bool AttachDropdowns(GH_Document doc, List<Dropdown> lists)
        {
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
            return added;
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
