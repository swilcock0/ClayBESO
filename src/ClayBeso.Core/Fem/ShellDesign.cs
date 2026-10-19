using System;
using System.Collections.Generic;
using System.Linq;
using ClayBeso.Printing;

namespace ClayBeso.Fem
{
    /// <summary>
    /// A shell together with its wall thickness and (optionally) the printed-layer assumptions it
    /// was designed with. Shell BESO outputs one, so everything downstream gets the model, the
    /// thickness, the layer rule and the bond from a single wire.
    /// </summary>
    public sealed class ShellDesign
    {
        public ShellModel Model { get; }

        /// <summary>Wall thickness per face [mm] (0 = removed face in void mode).</summary>
        public double[] Thickness { get; }

        /// <summary>Thickness → layer-height rule (resolved), or null if the design ignored layers.</summary>
        public LayerRule Rule { get; }

        /// <summary>Bond model used for the across-layer stiffness, or null.</summary>
        public BondModel Bond { get; }

        /// <summary>Where it came from, for Info text.</summary>
        public string Source { get; }

        public ShellDesign(ShellModel model, IList<double> thickness, LayerRule rule = null, BondModel bond = null, string source = "")
        {
            if (thickness.Count != model.ElementCount) throw new ArgumentException("Need one thickness per face.");
            Model = model;
            Thickness = thickness.ToArray();
            Rule = rule;
            Bond = bond;
            Source = source;
        }

        /// <summary>The model's own (starting) thickness, no layer assumptions.</summary>
        public static ShellDesign FromModel(ShellModel model) => new ShellDesign(model, model.Thickness, source: "Shell Model");

        public override string ToString() =>
            $"Shell design ({(Source.Length > 0 ? Source + ", " : "")}{Model.ElementCount} faces, " +
            $"wall {Thickness.Where(t => t > 0).DefaultIfEmpty(0).Min():G3}-{Thickness.Max():G3} mm" +
            (Rule != null ? $", {Rule}" : "") + ")";
    }
}
