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
- Component defaults target a 3 mm nozzle (tMin 3.5, tMax 7, hMin 1.0, hMax 2.5 mm).
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

## Ground rules (do not break these)

1. **Units inside the core are N, mm, MPa, kg/m³.** Geometry is converted to mm on the way in
   (`RhinoConvert.ToTriMesh`, `ShellModel.UnitScale`) and back on the way out. Thicknesses and
   layer heights are always in mm, whatever the Rhino document units are.
2. **The core must not reference RhinoCommon or Grasshopper.** That is what makes it testable.
   Put geometry/maths in `ClayBeso.Core`, and only parameter wiring in `ClayBeso.GH`.
3. **Never change an existing `ComponentGuid`** or the `ClayBesoInfo.Id`. Doing so breaks every
   saved .gh file that uses the component. New components get a new random GUID.
4. **Do not reorder or remove inputs/outputs of existing components.** Saved definitions
   connect by index. Add new inputs at the end and make them `Optional`.
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

## Key design facts (read before editing the maths)

- Element stiffness is stored at **unit thickness**, split into membrane+drilling `Km` (scales
  with t) and bending `Kb` (scales with t³). `ElementScaling` holds the per-element factors.
  The global matrix pattern and Cholesky ordering are built once (`LinearSystem`) and only
  refactorised numerically each iteration.
- Self-weight is **design dependent** (thinner wall = lighter). The sensitivity therefore has a
  load term: `alpha = (dfm*u'Km u + dfb*u'Kb u - 2*dfw*u.w) / dV`. Elements can get negative
  sensitivity (they weigh more than they help).
- Thickness mode uses an optimality-criteria update with a volume schedule
  (`V_k+1 = max(V*, V_k(1-ER))`), move limit and history averaging. Void mode follows
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
6. Pass models between components as plain objects (generic parameter). Read them with
   `RhinoConvert.Unwrap<T>()`.
7. Build, run tests, list the component in `README.md`.

## Known limitations / roadmap (good next tasks)

- Linear elastic, small displacements, one load case, analysis of the finished geometry only.
  No layer-by-layer (staged) analysis of the print process, no buckling check. Wet-clay walls
  often fail by buckling, so a linear buckling analysis is a high-value addition.
- Better membrane element (ANDES OPT) for accuracy on coarse meshes.
- Sensitivity filter uses straight-line distance. A geodesic filter would avoid leaking across
  narrow gaps.
- Void mode can oscillate without converging, and holes interrupt continuous layers.
- Printing output stops at polylines + per-point heights/widths: no G-code, travel moves,
  extrusion calculation or overhang check yet. A closed bottom (floor) must be printed
  separately with planar layers.
