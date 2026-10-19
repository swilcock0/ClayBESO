# ClayBESO — shell optimisation and non-planar layers for 3D printed clay

A Grasshopper plugin for Rhino 8 that:

1. **analyses** a thin clay shell (vase, vessel, column…) under **self-weight** and/or point
   loads with a built-in finite-element shell solver,
2. **optimises** it with **BESO**, either grading the **wall thickness** (no holes) or
   removing material to make **perforations**,
3. turns the optimised thickness into a **variable layer-height field** and slices the shell
   into **non-planar layer toolpaths**, with a **flow factor** at every point. Highly loaded
   regions get a wider bead and a taller layer, with the flow raised to supply both. For
   fixed-flow printers there are mappings where a thick wall comes from squashing the bead
   instead.

No other plugins or licences are needed.

![ClayBESO on a vase under self-weight](docs/Example.png)

*Left: the BESO wall thickness (3.5–7 mm) for a vase under self-weight. Clay is moved to the
lower body, which carries the weight. Right: the resulting target layer heights (1–2.5 mm) with
the non-planar layer toolpaths. This image uses the fixed-flow "constant bead area" mapping
(thick regions get lower, squashed layers). The default is now the proportional mapping, where
thick regions get taller layers and more flow.*

---

## Install

**Easiest:** download the `.yak` file from the
[latest release](https://github.com/swilcock0/ClayBESO/releases/latest) and **drag it onto an
open Rhino 8 window**. Then restart Rhino. To uninstall, use `_PackageManager` → Installed.

Manual install:

1. Install **Rhino 8**.
2. Get the built plugin, either:
   - from a colleague: the folder containing `ClayBeso.gha`, `ClayBeso.Core.dll` and `CSparse.dll`, or
   - by building it yourself (see `CONTRIBUTING.md`), then running
     `powershell -ExecutionPolicy Bypass -File scripts/install.ps1`.
3. Put **the whole folder** (not just the .gha) in
   `%APPDATA%\Grasshopper\Libraries\ClayBESO\`. In Grasshopper: *File → Special Folders →
   Components Folder*.
4. Restart Rhino. A **ClayBESO** tab appears in Grasshopper.

> If Windows blocked the downloaded files: right-click each DLL/GHA → Properties → *Unblock*.

## Components

Every input and output of every component (type, default, units) is listed in
**[docs/COMPONENTS.md](docs/COMPONENTS.md)**.

| Tab | Component | What it does |
|---|---|---|
| 1 Model | **Clay Material** | E [MPa], ν, density [kg/m³], optional strength [MPa]. Defaults = green clay. |
| 1 Model | **Shell Model** | Mesh + material + thickness [mm] + supports (auto base) + loads → model. |
| 2 Analyse | **Shell Analysis** | Displacements, von Mises stress, compliance, deformed preview. |
| 2 Analyse | **Interlayer Check** | Stresses across the printed layer interfaces vs a bond strength that drops with layer height. |
| 3 Optimise | **Shell BESO** | Live BESO loop (Run / Reset). Mode 0 = thickness, 1 = void. |
| 4 Print | **Layer Rule** | One thickness → layer-height rule shared by Layer Heights, Shell BESO and Shell Analysis. |
| 4 Print | **Layer Heights** | Thickness per face → target layer height per vertex. |
| 4 Print | **Non-planar Layers** | Layer height field → layer polylines + per-point height/width. |
| 4 Print | **Wall Preview** | Thickened solid wall for visual checking. |

## Basic workflow

A working example is in **`examples/TestVase.ghx`**. It builds a parametric vase from
ellipses with sliders, then runs the full chain: Shell Model → Analysis → BESO → Layer
Heights → Non-planar Layers. It needs no Rhino geometry: open it in Grasshopper and set BESO's
*Run* toggle to true.

```
Surface/Brep ──Mesh (QuadRemesh or Mesh Surface)──► Shell Model ◄── Clay Material
                                                      │
                                ┌─────────────────────┼───────────────────┐
                                ▼                     ▼                   ▼
                          Shell Analysis          Shell BESO         Wall Preview
                       (check the start)     (Run = Boolean Toggle)
                                                      │ t (thickness per face)
                                                      ▼
                                               Layer Heights ──h, tv──► Non-planar Layers
                                                                          │
                                                       Layers (polylines), h, w per point
                                                                          ▼
                                                        your toolpath / G-code definition
```

1. **Mesh the mid-surface** of the wall (the surface halfway through the wall thickness).
   Aim for 2 000–20 000 faces with roughly even triangles/quads. Finer meshes are more
   accurate. The bottom edge should sit on the build plate (lowest Z).
2. **Shell Model**: give the starting thickness (e.g. 5 mm). This is your **clay budget**: by
   default BESO keeps this amount of clay and only moves it around. *Fix Base* clamps
   everything at the lowest Z. Keep *Self-weight* on. Add point loads if needed (vectors in N).
3. **Shell Analysis**: sanity-check displacements and stresses before optimising.
4. **Shell BESO**: set **tMin / tMax** to the thinnest and thickest wall you can print. The
   starting thickness must lie between them, ideally near the middle. Set a **filter radius**
   of about 2–3 mesh edge lengths, then set **Run** to true with a Boolean Toggle. Watch the
   preview evolve. *Converged* turns true when it is done. Toggle **Reset** to start again.
   - Leave **Volume Fraction** at 0 to redistribute the same clay, which is the usual case.
   - Set it above 0 only to change the amount of clay: the average wall becomes Vf × tMax.
   - Void mode always needs a volume fraction, because it removes material.
   - **Playback**: connect a 0–1 slider to scrub through the iterations after a run (1 = final).
     The thickness output follows the slider, so downstream components update as well.
5. **Layer Heights**: set **hMin / hMax** from your nozzle (see the table below) and choose how
   thickness turns into layer height. With flow control (e.g. the WASP 40100), both bead width
   and layer height can follow the design, and the flow factor supplies the clay:
   - *Mapping 3, proportional* (**default**): the thinnest wall gets hMin and the thickest gets
     hMax. Connect BESO's tMin–tMax to **Thickness Range** so the mapping is anchored. Otherwise
     a nearly uniform wall would still be spread over the whole height range.
   - *Mapping 2, constant aspect ratio* (h = k·t): also rises with thickness, and keeps the same
     bead shape (height/width) everywhere.

   For fixed flow (no flux control):
   - *Mapping 0, constant bead area*: flow and feed stay fixed and only the nozzle height
     changes, so a thicker wall comes from a lower, more squashed layer.
   - *Mapping 1, linear inverse*: the thickest wall gets hMin and the thinnest gets hMax.

   Structurally, the clay per unit of wall area is set by the **thickness**. A taller layer puts
   more clay down per pass and gives fewer layers over the same height.
6. **Non-planar Layers**: produces one branch per layer.
   - *Method 0 (Conforming)*: every layer is a closed loop and the last layer is the rim.
     Heights are matched as closely as the geometry allows. Check the *Info* and *Preview*
     (colour = height error).
   - *Method 1 (Eikonal)*: heights are matched exactly, but the top may end in partial layers.
   - Connect **Vertex Thickness** from Layer Heights to get the per-point **Bead Area** and
     **Flow Factor**. The flow factor is the clay volume needed at that point, relative to the
     *Nominal Area* (default: the median). Multiply your nominal extrusion by it.
   - The flow factor uses the *realised* layer height, so the wall still comes out at the
     optimised thickness even where the layers can't follow the target height exactly.
   - Feed the polylines (Z per point is already in the curve) and the flow factors into your
     toolpath / G-code definition.

## Printer settings (Delta WASP 40100 Clay, 1.5–3 mm nozzles)

Build volume Ø400 × 1000 mm. Rules of thumb for a single-bead wall. Calibrate them with a
test print for your clay body:

| Nozzle | tMin – tMax (bead width) | hMin – hMax (layer height) | aspect k |
|---|---|---|---|
| 1.5 mm | 1.8 – 3.5 mm | 0.6 – 1.2 mm | 0.35–0.45 |
| 2.0 mm | 2.4 – 4.6 mm | 0.8 – 1.6 mm | 0.35–0.45 |
| 3.0 mm | 3.5 – 7.0 mm | 1.2 – 2.4 mm | 0.35–0.45 |

The component defaults are set for a 3 mm nozzle. Keep the **flow factor** range within what
your extruder can follow quickly. Compare the min–max shown in the *Info* output against what
you see in a test print.

**Walls thicker than one bead.** The tMax values above assume a single straight bead. Paths that
move back and forth within each layer can build much wider walls from the same nozzle. For
example, WeaveSlicer (Friedman-Gerlicz et al., CHI 2024) uses a sinusoidal path whose amplitude
sets the wall thickness, with reported walls of 2–20 mm. If you plan such paths, you can give
BESO a larger tMax and use its thickness field to drive the oscillation. ClayBESO doesn't
generate these paths itself: the thickness field and the layer curves are the inputs for your
own path design.

## Colours and legends

Every component with a coloured preview draws a **legend** (colour bar with values) in the top
left of the Rhino viewports. Turn it off with the *Legend* input, or hide the component's
preview. When several are visible they sit side by side. Blue is low and red is high.

The **Colour Range** input fixes the scale. Values outside the range get the end colours.
Defaults when nothing is connected:

| Component | Shows | Default range |
|---|---|---|
| Shell Analysis | von Mises stress [MPa] | min–max of the result |
| Shell BESO | wall thickness [mm] | tMin–tMax (void mode: min–max) |
| Layer Heights | target layer height [mm] | hMin–hMax |
| Non-planar Layers | layer-height error [%] | symmetric about 0 |

Fix the range to compare runs. With an automatic range, a tiny variation fills the whole
blue-to-red scale.

## Interlayer check (layer height in the structure)

The shell analysis treats the wall as solid. Printed clay is weakest **between layers**: a bead
of width w and height h has rounded sides, so it bonds to the next layer over a flat strip only
about w − h wide. The **Interlayer Check** component:

1. takes the wall thickness (bead width w) and the **Layer Field** φ from Non-planar Layers. The
   realised layer height h and the stacking direction (across the layers, following the real
   non-planar layers) come from φ;
2. rotates the analysis stresses into the layer directions and puts the across-layer force and
   moment through the bonded strip only, a fraction **c = (w − h)/w** of the wall;
3. reports a **utilisation**, the worst of
   - **bond**: tension + shear on the strip vs **η·f**, and
   - **bearing**: compression on the narrowed strip vs **f**, which matters for wet clay
     carrying its own weight.

Inputs: clay **strength f** (or set it on Clay Material) and **bond efficiency η**. η is the
interface strength per unit contact area relative to the bulk clay. Leave it at 1 to see the
geometric effect alone, then calibrate it from tests, for example by comparing printed samples
pulled across the layers with solid ones.

With the same wall, taller layers mean less contact and higher utilisation. On a 6 mm wall,
going from 1 mm to 3 mm layers raises the peak utilisation about 2.7× in the test case. With
*Orthotropic* on (the default), the analysis inside the check also treats the wall as weaker across
the layers, so stresses redistribute as they would in the print.

## Printed layers inside the optimisation

Connect a **Layer Rule** to Shell BESO (and the same rule to Layer Heights and Shell Analysis) and
BESO optimises a wall that is weaker across its layers. Its across-layer stiffness is scaled by
**κ = η · (t − h(t))/t**, using the same bond model as the Interlayer Check, with h(t) from the rule.
The Layer Rule's outputs then match what is printed:

```
Layer Rule ──► Shell BESO ──t──► Layer Heights ──h──► Non-planar Layers
     └──────────────────────────────┘ (same rule on both)
```

- Set the rule's **Thickness Range** to BESO's tMin–tMax, so every component maps thickness the
  same way (the components warn if it is missing).
- Layer directions are taken as horizontal inside BESO; the Interlayer Check uses the real
  non-planar layers afterwards.
- With the proportional mapping, h grows with t, so the contact fraction c stays roughly constant
  and κ mostly acts as a near-uniform across-layer weakening (η × 0.64–0.71 with the defaults). That
  still changes where BESO puts clay: in the test case the layer-aware design is about 2% stiffer
  than the layer-naive one when both are judged with layers. Mappings where h does not follow t
  (e.g. constant bead area) make κ vary with thickness, and BESO then trades layer contact
  against wall thickness.
- Shell BESO's Info shows the rule, η and the κ range.

## Units

Geometry is read in the **Rhino document units** and converted internally. **Thickness, layer
height and bead width are always in millimetres**, forces in **newtons**, stresses in **MPa**.

## Choosing material values

| State | E [MPa] | density [kg/m³] | notes |
|---|---|---|---|
| Green / wet (printing) | 0.1–5 | 1800–2000 | governs collapse while printing |
| Leather hard | 10–50 | ~1900 | |
| Fired | 10 000–30 000 | 2000–2400 | |

For a **stiffness** optimisation the absolute value of E only scales the displacements. The
optimised *shape* does not depend on it. It matters for checking deflections and stresses.

## Limitations — read before trusting results

- Linear elastic, small-displacement analysis of the **finished** geometry. The plugin does not
  simulate the print layer by layer, and it does not check **buckling**, which is often what
  makes wet clay walls fail.
- Flat triangular elements converge from the stiff side on curved shells. Use fine meshes
  (≥ 40 divisions around a vase).
- Void mode may stop at the iteration limit without converging. Holes interrupt continuous
  layers, so check printability.
- No G-code output yet. The plugin gives you polylines with per-point heights and flow factors.

See `docs/THEORY.md` for the maths and references, `docs/VALIDATION.md` for the experimental validation plan, `CONTRIBUTING.md` to work on the code, and
`AGENTS.md` for the rules AI assistants follow in this repo.
