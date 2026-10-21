# Component reference

Every ClayBESO Grasshopper component with its inputs and outputs, generated from the source code by
`tests/ClayBeso.Tests/ComponentDocsTests.cs`. **Don't edit this file by hand**: change the component's
descriptions in code, then regenerate (`UPDATE_DOCS=1 dotnet test tests/ClayBeso.Tests --filter ComponentDocs`).
The test suite fails while this file is out of date.

Units: lengths of geometry follow the Rhino document; thickness, layer height and bead width are in mm,
forces in N, stresses in MPa. *Generic* inputs carry ClayBESO objects (a Shell Model, a Clay Material,
a Layer Rule, a Design from Shell BESO, a Print from Print Layers) from the component that makes them.
Per-face lists follow the faces of the triangulated
mesh output by Shell Model; per-vertex lists follow its vertices.

Inputs in **bold** must be connected. The others have a default (shown) or are optional. New inputs
are only ever added at the end, so saved definitions keep working.

- **1 Model**: [Material](#material), [Shell Model](#shell-model)
- **2 Analyse**: [Print Check](#print-check), [Shell Analysis](#shell-analysis)
- **3 Optimise**: [Shell BESO](#shell-beso)
- **4 Print**: [Layer Rule](#layer-rule), [Print Layers](#print-layers), [Wall Preview](#wall-preview)
- **5 Help**: [Help](#help)
- **Legacy (hidden from the toolbar)**: [Interlayer Check](#interlayer-check), [Layer Heights](#layer-heights), [Non-planar Layers](#non-planar-layers), [Print Stages](#print-stages)

## 1 Model

### Material

Nickname `Material` · source `src/ClayBeso.GH/Components/ClayMaterialComponent.cs`

Linear-elastic, isotropic material for the shell analysis. Right-click for presets (Clay, Wood), which fill in the text boxes connected to the inputs. Defaults are for green (wet) clay, the state that governs self-weight collapse while printing.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | Young's Modulus | `Modulus` | Number | item | `1.0` | Young's modulus [MPa]. Green clay ~0.1-5 MPa, leather-hard ~10-50 MPa, fired ~10 000-30 000 MPa; softwood along the grain ~11 000 MPa (preset Wood). |
| 1 | Poisson | `ν` | Number | item | `0.35` | Poisson's ratio [-], 0 to <0.5. |
| 2 | Density | `Density` | Number | item | `1900` | Density [kg/m³]. Clay ~1900, softwood ~420. |
| 3 | Strength | `Strength` | Number | item | `0.03` | Strength f [MPa] for the interlayer and utilisation checks. Default 0.03 MPa is a cautious PLACEHOLDER for soft printing clay: about half the unconfined compressive strength printing clays show on a pocket penetrometer (0.6-1.3 kg/cm² = 0.06-0.13 MPa, Friedman-Gerlicz et al. 2024), because interfaces fail in shear and tension. Measure your own clay: penetrometer reading [kg/cm²] × 0.098 = MPa, then halve it (docs/VALIDATION.md §2). The Wood preset uses 4 MPa (C24 shear strength). 0 = no strength checks. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Material | `Material` | Generic | item | Material for Shell Model. |

### Shell Model

Nickname `Shell` · source `src/ClayBeso.GH/Components/ShellModelComponent.cs`

Build a shell analysis model from a mesh of the mid-surface (e.g. a vase). Quads are split into triangles. Aim for 2 000-20 000 faces.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Mesh** | `Mesh` | Mesh | item | required | Mid-surface mesh of the shell, in document units. |
| 1 | Material | `Material` | Generic | item | optional | Material. Default: green clay (E = 1 MPa, strength 0.03 MPa placeholder). |
| 2 | Thickness | `Thickness` | Number | list | `5.0` | Wall thickness [mm]: one value, or one per mesh face (after triangulation). |
| 3 | Fix Base | `Base` | Boolean | item | `true` | Clamp every vertex within the tolerance of the lowest Z (the print bed). |
| 4 | Supports | `Supports` | Point | list | optional | Extra support points (snapped to the nearest vertex). |
| 5 | Fix Rotations | `Rot` | Boolean | item | `true` | Also fix rotations at supports (clamped). False = pinned. |
| 6 | Tolerance | `Tol` | Number | item | `0.5` | Base detection / snapping tolerance, in document units. |
| 7 | Self-weight | `SW` | Boolean | item | `true` | Include self-weight (gravity along -Z). |
| 8 | Gravity Factor | `g` | Number | item | `1.0` | Self-weight multiplier (e.g. 1.5 as a safety factor). |
| 9 | Load Points | `Load Points` | Point | list | optional | Points where point loads act (snapped to the nearest vertex). |
| 10 | Load Vectors | `Load Vectors` | Vector | list | optional | Point-load forces [N]: one vector, or one per load point. |
| 11 | Frozen Faces | `Fz` | Integer | list | optional | Indices of faces the optimiser must not change (e.g. a rim or foot band). |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Model | `Model` | Generic | item | Shell model. |
| 1 | Mesh | `Mesh` | Mesh | item | Triangulated analysis mesh. Face indices of all per-face outputs refer to this mesh. |
| 2 | Supports | `S` | Point | list | Supported vertices. |
| 3 | Info | `Info` | Text | item | Model summary. |

## 2 Analyse

### Print Check

Nickname `Check` · source `src/ClayBeso.GH/Components/PrintCheckComponent.cs`

Structural check of a print, in one place: the finished piece under all loads, and the piece while it is printed (stages loaded by their own weight). Layer interfaces carry force only through their bonded strip c = (w - h)/w, and the wall is weaker across the layers. With Buckling on, it also finds how many times its loads each stage carries before the wall buckles.

Connect the Print output of Print Layers. Pick what to see with Show, and when with Stage.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Print** | `Print` | Generic | item | required | Print output of Print Layers. |
| 1 | Strength | `Strength` | Number | item | `0.0` | Clay strength [MPa] for the interlayer utilisation. 0 = the Material strength; if that is 0 too, utilisation isn't reported. |
| 2 | Show | `Show` | Integer | item | `0` | 0 = interlayer utilisation, 1 = interface angle, 2 = von Mises, 3 = displacement, 4 = layer contact, 5 = buckling mode (needs Buckling on; dropdown appears when placed). |
| 3 | Stage | `Stage` | Integer | item | `-1` | -1 = finished piece, -2 = worst print stage, 0..N-1 = that stage (dropdown appears when placed; use a slider for a specific stage). |
| 4 | Stages | `Stages` | Integer | item | `8` | Print stages to check (0 = finished piece only). |
| 5 | Bond Efficiency | `η` | Number | item | `0.0` | Interface efficiency 0-1 (calibrate with docs/VALIDATION.md section 4). 0 = the design's, or 1. |
| 6 | Orthotropic | `Orth` | Boolean | item | `true` | Treat the wall as weaker across its layers (stiffness × η·c from the printed layer heights). |
| 7 | Stage Point Loads | `SPL` | Boolean | item | `false` | Also apply the point loads during printing (they always act on the finished piece). |
| 8 | Deform Scale | `DS` | Number | item | `10.0` | Displacement exaggeration for the preview. |
| 9 | Colour Range | `CR` | Interval | item | optional | Fix the preview colour scale. Empty = automatic for what is shown. |
| 10 | Legend | `Lg` | Boolean | item | `true` | Draw a colour legend in the viewport. |
| 11 | Buckling | `Buckling` | Boolean | item | `true` | Linear buckling analysis of the finished piece and of every print stage (takes longer). Turn off while BESO is running if it slows things down. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Max Utilisation | `Max Utilisation` | Number | item | Highest interlayer utilisation, finished piece or any stage (> 1 = expected to fail). |
| 1 | Preview | `Preview` | Mesh | item | The shown stage, deformed and coloured by the Show option. |
| 2 | Info | `Info` | Text | item | Summary. |
| 3 | Worst Stage | `Worst Stage` | Integer | item | Index of the most critical print stage (-1 if none). |
| 4 | Worst Point | `Worst Point` | Point | item | Where the highest value of the shown quantity is. |
| 5 | Values | `V` | Number | list | Per face: the shown quantity for the shown stage (NaN where not printed or undefined). |
| 6 | Stage Height | `z` | Number | list | Per stage: height of the highest printed point (document units). |
| 7 | Stage Utilisation | `Us` | Number | list | Per stage: highest interlayer utilisation (von Mises [MPa] without a strength). |
| 8 | Force Flow | `FF` | Vector | list | Per face, finished piece: dominant principal force direction; length 0-1 = how directional. |
| 9 | Flow Lines | `FL` | Line | list | Finished piece: a short line per face along the force flow, for viewing. |
| 10 | Buckling Factor | `Buckling Factor` | Number | item | Lowest buckling load factor [-], finished piece or any stage: the loads times this make a perfect shell buckle. < 1 = buckles; real walls buckle well below it, so aim higher (see Info). Infinity = nothing in compression. |
| 11 | Stage Buckling | `λs` | Number | list | Per stage: buckling load factor under its own weight [-] (how many times heavier the clay could be before that stage buckles). |

### Shell Analysis

Nickname `Analyse` · source `src/ClayBeso.GH/Components/ShellAnalysisComponent.cs`

Linear static analysis of a shell: stresses, displacements and force flow, and optionally linear buckling (how many times the loads the shell carries before it buckles).

Connect a Shell Model to check the starting shell, or the Design output of Shell BESO to check the optimised one (its thickness, Layer Rule and bond come with it).

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell Model (starting shell) or the Design output of Shell BESO (optimised shell). |
| 1 | Show | `Show` | Integer | item | `0` | Preview colours: 0 = von Mises stress, 1 = displacement, 2 = buckling mode (turns Buckling on) (a dropdown appears when you place the component). |
| 2 | Thickness | `Thickness` | Number | list | optional | Optional thickness per face [mm], replacing the model's or design's. |
| 3 | Layer Rule | `Layer Rule` | Generic | item | optional | Optional Layer Rule: analyse the wall as weaker across its layers (κ = η·(t − h)/t). Default: the design's rule, if any. |
| 4 | Bond Efficiency | `η` | Number | item | `0.0` | With a Layer Rule: interface efficiency 0-1. 0 = the design's, or 1. |
| 5 | Deform Scale | `DS` | Number | item | `10.0` | Displacement exaggeration for the preview mesh. |
| 6 | Colour Range | `CR` | Interval | item | optional | Fix the colour scale (MPa for stress, mm for displacement). Empty = min-max of the result. |
| 7 | Legend | `Lg` | Boolean | item | `true` | Draw a colour legend in the viewport. |
| 8 | Buckling | `Buckling` | Boolean | item | `false` | Also run a linear buckling analysis (takes longer; off by default so a running BESO isn't slowed down). |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Preview | `Preview` | Mesh | item | Deformed mesh coloured by the Show option. |
| 1 | Info | `Info` | Text | item | Summary: peak stress and where, displacement, layers. |
| 2 | Max Displacement | `Max Displacement` | Number | item | Largest displacement [mm]. |
| 3 | Von Mises | `Von Mises` | Number | list | Von Mises stress per face, worst of the two surfaces [MPa]. |
| 4 | Displacements | `d` | Vector | list | Displacement per vertex [mm]. |
| 5 | Utilisation | `U` | Number | list | σ / strength per face (Strength on the Material component). |
| 6 | Compliance | `C` | Number | item | Compliance F·u [N·mm]. Lower = stiffer. |
| 7 | Max Stress Point | `σMax` | Point | item | Centre of the face with the highest von Mises stress. |
| 8 | Force Flow | `FF` | Vector | list | Per face: direction of the dominant principal in-plane force; length 0-1 = how directional it is. |
| 9 | Flow Lines | `FL` | Line | list | A short line per face along the force flow, for viewing force-flow trajectories. |
| 10 | Buckling Factor | `Buckling Factor` | Number | item | With Buckling on: lowest buckling load factor [-]. The loads (self-weight and point loads) times this make a perfect shell buckle; < 1 = buckles. Infinity = nothing in compression. |
| 11 | Buckling Mode | `φ` | Vector | list | With Buckling on: buckling shape per vertex, scaled so the largest movement is 1 (no units). |

## 3 Optimise

### Shell BESO

Nickname `BESO` · source `src/ClayBeso.GH/Components/BesoComponent.cs`

Bi-directional evolutionary structural optimisation of a shell for minimum compliance (maximum stiffness), including self-weight.

Mode 0 = Thickness: graded wall thickness, no holes (always printable). By default it keeps the clay volume of the Shell Model thickness and only redistributes it.

Mode 1 = Void: classic BESO, removes faces to make perforations.

Set Run to true (Boolean Toggle) to iterate. Set Reset to true to start over.

Connect the Design output to Print Layers (and to Shell Analysis to analyse the result).

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell model. |
| 1 | Run | `Run` | Boolean | item | `false` | Iterate while true (connect a Boolean Toggle). |
| 2 | Reset | `Reset` | Boolean | item | `false` | Restart from the initial design. |
| 3 | Mode | `Mode` | Integer | item | `0` | 0 = Thickness (graded wall), 1 = Void (perforations). A dropdown appears when you place the component. |
| 4 | Min Thickness | `Min Thickness` | Number | item | `3.5` | Thickness mode: thinnest printable wall [mm] (about 1.1x nozzle diameter). |
| 5 | Max Thickness | `Max Thickness` | Number | item | `7.0` | Thickness mode: thickest wall [mm] (widest bead you can extrude, about 2.3x nozzle). |
| 6 | Filter Radius | `Filter Radius` | Number | item | `0.0` | Filter radius in document units. 0 (default) = automatic: 3× the average mesh edge length (Info shows the value used). In thickness mode the wall thickness is averaged over this radius, so it sets how wide the transitions between thin and thick wall are (no stair-steps between faces). Use at least 2-3× the mesh edge length; larger = smoother, more gradual walls. |
| 7 | Layer Rule | `Layer Rule` | Generic | item | optional | Optional Layer Rule. When connected, the wall is weaker across its layers: across-layer stiffness × κ = η·(t − h)/t, with h from the rule. The rule travels on in the Design output. |
| 8 | Bond Efficiency | `η` | Number | item | `1.0` | With a Layer Rule: interface efficiency 0-1 (calibrate from tests, see docs/VALIDATION.md §4). 1 = only the contact geometry weakens the wall. |
| 9 | Volume Fraction | `Vf` | Number | item | `0.0` | Thickness mode: 0 (default) = keep the clay volume of the Shell Model thickness and only redistribute it. A value > 0 sets a different target: average thickness = Vf × tMax (must be above tMin/tMax). Void mode: fraction of the full wall to keep (required). |
| 10 | Playback | `Playback` | Number | item | `1.0` | Show the design at a point of the run: 0 = first iteration, 1 = final (default). Connect a 0-1 slider to scrub through the optimisation. Thickness, Design and the preview follow it; it never restarts the run. |
| 11 | Max Iterations | `N` | Integer | item | `100` | Iteration limit. |
| 12 | Evolution Rate | `ER` | Number | item | `0.03` | Max relative volume change per iteration. |
| 13 | Penalty | `p` | Number | item | `3.0` | Void mode: stiffness penalty exponent. |
| 14 | Admission Ratio | `AR` | Number | item | `0.02` | Void mode: max fraction of volume re-added per iteration. |
| 15 | Colour Range | `CR` | Interval | item | optional | Fix the thickness colour scale [mm]. Empty = tMin to tMax in thickness mode, min-max in void mode. |
| 16 | Legend | `Lg` | Boolean | item | `true` | Draw a colour legend in the viewport. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Design | `Design` | Generic | item | The model with this thickness and the Layer Rule and bond it was optimised with, in one wire. Connect it to Print Layers or Shell Analysis. |
| 1 | Thickness | `Thickness` | Number | list | Wall thickness per face [mm] (0 for removed faces in void mode). |
| 2 | Preview | `Preview` | Mesh | item | Mesh coloured by thickness (void faces hidden). |
| 3 | Info | `Info` | Text | item | Status. |
| 4 | Converged | `Converged` | Boolean | item | True once compliance has stabilised at the target volume. |
| 5 | Iteration | `i` | Integer | item | Iterations done (or the one shown in playback). |
| 6 | Compliance | `C` | Number | list | Compliance history [N·mm]. |
| 7 | Volume | `V` | Number | list | Volume-fraction history. |
| 8 | Solid | `S` | Boolean | list | Per face: true if material is kept. |
| 9 | Sensitivity | `α` | Number | list | Filtered sensitivity per face (higher = more useful material). |

## 4 Print

### Layer Rule

Nickname `Rule` · source `src/ClayBeso.GH/Components/LayerRuleComponent.cs`

How wall thickness turns into layer height, as one object shared by Layer Heights, Shell BESO and Shell Analysis. Sharing it means BESO optimises with the same layer heights you will print (needed for the across-layer stiffness).

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | Mapping | `Mapping` | Integer | item | `3` | 3 = proportional (thicker wall, taller layer), 2 = constant aspect ratio (h = k·t), 0 = constant bead area (fixed flow), 1 = linear inverse (fixed flow). |
| 1 | Min Layer Height | `Min Height` | Number | item | `1.0` | Smallest layer height [mm]. At least 0.25× nozzle diameter (0.75 mm for a 3 mm nozzle). |
| 2 | Max Layer Height | `Max Height` | Number | item | `2.25` | Largest layer height [mm]. At most 0.75× nozzle diameter (2.25 mm for a 3 mm nozzle). |
| 3 | Bead Area | `A` | Number | item | `0.0` | Bead cross-section [mm²] for mapping 0. 0 = automatic. |
| 4 | Aspect Ratio | `k` | Number | item | `0.4` | Layer height / bead width for mapping 2. |
| 5 | Thickness Range | `Thickness Range` | Interval | item | optional | Mappings 1 and 3: thickness [mm] mapped onto hMin..hMax. Empty = BESO's tMin..tMax inside BESO, or the field's own range elsewhere. Set it explicitly to make every component use the same mapping. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Layer Rule | `Layer Rule` | Generic | item | Layer rule: connect it to Shell BESO (it then travels on in the Design) or to Print Layers. |

### Print Layers

Nickname `Print` · source `src/ClayBeso.GH/Components/PrintLayersComponent.cs`

From a design to printable non-planar layers in one step: layer heights from the wall thickness (Layer Rule), the layer field, the toolpaths, the flow factor and the overhang check.

Connect the Design output of Shell BESO (it carries the model, thickness, Layer Rule and bond), or a Shell Model for its own uniform wall.

Method: Conforming (closed layers fitting base and rim), Eikonal (exact heights from the base), or Force-aligned (layers turn to lie across the force flow; the force flow is computed here, nothing to wire).

Connect the Print output to Print Check.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Design** | `Design` | Generic | item | required | Design output of Shell BESO, or a Shell Model (uses its own thickness). |
| 1 | Method | `Method` | Integer | item | `0` | 0 = Conforming, 1 = Eikonal, 2 = Force-aligned (a dropdown appears when you place the component). |
| 2 | Show | `Show` | Integer | item | `0` | Preview colours: 0 = layer height, 1 = layer-height error, 2 = wall thickness, 3 = wall lean (a dropdown appears when you place the component). |
| 3 | Layer Rule | `Layer Rule` | Generic | item | optional | Optional Layer Rule, replacing the design's. Default (none anywhere): proportional, 1-2.25 mm. |
| 4 | Thickness | `Thickness` | Number | list | optional | Optional wall thickness per face [mm], replacing the design's. |
| 5 | Max Deviation | `Dev` | Number | item | `30.0` | Force-aligned: most the layers may turn away from the conforming layering [deg]. |
| 6 | Max Overhang | `OvMax` | Number | item | `45.0` | Wall lean from vertical [deg] above which a point is flagged (planar clay starts to slump around 45°; calibrate with docs/VALIDATION.md section 7). |
| 7 | Max Tilt | `TiltMax` | Number | item | `30.0` | Layer slope along the path [deg] above which a point is flagged (nozzle scraping on a 3-axis printer). |
| 8 | Vertical | `Vz` | Boolean | item | `true` | Measure layer height vertically (Z) rather than along the wall. |
| 9 | Smoothing | `Sm` | Integer | item | `5` | Smoothing passes on the layer-height field. |
| 10 | Tolerance | `Tol` | Number | item | `0.5` | Band (document units) at the bottom/top used to find the base and the rim. |
| 11 | Seam | `Seam` | Vector | item | `(1, 0, 0)` | Closed layers start at the point furthest in this direction. |
| 12 | Nominal Area | `A0` | Number | item | `0.0` | Bead cross-section [mm²] for flow factor 1. 0 = median of the paths. |
| 13 | Colour Range | `CR` | Interval | item | optional | Fix the preview colour scale. Empty = automatic for what is shown. |
| 14 | Legend | `Lg` | Boolean | item | `true` | Draw a colour legend in the viewport. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Layers | `Layers` | Curve | tree | Layer toolpaths (one branch per layer) on the wall mid-surface, in document units. |
| 1 | Flow Factor | `Flow Factor` | Number | tree | Per point: flow multiplier (1 = Nominal Area) that gives the wall its thickness at the realised layer height. |
| 2 | Print | `Print` | Generic | item | Everything about this print (design, layers, paths). Connect it to Print Check. |
| 3 | Preview | `Preview` | Mesh | item | Mesh coloured by the Show option. |
| 4 | Info | `Info` | Text | item | Summary: layers, flow range, printability warnings. |
| 5 | Realised Height | `Realised Height` | Number | tree | Per point: layer height the layers actually have [mm]. |
| 6 | Width | `Width` | Number | tree | Per point: bead width = wall thickness [mm]. |
| 7 | Overhang Ratio | `OvR` | Number | tree | Per point: how far the bead sits past the one below, as a fraction of its width (> 0.5 = more than half unsupported). |
| 8 | Layer Tilt | `Tilt` | Number | tree | Per point: slope of the layer along the path [deg]. |
| 9 | Wall Lean | `Lean` | Number | tree | Per point: wall lean from vertical [deg], across the bead. |
| 10 | Bead Offset | `Off` | Number | tree | Per point: horizontal offset past the bead below [mm]. |
| 11 | Bead Area | `A` | Number | tree | Per point: bead cross-section [mm²] (extruded volume per mm of path). |
| 12 | Target Height | `h` | Number | tree | Per point: target layer height from the Layer Rule [mm]. |
| 13 | Layer Field | `φ` | Number | list | Per vertex: layer index field (layer k is φ = k). |

### Wall Preview

Nickname `Wall` · source `src/ClayBeso.GH/Components/WallPreviewComponent.cs`

Thicken the mid-surface by the per-face thickness to preview the printed wall as a solid.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell Model, or the Design output of Shell BESO. |
| 1 | Thickness | `Thickness` | Number | list | optional | Wall thickness per face [mm]. Default: the design's or model's thickness. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Wall | `Wall` | Mesh | item | Closed mesh of the thickened wall (document units). |

## 5 Help

### Help

Nickname `Help` · source `src/ClayBeso.GH/Components/HelpComponent.cs`

Opens the ClayBESO component reference (every input and output of every component) in your browser, and shows the installed ClayBESO version.

Double-click this component, or connect a Boolean Toggle (every click opens the page) or a Button (each press opens it) to Open.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | Open | `Open` | Boolean | item | `false` | Opens the component reference: on every click of a Boolean Toggle, or on each press of a Button. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | URL | `URL` | Text | item | Address of the component reference. |
| 1 | Version | `Version` | Text | item | Installed ClayBESO release version (e.g. 0.3.0). |

## Legacy (hidden from the toolbar)

### Interlayer Check

Nickname `Interlayer` · source `src/ClayBeso.GH/Components/InterlayerCheckComponent.cs`

Legacy: replaced by Print Check, which does this with fewer connections. Kept so older definitions still open.

Check the bond between printed layers. Stresses are split into along-layer and across-layer using the real (non-planar) layer directions. Each interface carries them only through its bonded strip, c = (w - h)/w of the wall (bead width w = wall thickness, h = layer height), so taller layers on the same wall bond over less.

Utilisation = worst of bond failure (tension + shear vs η·f) and bearing of the narrowed strip (compression vs f). Above 1 = expected to fail.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell model. |
| 1 | Thickness | `t` | Number | list | optional | Wall thickness per face [mm] (e.g. from Shell BESO). Default: the model's thickness. |
| 2 | **Layer Field** | `φ` | Number | list | required | Layer field per vertex, from the Layer Field output of Non-planar Layers. |
| 3 | Vertical | `Vz` | Boolean | item | `true` | Same setting as on Non-planar Layers (layer height measured in Z). |
| 4 | Strength | `f` | Number | item | `0.0` | Bulk clay strength [MPa]. 0 = use the Strength of the Material. |
| 5 | Bond Efficiency | `η` | Number | item | `1.0` | Interface strength per unit contact area relative to the bulk (0-1). Calibrate from tests; 1 = only the contact geometry weakens the interface. |
| 6 | Min Contact | `cMin` | Number | item | `0.05` | Lower bound on the contact fraction (a round bead still touches over a small strip). |
| 7 | Colour Range | `CR` | Interval | item | optional | Fix the utilisation colour scale. Empty = 0 to the maximum (or 1 if lower). |
| 8 | Legend | `Lg` | Boolean | item | `true` | Draw a colour legend in the viewport. |
| 9 | Orthotropic | `Orth` | Boolean | item | `true` | Analyse the wall as weaker across its layers (stiffness × η·c, with c from the realised layer heights), so stresses redistribute as they would in the print. False = isotropic wall, stresses only checked at the interfaces. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Utilisation | `U` | Number | list | Per face: worst of bond and bearing utilisation (NaN where the layer direction is undefined, e.g. a flat floor). |
| 1 | Bond Utilisation | `Ub` | Number | list | Per face: sqrt(σn+² + τ²) / (η f) on the contact strip. |
| 2 | Bearing Utilisation | `Uc` | Number | list | Per face: compression on the contact strip / f. |
| 3 | Contact | `c` | Number | list | Per face: bonded fraction of the wall, (w - h)/w. |
| 4 | Layer Height | `h` | Number | list | Per face: realised layer height [mm]. |
| 5 | Tension | `σt` | Number | list | Per face: largest tensile stress across the interface [MPa]. |
| 6 | Compression | `σc` | Number | list | Per face: largest compressive stress across the interface [MPa]. |
| 7 | Shear | `τ` | Number | list | Per face: largest shear stress along the interface [MPa]. |
| 8 | Preview | `P` | Mesh | item | Mesh coloured by utilisation (grey = not checked). |
| 9 | Worst Point | `Umax` | Point | item | Centre of the face with the highest utilisation. |
| 10 | Info | `I` | Text | item | Summary. |
| 11 | Interface Angle | `θf` | Number | list | Per face: angle [deg] between the force flow and the stacking direction. 0 = force crosses the layers squarely (interfaces in compression/tension); 90 = force runs along the layers (interfaces in shear, layers can slide). |
| 12 | Force Flow | `FF` | Vector | list | Per face: dominant principal in-plane force direction; length 0-1 = how directional it is. |

### Layer Heights

Nickname `LayerH` · source `src/ClayBeso.GH/Components/LayerHeightComponent.cs`

Legacy: replaced by Print Layers, which does this with fewer connections. Kept so older definitions still open.

Turn a wall-thickness field (e.g. from Shell BESO) into a target layer height per vertex.

Mapping 3 (default) = proportional: thinnest wall gets hMin, thickest gets hMax. More demand means a wider bead AND a taller layer, with the flow raised to supply both (Flow Factor output of Non-planar Layers).

Mapping 2 = constant aspect ratio h = k·t: also rises with thickness, same bead shape everywhere.

Mappings 0 and 1 are for fixed flow: 0 = constant bead area (thick walls by squashing the bead with a lower layer), 1 = linear inverse (thickest wall gets hMin).

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell model (for the mesh). |
| 1 | **Thickness** | `t` | Number | list | required | Wall thickness per face [mm]. |
| 2 | Min Layer Height | `hMin` | Number | item | `1.0` | Smallest layer height [mm]. At least 0.25× nozzle diameter (Vele et al. 2023: 25-75 %), i.e. 0.4 mm for a 1.5 mm nozzle, 0.75 mm for 3 mm. |
| 3 | Max Layer Height | `hMax` | Number | item | `2.25` | Largest layer height [mm]. At most 0.75× nozzle diameter (Vele et al. 2023: 25-75 %), i.e. 1.1 mm for a 1.5 mm nozzle, 2.25 mm for 3 mm. |
| 4 | Mapping | `Map` | Integer | item | `3` | 3 = proportional (default: thicker wall, taller layer), 2 = constant aspect ratio (h = k·t), 0 = constant bead area (fixed flow), 1 = linear inverse (fixed flow). |
| 5 | Bead Area | `A` | Number | item | `0.0` | Bead cross-section [mm²] for mapping 0. 0 = automatic. |
| 6 | Smoothing | `Sm` | Integer | item | `5` | Smoothing passes on the height field. |
| 7 | Aspect Ratio | `k` | Number | item | `0.4` | Layer height / bead width for mapping 2. Clay beads are typically 0.3-0.5. |
| 8 | Colour Range | `CR` | Interval | item | optional | Fix the layer-height colour scale [mm]. Empty = hMin to hMax. |
| 9 | Legend | `Lg` | Boolean | item | `true` | Draw a colour legend in the viewport. |
| 10 | Thickness Range | `tR` | Interval | item | optional | Mappings 1 and 3: thickness interval [mm] mapped onto hMin..hMax, e.g. BESO's tMin To tMax. Empty = min-max of the field (a nearly uniform wall would then still use the full height range). |
| 11 | Layer Rule | `Rule` | Generic | item | optional | Optional Layer Rule. When connected it replaces Mapping, hMin, hMax, Bead Area, Aspect Ratio and Thickness Range, so these heights match what Shell BESO assumed. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Layer Heights | `h` | Number | list | Target layer height per vertex [mm]. |
| 1 | Vertex Thickness | `tv` | Number | list | Wall thickness averaged to the vertices [mm] (= bead width). |
| 2 | Bead Area | `A` | Number | item | Bead cross-section used [mm²]. |
| 3 | Preview | `P` | Mesh | item | Mesh coloured by layer height. |

### Non-planar Layers

Nickname `Layers` · source `src/ClayBeso.GH/Components/NonPlanarLayersComponent.cs`

Legacy: replaced by Print Layers, which does this with fewer connections. Kept so older definitions still open.

Slice the shell into non-planar layer toolpaths whose spacing follows the target layer heights.

Method 0 = Conforming: layers fit both the base and the top rim, so every layer is a closed loop (heights matched as closely as possible). Check the error output.

Method 1 = Eikonal: exact local layer heights grown from the base. The top may end in partial layers.

Method 2 = Force-aligned: like Conforming, but layers turn to lie across the force flow (Force Flow from Shell Analysis), so interfaces carry compression rather than shear (Vele, Kurilla & Achten 2023).

Also checks printability per point: wall lean across the bead, bead offset past the bead below, and layer tilt along the path (nozzle scraping on a 3-axis printer).

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell model (for the mesh). |
| 1 | **Layer Heights** | `h` | Number | list | required | Target layer height per vertex [mm] (from Layer Heights). |
| 2 | Vertex Thickness | `tv` | Number | list | optional | Optional bead width per vertex [mm], passed through per point. |
| 3 | Method | `Mth` | Integer | item | `0` | 0 = Conforming (closed layers, fits base and rim), 1 = Eikonal (exact heights), 2 = Force-aligned (needs Force Flow). |
| 4 | Vertical | `Vz` | Boolean | item | `true` | Measure layer height vertically (Z) rather than along the wall. |
| 5 | Tolerance | `Tol` | Number | item | `0.5` | Band (document units) at the bottom/top used to find the base and the rim. |
| 6 | Seam | `Seam` | Vector | item | `(1, 0, 0)` | Closed layers start at the point furthest in this direction. |
| 7 | Nominal Area | `A0` | Number | item | `0.0` | Bead cross-section [mm²] that corresponds to 100% flow. 0 = median of the paths. |
| 8 | Colour Range | `CR` | Interval | item | optional | Fix the height-error colour scale [%], e.g. -20 to 20. Empty = symmetric about 0. |
| 9 | Legend | `Lg` | Boolean | item | `true` | Draw a colour legend in the viewport. |
| 10 | Force Flow | `FF` | Vector | list | optional | Method 2: force flow per mesh face, from the Force Flow output of Shell Analysis (or Interlayer Check). |
| 11 | Max Deviation | `Dev` | Number | item | `30.0` | Method 2: most the layers may turn away from the conforming layering [deg]. Larger = more aligned with the force, but steeper layer tilts. |
| 12 | Max Overhang | `OvMax` | Number | item | `45.0` | Wall lean from vertical [deg] above which a point is flagged. 45 = where planar clay prints started to slump in Vele and Achten (2023); calibrate with docs/VALIDATION.md section 7. |
| 13 | Max Tilt | `TiltMax` | Number | item | `30.0` | Layer slope along the path [deg] above which a point is flagged (a nozzle that cannot tilt scrapes steep layers). Calibrate with docs/VALIDATION.md section 7. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Layers | `L` | Curve | tree | Layer toolpaths (one branch per layer), on the wall mid-surface, in document units. |
| 1 | Target Height | `h` | Number | tree | Target layer height at each path point [mm]. |
| 2 | Realised Height | `hr` | Number | tree | Layer height the layers actually have at each point [mm]. |
| 3 | Width | `w` | Number | tree | Bead width (wall thickness) at each point [mm]. |
| 4 | Layer Field | `φ` | Number | list | Layer index field per vertex (layer k is φ = k). |
| 5 | Layer Count | `n` | Integer | item | Number of layers. |
| 6 | Preview | `P` | Mesh | item | Mesh coloured by relative layer-height error (realised vs target). |
| 7 | Info | `I` | Text | item | Summary. |
| 8 | Bead Area | `A` | Number | tree | Bead cross-section at each point [mm²] for width w and the realised height. Extruded volume per mm of path. Needs Vertex Thickness. |
| 9 | Flow Factor | `F` | Number | tree | Flow multiplier at each point (1 = Nominal Area). Multiply your nominal extrusion / flow % by this, so the wall gets width w at the realised layer height. |
| 10 | Wall Lean | `Lean` | Number | tree | Per point: wall lean from vertical [deg], across the bead. 0 = vertical wall. |
| 11 | Layer Tilt | `Tilt` | Number | tree | Per point: slope of the layer along the path [deg]. |
| 12 | Bead Offset | `Off` | Number | tree | Per point: how far the bead sits past the one below [mm] = realised height x tan(lean). |
| 13 | Overhang Ratio | `OvR` | Number | tree | Per point: bead offset / bead width, the unsupported fraction of the bead. Needs Vertex Thickness. |

### Print Stages

Nickname `Stages` · source `src/ClayBeso.GH/Components/PrintStagesComponent.cs`

Legacy: replaced by Print Check, which does this with fewer connections. Kept so older definitions still open.

Analyse the shell while it is being printed: at each stage only the layers printed so far (from the Layer Field of Non-planar Layers) exist, loaded by their own weight. Overhangs and soft clay usually fail part-way up, which an analysis of the finished piece can't show.

With a clay strength, each stage also gets the interlayer check, and the worst stage is the one with the highest interlayer utilisation; without one, the highest von Mises stress. Clay stiffness is taken as the same at every height (no ageing yet).

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell model. |
| 1 | Thickness | `t` | Number | list | optional | Wall thickness per face [mm] (e.g. from Shell BESO). Default: the model's thickness. |
| 2 | **Layer Field** | `φ` | Number | list | required | Layer field per vertex, from the Layer Field output of Non-planar Layers. |
| 3 | Stages | `N` | Integer | item | `10` | Number of stages, evenly spaced in layers up to the last one. |
| 4 | Show Stage | `Show` | Integer | item | `-1` | Stage shown in the preview: 0 = first ... N-1 = finished piece; -1 = the worst stage. |
| 5 | Vertical | `Vz` | Boolean | item | `true` | Same setting as on Non-planar Layers (layer height measured in Z). |
| 6 | Strength | `f` | Number | item | `0.0` | Clay strength [MPa] for the interlayer check at each stage. 0 = use the Material strength; if that is 0 too, the check is skipped. |
| 7 | Bond Efficiency | `η` | Number | item | `1.0` | Interface efficiency 0-1 (see Interlayer Check). |
| 8 | Orthotropic | `Orth` | Boolean | item | `true` | Treat the wall as weaker across its layers (stiffness × η·c from the realised layer heights). |
| 9 | Point Loads | `PL` | Boolean | item | `false` | Also apply the model's point loads at every stage (normally they act only on the finished piece). |
| 10 | Deform Scale | `DS` | Number | item | `10.0` | Displacement exaggeration for the preview. |
| 11 | Colour Range | `CR` | Interval | item | optional | Fix the preview colour scale. Empty = 0 to the maximum over all stages (the same scale for every stage). |
| 12 | Legend | `Lg` | Boolean | item | `true` | Draw a colour legend in the viewport. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Stage Layer | `L` | Integer | list | Per stage: printed up to this layer. |
| 1 | Stage Height | `z` | Number | list | Per stage: height of the highest printed point (document units). |
| 2 | Max Displacement | `dMax` | Number | list | Per stage: largest displacement [mm]. |
| 3 | Max Von Mises | `σMax` | Number | list | Per stage: largest von Mises stress [MPa]. |
| 4 | Max Utilisation | `Umax` | Number | list | Per stage: largest interlayer utilisation (empty without a strength). |
| 5 | Worst Stage | `Worst` | Integer | item | Index of the most critical stage. |
| 6 | Preview | `P` | Mesh | item | The shown stage, deformed and coloured by interlayer utilisation (or von Mises without a strength). |
| 7 | Info | `I` | Text | item | Summary. |
