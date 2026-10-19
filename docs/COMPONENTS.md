# Component reference

Every ClayBESO Grasshopper component with its inputs and outputs, generated from the source code by
`tests/ClayBeso.Tests/ComponentDocsTests.cs`. **Don't edit this file by hand**: change the component's
descriptions in code, then regenerate (`UPDATE_DOCS=1 dotnet test tests/ClayBeso.Tests --filter ComponentDocs`).
The test suite fails while this file is out of date.

Units: lengths of geometry follow the Rhino document; thickness, layer height and bead width are in mm,
forces in N, stresses in MPa. *Generic* inputs carry ClayBESO objects (a Shell Model, a Clay Material,
a Layer Rule) from the component that makes them. Per-face lists follow the faces of the triangulated
mesh output by Shell Model; per-vertex lists follow its vertices.

Inputs in **bold** must be connected. The others have a default (shown) or are optional. New inputs
are only ever added at the end, so saved definitions keep working.

- **1 Model**: [Clay Material](#clay-material), [Shell Model](#shell-model)
- **2 Analyse**: [Shell Analysis](#shell-analysis), [Interlayer Check](#interlayer-check)
- **3 Optimise**: [Shell BESO](#shell-beso)
- **4 Print**: [Layer Heights](#layer-heights), [Layer Rule](#layer-rule), [Non-planar Layers](#non-planar-layers), [Wall Preview](#wall-preview)
- **5 Help**: [Help](#help)

## 1 Model

### Clay Material

Nickname `Clay` · source `src/ClayBeso.GH/Components/ClayMaterialComponent.cs`

Linear-elastic material for the shell analysis. Defaults are for green (wet) clay, the state that governs self-weight collapse while printing.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | Young's Modulus | `E` | Number | item | `1.0` | Young's modulus [MPa]. Green clay ~0.1-5 MPa, leather-hard ~10-50 MPa, fired ~10 000-30 000 MPa. |
| 1 | Poisson | `ν` | Number | item | `0.35` | Poisson's ratio [-], 0 to <0.5. |
| 2 | Density | `ρ` | Number | item | `1900` | Density [kg/m³]. |
| 3 | Strength | `f` | Number | item | `0` | Optional allowable stress [MPa] for utilisation output. 0 = not used. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Material | `Mat` | Generic | item | Clay material. |

### Shell Model

Nickname `Shell` · source `src/ClayBeso.GH/Components/ShellModelComponent.cs`

Build a shell analysis model from a mesh of the mid-surface (e.g. a vase). Quads are split into triangles. Aim for 2 000-20 000 faces.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Mesh** | `M` | Mesh | item | required | Mid-surface mesh of the shell, in document units. |
| 1 | Material | `Mat` | Generic | item | optional | Clay Material. Default: green clay (E = 1 MPa). |
| 2 | Thickness | `t` | Number | list | `5.0` | Wall thickness [mm]: one value, or one per mesh face (after triangulation). |
| 3 | Fix Base | `Base` | Boolean | item | `true` | Clamp every vertex within the tolerance of the lowest Z (the print bed). |
| 4 | Supports | `S` | Point | list | optional | Extra support points (snapped to the nearest vertex). |
| 5 | Fix Rotations | `Rot` | Boolean | item | `true` | Also fix rotations at supports (clamped). False = pinned. |
| 6 | Tolerance | `Tol` | Number | item | `0.5` | Base detection / snapping tolerance, in document units. |
| 7 | Self-weight | `SW` | Boolean | item | `true` | Include self-weight (gravity along -Z). |
| 8 | Gravity Factor | `g` | Number | item | `1.0` | Self-weight multiplier (e.g. 1.5 as a safety factor). |
| 9 | Load Points | `LP` | Point | list | optional | Points where point loads act (snapped to the nearest vertex). |
| 10 | Load Vectors | `LV` | Vector | list | optional | Point-load forces [N]: one vector, or one per load point. |
| 11 | Frozen Faces | `Fz` | Integer | list | optional | Indices of faces the optimiser must not change (e.g. a rim or foot band). |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Model | `Model` | Generic | item | Shell model. |
| 1 | Mesh | `M` | Mesh | item | Triangulated analysis mesh. Face indices of all per-face outputs refer to this mesh. |
| 2 | Supports | `S` | Point | list | Supported vertices. |
| 3 | Info | `I` | Text | item | Model summary. |

## 2 Analyse

### Shell Analysis

Nickname `Analyse` · source `src/ClayBeso.GH/Components/ShellAnalysisComponent.cs`

Linear static analysis of a shell model, optionally with a per-face thickness (e.g. the BESO result).

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell model. |
| 1 | Thickness | `t` | Number | list | optional | Optional thickness per face [mm]. Default: the model's thickness. |
| 2 | Deform Scale | `DS` | Number | item | `10.0` | Displacement exaggeration for the preview mesh. |
| 3 | Colour Range | `CR` | Interval | item | optional | Fix the stress colour scale [MPa]. Empty = min-max of the result. |
| 4 | Legend | `Lg` | Boolean | item | `true` | Draw a colour legend in the viewport. |
| 5 | Layer Rule | `Rule` | Generic | item | optional | Optional Layer Rule: analyse the wall as weaker across its layers (κ = η·(t − h)/t), as Shell BESO does with the same rule. |
| 6 | Bond Efficiency | `η` | Number | item | `1.0` | With a Layer Rule: interface efficiency 0-1. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Compliance | `C` | Number | item | Compliance F·u [N·mm]. Lower = stiffer. |
| 1 | Max Displacement | `dMax` | Number | item | Largest displacement [mm]. |
| 2 | Displacements | `d` | Vector | list | Displacement per vertex [mm]. |
| 3 | Von Mises | `σ` | Number | list | Von Mises stress per face, worst of the two surfaces [MPa]. |
| 4 | Utilisation | `U` | Number | list | σ / strength per face (needs Strength on the material). |
| 5 | Preview | `P` | Mesh | item | Deformed mesh coloured by von Mises stress. |
| 6 | Info | `I` | Text | item | Summary. |
| 7 | Max Stress Point | `σMax` | Point | item | Centre of the face with the highest von Mises stress. |

### Interlayer Check

Nickname `Interlayer` · source `src/ClayBeso.GH/Components/InterlayerCheckComponent.cs`

Check the bond between printed layers. Stresses are split into along-layer and across-layer using the real (non-planar) layer directions. Each interface carries them only through its bonded strip, c = (w - h)/w of the wall (bead width w = wall thickness, h = layer height), so taller layers on the same wall bond over less.

Utilisation = worst of bond failure (tension + shear vs η·f) and bearing of the narrowed strip (compression vs f). Above 1 = expected to fail.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell model. |
| 1 | Thickness | `t` | Number | list | optional | Wall thickness per face [mm] (e.g. from Shell BESO). Default: the model's thickness. |
| 2 | **Layer Field** | `φ` | Number | list | required | Layer field per vertex, from the Layer Field output of Non-planar Layers. |
| 3 | Vertical | `Vz` | Boolean | item | `true` | Same setting as on Non-planar Layers (layer height measured in Z). |
| 4 | Strength | `f` | Number | item | `0.0` | Bulk clay strength [MPa]. 0 = use the Strength of the Clay Material. |
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

## 3 Optimise

### Shell BESO

Nickname `BESO` · source `src/ClayBeso.GH/Components/BesoComponent.cs`

Bi-directional evolutionary structural optimisation of a shell for minimum compliance (maximum stiffness), including self-weight.

Mode 0 = Thickness: graded wall thickness, no holes (always printable). By default it keeps the clay volume of the Shell Model thickness and only redistributes it.

Mode 1 = Void: classic BESO, removes faces to make perforations.

Set Run to true (Boolean Toggle) to iterate. Set Reset to true to start over.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell model. |
| 1 | Mode | `Mode` | Integer | item | `0` | 0 = Thickness (graded wall), 1 = Void (perforations). |
| 2 | Volume Fraction | `Vf` | Number | item | `0.0` | Thickness mode: 0 (default) = keep the clay volume of the Shell Model thickness and only redistribute it. A value > 0 sets a different target: average thickness = Vf × tMax (must be above tMin/tMax). Void mode: fraction of the full wall to keep (required). |
| 3 | Min Thickness | `tMin` | Number | item | `3.5` | Thickness mode: thinnest printable wall [mm] (about 1.1x nozzle diameter). |
| 4 | Max Thickness | `tMax` | Number | item | `7.0` | Thickness mode: thickest wall [mm] (widest bead you can extrude, about 2.3x nozzle). |
| 5 | Evolution Rate | `ER` | Number | item | `0.03` | Max relative volume change per iteration. |
| 6 | Filter Radius | `R` | Number | item | `10.0` | Sensitivity filter radius in document units. Use 2-3× the mesh edge length. |
| 7 | Max Iterations | `N` | Integer | item | `100` | Iteration limit. |
| 8 | Run | `Run` | Boolean | item | `false` | Iterate while true. |
| 9 | Reset | `Reset` | Boolean | item | `false` | Restart from the initial design. |
| 10 | Penalty | `p` | Number | item | `3.0` | Void mode: stiffness penalty exponent. |
| 11 | Admission Ratio | `AR` | Number | item | `0.02` | Void mode: max fraction of volume re-added per iteration. |
| 12 | Colour Range | `CR` | Interval | item | optional | Fix the thickness colour scale [mm]. Empty = tMin to tMax in thickness mode, min-max in void mode. |
| 13 | Legend | `Lg` | Boolean | item | `true` | Draw a colour legend in the viewport. |
| 14 | Playback | `Pb` | Number | item | `1.0` | Show the design at a point of the run: 0 = first iteration, 1 = final (default). Connect a 0-1 slider to scrub through the optimisation. Thickness, Solid and the preview follow it; it never restarts the run. |
| 15 | Layer Rule | `Rule` | Generic | item | optional | Optional Layer Rule (from the Layer Rule component). When connected, the wall is weaker across its layers: across-layer stiffness × κ = η·(t − h)/t, with h from the rule. Use the same rule on Layer Heights. |
| 16 | Bond Efficiency | `η` | Number | item | `1.0` | With a Layer Rule: interface efficiency 0-1 (calibrate from tests, see docs/VALIDATION.md §4). 1 = only the contact geometry weakens the wall. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Thickness | `t` | Number | list | Wall thickness per face [mm] (0 for removed faces in void mode). |
| 1 | Solid | `S` | Boolean | list | Per face: true if material is kept. |
| 2 | Sensitivity | `α` | Number | list | Filtered sensitivity per face (higher = more useful material). |
| 3 | Preview | `P` | Mesh | item | Mesh coloured by thickness (void faces hidden). |
| 4 | Compliance | `C` | Number | list | Compliance history [N·mm]. |
| 5 | Volume | `V` | Number | list | Volume-fraction history. |
| 6 | Iteration | `i` | Integer | item | Iterations done. |
| 7 | Converged | `Done` | Boolean | item | True once compliance has stabilised at the target volume. |
| 8 | Info | `I` | Text | item | Status. |

## 4 Print

### Layer Heights

Nickname `LayerH` · source `src/ClayBeso.GH/Components/LayerHeightComponent.cs`

Turn a wall-thickness field (e.g. from Shell BESO) into a target layer height per vertex.

Mapping 3 (default) = proportional: thinnest wall gets hMin, thickest gets hMax. More demand means a wider bead AND a taller layer, with the flow raised to supply both (Flow Factor output of Non-planar Layers).

Mapping 2 = constant aspect ratio h = k·t: also rises with thickness, same bead shape everywhere.

Mappings 0 and 1 are for fixed flow: 0 = constant bead area (thick walls by squashing the bead with a lower layer), 1 = linear inverse (thickest wall gets hMin).

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell model (for the mesh). |
| 1 | **Thickness** | `t` | Number | list | required | Wall thickness per face [mm]. |
| 2 | Min Layer Height | `hMin` | Number | item | `1.0` | Smallest layer height [mm]. About 0.4× nozzle diameter (0.6 mm for 1.5 mm, 1.2 mm for 3 mm). |
| 3 | Max Layer Height | `hMax` | Number | item | `2.5` | Largest stable layer height [mm]. About 0.8× nozzle diameter (1.2 mm for 1.5 mm, 2.4 mm for 3 mm). |
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

### Layer Rule

Nickname `Rule` · source `src/ClayBeso.GH/Components/LayerRuleComponent.cs`

How wall thickness turns into layer height, as one object shared by Layer Heights, Shell BESO and Shell Analysis. Sharing it means BESO optimises with the same layer heights you will print (needed for the across-layer stiffness).

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | Mapping | `Map` | Integer | item | `3` | 3 = proportional (thicker wall, taller layer), 2 = constant aspect ratio (h = k·t), 0 = constant bead area (fixed flow), 1 = linear inverse (fixed flow). |
| 1 | Min Layer Height | `hMin` | Number | item | `1.0` | Smallest layer height [mm]. |
| 2 | Max Layer Height | `hMax` | Number | item | `2.5` | Largest layer height [mm]. |
| 3 | Bead Area | `A` | Number | item | `0.0` | Bead cross-section [mm²] for mapping 0. 0 = automatic. |
| 4 | Aspect Ratio | `k` | Number | item | `0.4` | Layer height / bead width for mapping 2. |
| 5 | Thickness Range | `tR` | Interval | item | optional | Mappings 1 and 3: thickness [mm] mapped onto hMin..hMax. Empty = BESO's tMin..tMax inside BESO, or the field's own range elsewhere. Set it explicitly to make every component use the same mapping. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Layer Rule | `Rule` | Generic | item | Layer rule. |

### Non-planar Layers

Nickname `Layers` · source `src/ClayBeso.GH/Components/NonPlanarLayersComponent.cs`

Slice the shell into non-planar layer toolpaths whose spacing follows the target layer heights.

Method 0 = Conforming: layers fit both the base and the top rim, so every layer is a closed loop (heights matched as closely as possible). Check the error output.

Method 1 = Eikonal: exact local layer heights grown from the base. The top may end in partial layers.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell model (for the mesh). |
| 1 | **Layer Heights** | `h` | Number | list | required | Target layer height per vertex [mm] (from Layer Heights). |
| 2 | Vertex Thickness | `tv` | Number | list | optional | Optional bead width per vertex [mm], passed through per point. |
| 3 | Method | `Mth` | Integer | item | `0` | 0 = Conforming (closed layers, fits base and rim), 1 = Eikonal (exact heights). |
| 4 | Vertical | `Vz` | Boolean | item | `true` | Measure layer height vertically (Z) rather than along the wall. |
| 5 | Tolerance | `Tol` | Number | item | `0.5` | Band (document units) at the bottom/top used to find the base and the rim. |
| 6 | Seam | `Seam` | Vector | item | `(1, 0, 0)` | Closed layers start at the point furthest in this direction. |
| 7 | Nominal Area | `A0` | Number | item | `0.0` | Bead cross-section [mm²] that corresponds to 100% flow. 0 = median of the paths. |
| 8 | Colour Range | `CR` | Interval | item | optional | Fix the height-error colour scale [%], e.g. -20 to 20. Empty = symmetric about 0. |
| 9 | Legend | `Lg` | Boolean | item | `true` | Draw a colour legend in the viewport. |

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

### Wall Preview

Nickname `Wall` · source `src/ClayBeso.GH/Components/WallPreviewComponent.cs`

Thicken the mid-surface by the per-face thickness to preview the printed wall as a solid.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | **Model** | `Model` | Generic | item | required | Shell model. |
| 1 | Thickness | `t` | Number | list | optional | Wall thickness per face [mm]. Default: the model's thickness. |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | Wall | `W` | Mesh | item | Closed mesh of the thickened wall (document units). |

## 5 Help

### Help

Nickname `Help` · source `src/ClayBeso.GH/Components/HelpComponent.cs`

Opens the ClayBESO component reference (every input and output of every component) in your browser. Double-click this component, or connect a Button to Open.

**Inputs**

| # | Name | Nick | Type | Access | Default | Description |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | Open | `Open` | Boolean | item | `false` | Opens the component reference when this turns true (connect a Button). |

**Outputs**

| # | Name | Nick | Type | Access | Description |
| --- | --- | --- | --- | --- | --- |
| 0 | URL | `URL` | Text | item | Address of the component reference. |
