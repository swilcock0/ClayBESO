# AGENTS.md — instructions for AI coding assistants

This file is read automatically by AI coding assistants (Claude Code reads it through
`CLAUDE.md`; Codex, Cursor and others read it directly). Humans should start with
`README.md` and `CONTRIBUTING.md`.

## Who you are working with

The people working on this repo are **architecture / design researchers, not professional
programmers**. Assume they know Rhino, Grasshopper and clay printing well, and C#, git and
numerical methods less well. So:

- Explain what you changed and why in plain language, in terms of what they will see in
  Grasshopper. Avoid jargon or explain it in one line.
- Before a large change, describe the plan in a few bullet points and ask them to confirm.
- After every change, **build and run the tests**, then tell them exactly how to try it in
  Grasshopper (which component, which inputs).
- Never claim something works in Rhino/Grasshopper unless they have tried it. You can only
  verify the core library with tests. Say so.
- If a request would make the physics wrong or misleading (e.g. "just make the stresses
  smaller"), explain the problem instead of doing it.

## What this project is

A Grasshopper plugin (Rhino 8) that:

1. Analyses a thin shell (e.g. a vase) with a built-in finite-element solver
   (flat triangular shell: CST membrane + DKT bending, 6 DOF per node).
2. Optimises it with BESO (bi-directional evolutionary structural optimisation) for minimum
   compliance under self-weight and/or point loads:
   - **Thickness mode**: graded wall thickness in [tMin, tMax], no holes.
   - **Void mode**: classic soft-kill BESO that removes faces.
3. Converts the wall thickness into a **variable, non-planar layer-height field** for clay 3D
   printing and slices it into layer toolpaths (polylines on the mid-surface).

## Printing context

- Printer: **Delta WASP 40100 Clay** (delta, build volume Ø400 × 1000 mm), nozzles 1.5–3 mm.
- **Flow (extrusion rate) can be changed dynamically along the path**, as can Z. So bead width
  and layer height are independent: `bead area = f(width, height)` and
  `flow = bead area × feed`. The Non-planar Layers component outputs per point the bead area
  (stadium model, `Printing/Bead.cs`) and a flow factor relative to a nominal area. Both use
  the *realised* layer height.
- **Design intent (from the user): both bead width and layer height assign material, and flux
  is raised to allow taller layers.** The default layer mapping is therefore *proportional*
  (thicker wall → taller layer). Don't frame thick walls as coming from squashing: the
  squash-based mappings 0 and 1 exist only for fixed-flow printers.
- Component defaults target a 3 mm nozzle (tMin 3.5, tMax 7, hMin 1.0, hMax 2.25 mm). Layer height stays within
  25–75 % of the nozzle diameter (Vele, Kurilla & Achten, eCAADe 2023).
- G-code export is not implemented. Before writing it, ask the user for a sample G-code file
  from their current WASP workflow to match the header, E-axis convention (absolute/relative,
  volumetric or not) and flow commands. Don't guess firmware specifics.

## Repository layout

```
src/ClayBeso.Core/        Rhino-free library (netstandard2.0). ALL maths lives here.
  Geometry/               Vec3, TriMesh (indexed triangle mesh + adjacency)
  Fem/                    ShellElement, ShellModel, LinearSystem (sparse Cholesky), ShellSolver
  Optimisation/           SensitivityFilter, BesoOptimizer (+ BesoSettings, BesoMode)
  Printing/               LayerHeightField, LayerField (conforming / eikonal),
                          FastMarching, MeshOperators, LayerSlicer
src/ClayBeso.GH/          Grasshopper plugin (.gha). Thin wrappers only, no maths.
  Common/                 RhinoConvert (units + mesh conversion), Preview (colours, thicken)
  Components/             one file per Grasshopper component
tests/ClayBeso.Tests/     xUnit tests of the core (benchmarks, sensitivities, layers, pipeline)
docs/THEORY.md            the maths, with references
docs/VALIDATION.md        the experimental validation plan (tests, pass criteria, which plugin input each sets)
docs/COMPONENTS.md        every component input/output, GENERATED from the sources (never edit by hand)
```

## Commands

The .NET 8 SDK is required. On the original machine `dotnet` may not be on the PATH of a
fresh shell. If `dotnet` is not found, use the full path `"C:\Program Files\dotnet\dotnet.exe"`
(or in Git Bash: `export PATH="/c/Program Files/dotnet:$PATH"`).

```bash
dotnet build ClayBeso.sln -c Release      # builds core, plugin (net48 + net7.0-windows) and tests
dotnet test tests/ClayBeso.Tests          # must stay green (all tests pass)
dotnet test tests/ClayBeso.Tests --logger "console;verbosity=detailed"   # shows benchmark numbers
```

Built plugin: `src/ClayBeso.GH/bin/Release/net7.0-windows/` (Rhino 8 default runtime) and
`.../net48/` (Rhino 8 started with the .NET Framework runtime). `scripts/install.ps1` copies it
into the Grasshopper Libraries folder. Rhino must be restarted to load a new build.

## Releasing (only when the user asks)

1. Bump `<Version>` in `src/ClayBeso.GH/ClayBeso.GH.csproj` (semantic versioning: 0.x.y). It is the only
   place: the plugin info and the Help component read it from the built assembly.
2. Build and test, then run `scripts/package.ps1`. It writes `artifacts/claybeso-<version>-rh8_0-win.yak`.
3. Commit, tag `v<version>`, push the commit and the tag, then
   `gh release create v<version> artifacts/claybeso-<version>-rh8_0-win.yak --title ... --notes ...`.
4. Don't `yak push` to the public McNeel package server unless the user explicitly asks.

## Ground rules (do not break these)

1. **Units inside the core are N, mm, MPa, kg/m³.** Geometry is converted to mm on the way in
   (`RhinoConvert.ToTriMesh`, `ShellModel.UnitScale`) and back on the way out. Thicknesses and
   layer heights are always in mm, whatever the Rhino document units are.
2. **The core must not reference RhinoCommon or Grasshopper.** That is what makes it testable.
   Put geometry/maths in `ClayBeso.Core`, and only parameter wiring in `ClayBeso.GH`.
3. **Never change an existing `ComponentGuid`** or the `ClayBesoInfo.Id`. Doing so breaks every
   saved .gh file that uses the component. New components get a new random GUID.
4. **Saved definitions connect wires by position, so never reorder or remove the inputs/outputs of a
   released component in place.** The user wants the most important inputs/outputs at the top with
   full-word nicknames, so when an order needs to change:
   - give the component the new order and a **new GUID**;
   - keep the old order as a hidden legacy subclass with the **old GUID** in `<Name>V1.cs`
     (see `BesoComponentV1.cs`, `ShellAnalysisComponentV1.cs`), sharing all the code;
   - read inputs and write outputs **by name** (`In("Name")`, `Out("Name")` on `ClayComponent`),
     never by index, so both versions work;
   - add the pair to `ComponentDocsTests.LegacyVersionsHaveTheSameParameterNames`.

   Adding an input or output at the end of an existing component, marked `Optional`, is still fine
   without a new version. Unreleased components can be reordered freely.
5. **Numerical changes need a test.** Any change to elements, the solver, sensitivities or the
   layer field must keep the existing benchmarks passing and add a test for the new behaviour.
   The key safety nets:
   - `ElementTests`: rigid-body modes have zero energy; constant strain/curvature are exact.
   - `SolverBenchmarks`: cantilever plate vs beam theory; Scordelis-Lo roof (ref. 0.3024).
   - `BesoTests.SensitivityMatchesFiniteDifference`: analytic sensitivities vs finite
     differences. If you change how stiffness or self-weight scale with the design variable,
     update `BesoOptimizer.RawSensitivity` and this test must still pass.
   - `LayerTests`: uniform heights give planar layers; graded heights are reproduced.
6. Keep `netstandard2.0` compatible C# in the core (no `PriorityQueue`, no `Math.Clamp`,
   no `System.Range` / `^1` indexing). The GH project targets net48 as well.
7. The only third-party dependency is **CSparse** (sparse Cholesky, MIT). Ask before adding
   others. Rhino packages are references only (`ExcludeAssets="runtime"`).
8. **No AI attribution in git history.** Commit messages and pull request descriptions must
   not contain `Co-Authored-By:` trailers for AI assistants, or "Generated with …" lines.

## Component structure (read before adding or rewiring components)

The user wants **one straight chain with as few wires as possible**:

```
Shell Model -> Shell BESO --Design--> Print Layers --Print--> Print Check
                  ^ Layer Rule (optional)
```

- `Fem/ShellDesign` = model + per-face thickness + resolved `LayerRule` + `BondModel`. Shell BESO
  outputs one ("Design"); Print Layers also accepts a plain Shell Model.
- `Printing/PrintPlanner.Plan(design, settings, rule?)` does layer heights, layer field (all three
  methods; force-aligned runs its own analysis for the force flow), slicing, overhang check and flow
  factors, returning a `PrintPlan` (the "Print" output).
- `Printing/PrintChecker.Check(plan, settings)` does the finished-piece analysis + interlayer check
  and the print stages, returning a `PrintCheckResult`.
- Don't reintroduce designs where the user has to wire the same data twice or back up the canvas
  (e.g. Force Flow from an analysis into the slicer). Compute it inside, or carry it in Design/Print.
- **Every mode/option input gets an automatic dropdown.** Derive components from
  `Common/ClayComponent` (or `LegendComponent`, which derives from it) and declare
  `protected override IEnumerable<Dropdown> Dropdowns`. Shared option lists live in `Common/Modes`.
  Dropdowns are only created when a component is newly placed, never on file load or paste.
- Replaced components (Layer Heights, Non-planar Layers, Interlayer Check, Print Stages) are
  `GH_Exposure.hidden`: kept so older .gh files open, not on the toolbar. Never delete them.

## Key design facts (read before editing the maths)

- Element stiffness is stored at **unit thickness**, split into membrane+drilling `Km` (scales
  with t) and bending `Kb` (scales with t³). `ElementScaling` holds the per-element factors.
  The global matrix pattern and Cholesky ordering are built once (`LinearSystem`) and only
  refactorised numerically each iteration.
- Self-weight is **design dependent** (thinner wall = lighter). The sensitivity therefore has a
  load term: `alpha = (dfm*u'Km u + dfb*u'Kb u - 2*dfw*u.w) / dV`. Elements can get negative
  sensitivity (they weigh more than they help).
- Thickness mode uses an optimality-criteria update with a volume schedule
  (`V_k+1 = max(V*, V_k(1-ER))`) and a move limit (0.3·span). By default it keeps the starting
  volume. When positive-sensitivity faces can't hold the target volume (self-weight), the
  sensitivities are shifted by a constant. Don't go back to clipping at zero: that silently loses
  clay (regression test `SelfWeightOnFineMeshKeepsTheClayAndConverges`).
- Thickness mode filters the **thickness itself** (`BesoSettings.ThicknessFilter`, default on):
  design variables x, wall thickness `t = W x`, sensitivities through Wᵀ (`DesignSensitivity`).
  `Design`/`DesignHistory` hold x; use `EffectiveThickness(design)` or `ScalingFor(design)` to get the
  wall, never x directly. `ThicknessFilterTests` checks the chain rule by finite differences.
- `LinearSystem` reuses the previous Cholesky factor as a CG preconditioner (reanalysis) and only
  refactorises when CG gets slow. `ReusedFactorisationGivesTheSameDesignAsDirectSolves` checks it. Void mode follows
  Huang & Xie (2007/2010) with an admission-ratio cap.
- Layer field `phi`: layer k is the iso-curve `phi = k`. The conforming method solves two
  Poisson problems (harmonic stacking direction, then least-squares fit of
  `grad phi = direction / h`) with phi pinned to 0 at the base and an integer at the rim.
  The eikonal method is fast marching with slowness `1/h`.
- "Vertical" layer height uses the factor `|grad_s z| = sqrt(1 - n_z^2)` to convert spacing
  along the wall to spacing in Z.
- The CST membrane converges slowly on curved shells (Scordelis-Lo: ~12% low at 16×16, ~2%
  at 48×48). Users should use fine meshes. Upgrading to a membrane with drilling DOFs
  (Felippa's ANDES "OPT" triangle) is on the roadmap and must pass the same tests.

## Adding a Grasshopper component (checklist)

1. Put any new maths in `ClayBeso.Core` with a test.
2. Create `src/ClayBeso.GH/Components/<Name>Component.cs`, copying the structure of an
   existing one (e.g. `WallPreviewComponent.cs` is the simplest).
3. New GUID (`[guid]::NewGuid()` in PowerShell), category `Tab.Name`, a sub-tab from `Tab`.
4. Give every input/output a description that states its **units**.
5. Catch exceptions and report them with `AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ...)`.
   Never let an exception escape `SolveInstance`.
6. Derive from `Common/ClayComponent` and declare a `Dropdown` for every mode/option input. If the
   component outputs a false-colour preview mesh, derive from `Common/LegendComponent` instead
   instead of `GH_Component`. Add *Colour Range* (interval) and *Legend* (bool) inputs at the
   end, colour with `Preview.FaceColoured/VertexColoured(..., out used, range)`, then call
   `SetLegend(title, unit, used, show)` so the legend matches the mesh.
7. Pass models between components as plain objects (generic parameter). Read them with
   `RhinoConvert.Unwrap<T>()`.
8. Regenerate the component reference: `UPDATE_DOCS=1 dotnet test tests/ClayBeso.Tests --filter ComponentDocs`
   (PowerShell: `$env:UPDATE_DOCS=1; dotnet test ...`). It is built from the input/output descriptions in
   code, and the test suite fails while `docs/COMPONENTS.md` is out of date, so write good descriptions.
9. Build, run tests, list the component in `README.md`.

## Known limitations / roadmap (good next tasks)

- **Layer height in the structure (done, both steps).** `BondModel` (contact c = (w − h)/w,
  efficiency η) drives (1) `Fem/InterlayerCheck.cs`, a post-analysis check, and (2) the orthotropic
  wall: `ShellElement.EnableLayerAxes` adds across-layer parts Km1/Kb1, `ElementScaling.Across` = κ,
  and `BesoSettings.LayerRule` + `Bond` make BESO use κ = η·c(t, h(t)). The shared `Printing/LayerRule`
  is the single source of h(t). Keep κ = 1 bit-for-bit isotropic (`LayeredModelWithKappaOneMatchesIsotropic`)
  and the layered sensitivities checked by finite differences (`LayeredSensitivityMatchesFiniteDifference`).
  Open: real (non-planar) layer directions inside BESO (currently horizontal), and calibrating η (docs/VALIDATION.md §4).

- **Overhangs (done):** `Printing/OverhangCheck` (lean, bead offset, ratio, tilt per path point),
  `Fem/ForceFlow` (principal force direction, interface angle in `InterlayerCheck`),
  `LayerMethod.ForceAligned` (stacking direction turned towards the force flow, limited by a max
  deviation), and `Fem/PrintStages` (analysis of each partly printed stage). Source: Vele, Kurilla &
  Achten 2023 (PDFs are in the git-ignored `Amir/` folder). Open: clay ageing in stages (younger
  layers softer), and default limits calibrated by docs/VALIDATION.md §7.
  Force-aligned turning stretches layers; `LayerField.Compute(maxHeight:)` (Print Layers passes the
  rule's hMax) reduces the turn so layers stay within max(hMax, conforming height). Keep
  `ForceAlignedLayersStayWithinTheMaxLayerHeight` (the example vase) passing.
- **Linear buckling (done):** `Fem/Buckling.cs` (geometric stiffness from the membrane forces,
  restarted Lanczos for the smallest positive load factor). Print Check runs it for the finished
  piece and every stage (`PrintStageOptions.Buckling`), Shell Analysis on request. Keep the
  `BucklingTests` benchmarks (Euler, Greenhill, classical cylinder) passing. Open: second-order
  (P-delta) analysis, which can reuse `Buckling.GeometricBlocks`; calibrating the warning margin
  (`BucklingText.Margin` = 3, a placeholder) with docs/VALIDATION.md §5.
- Linear elastic, small displacements, one load case (several load cases are a good next task).
- Better membrane element (ANDES OPT) for accuracy on coarse meshes.
- Shell BESO's Filter Radius 0 (default) means automatic: `SensitivityFilter.AutoRadius` = 3× the
  average mesh edge. In the core, `BesoSettings.FilterRadius` 0 still means no filter.
- Sensitivity filter uses straight-line distance. A geodesic filter would avoid leaking across
  narrow gaps.
- Void mode can oscillate without converging, and holes interrupt continuous layers.
- Printing output stops at polylines + per-point heights/widths: no G-code, travel moves,
  extrusion calculation or overhang check yet. A closed bottom (floor) must be printed
  separately with planar layers.
- **Toolpath design (e.g. woven / oscillating paths for walls wider than one bead) is
  deliberately left to the student.** The user decided the plugin should provide the thickness
  field and layer curves, not generate such paths. Don't add a weaving/toolpath generator unless
  the user or student asks for one. Help them build it in Grasshopper instead.
