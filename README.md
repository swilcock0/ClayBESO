# ClayBESO — shell optimisation and non-planar layers for 3D printed clay

A Grasshopper plugin for Rhino 8 that:

1. **analyses** a thin clay shell (vase, vessel, column…) under **self-weight** and/or point
   loads with a built-in finite-element shell solver,
2. **optimises** it with **BESO**, either grading the **wall thickness** (no holes) or
   removing material to make **perforations**,
3. turns the optimised thickness into a **variable layer-height field** and slices the shell
   into **non-planar layer toolpaths**: thick, highly loaded regions get low, squashed layers
   (wider bead), and lightly loaded regions get tall, narrow layers.

No other plugins or licences are needed.

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

| Tab | Component | What it does |
|---|---|---|
| 1 Model | **Clay Material** | E [MPa], ν, density [kg/m³], optional strength [MPa]. Defaults = green clay. |
| 1 Model | **Shell Model** | Mesh + material + thickness [mm] + supports (auto base) + loads → model. |
| 2 Analyse | **Shell Analysis** | Displacements, von Mises stress, compliance, deformed preview. |
| 3 Optimise | **Shell BESO** | Live BESO loop (Run / Reset). Mode 0 = thickness, 1 = void. |
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
5. **Layer Heights**: set **hMin / hMax** from your nozzle (see the table below) and choose how
   thickness turns into layer height:
   - *Mapping 0, constant bead area*: flow and feed stay fixed and only the nozzle height
     changes. A thicker wall means a lower, more squashed layer.
   - *Mapping 1, linear*: the thickest wall gets hMin and the thinnest gets hMax.
   - *Mapping 2, constant aspect ratio* (h = k·t): every bead is squashed the same amount.
     This needs flow control, so use it when you can modulate flow, as on the WASP.
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

See `docs/THEORY.md` for the maths and references, `CONTRIBUTING.md` to work on the code, and
`AGENTS.md` for the rules AI assistants follow in this repo.
