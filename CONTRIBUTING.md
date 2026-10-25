# Working on ClayBESO (with Claude Code)

This guide is for people who know Rhino and Grasshopper but are new to programming. You can
build on this plugin by describing what you want to an AI coding assistant (**Claude Code**)
and checking its work. The assistant reads `CLAUDE.md` / `AGENTS.md` automatically, which tell
it the project's rules.

## 1. One-time setup (≈ 20 minutes)

Install, in this order:

1. **Rhino 8**.
2. **.NET 8 SDK**: open PowerShell and run `winget install Microsoft.DotNet.SDK.8`.
3. **Git**: `winget install Git.Git`.
4. **Claude Code**: follow https://docs.claude.com/en/docs/claude-code (needs a Claude account).
5. Get the code: `git clone <repo-url>` (or unzip the folder you were given), then open a
   terminal in that folder and run `claude`.

Check that everything works by asking Claude:

> Build the project and run the tests, then tell me if everything passed.

You should hear back that all tests pass.

## 2. The everyday loop

1. **Describe the goal in Grasshopper terms.** For example:
   *"I want an input on Shell BESO that keeps the top 10 mm of the vase at full thickness."*
2. **Ask for a plan first** for anything non-trivial: *"Before changing code, explain your
   plan in simple terms."* Read it. Ask questions if something is unclear.
3. **Let it implement**, then make sure it **built and ran the tests**. Ask if it didn't.
4. **Install and try it**: ask *"Install the new build into Grasshopper"*. That runs
   `scripts/install.ps1`. Restart Rhino, open your test definition and check the result.
   The tests only check the maths. Only you can check that it behaves sensibly in Grasshopper.
5. **Save a checkpoint** when it works: *"Commit this with a short message."*
   If something goes wrong later, you can ask Claude to go back to this checkpoint.

## 3. Good requests vs. risky requests

Good requests (clear and testable):

- "Add an output to Print Layers with the total toolpath length per layer."
- "Let Shell Model accept support curves as well as points."
- "Add a stress-based optimisation criterion and a test that checks it."
- "Explain in simple terms what the Filter Radius does, and what happens if I double it."

Be careful with requests like these:

- "The stresses look too high, make them lower." That would only hide the problem. Ask
  *why* they are high instead.
- "Change the BESO formula." That's fine, but ask for a finite-difference sensitivity test (the
  existing `SensitivityMatchesFiniteDifference` test shows how).
- Renaming or reordering component inputs. It breaks saved .gh files. Add new inputs at the
  end instead.

## 4. Rules of the codebase (short version)

- All maths goes in `src/ClayBeso.Core` (no Rhino code there). This is what lets the tests run
  without Rhino.
- Grasshopper components in `src/ClayBeso.GH/Components` only read inputs, call the core and
  write outputs.
- Units inside: **mm, N, MPa**. Thickness and layer heights are always in mm.
- Never change a component's GUID.
- Every numerical change needs a passing test.
- If Karamba3D is installed in Rhino, ask Claude to "run the Karamba check" after changes to the
  solver or BESO. It compares our results with Karamba's (`tools/KarambaCheck/README.md`).

## 5. When something breaks

- **Red component in Grasshopper**: hover over it or right-click → *Runtime messages*, and copy
  the message to Claude.
- **Rhino doesn't show the new version**: Rhino must be fully restarted after installing.
- **Build errors**: paste the full error into Claude and ask it to fix and re-run the tests.
- **"dotnet not found"**: close and reopen the terminal after installing the SDK, or tell
  Claude to use `C:\Program Files\dotnet\dotnet.exe`.

## 6. Ideas for next steps

Ranked roughly by value for clay printing (details in `AGENTS.md` → *Known limitations*):

1. **Calibrate the buckling margin** with collapse-height prints (`docs/VALIDATION.md` §5).
2. **Clay ageing in print stages**: lower layers have stiffened by the time the top is printed.
3. **G-code export** from the layer polylines (Z per point, extrusion from width × height).
4. **Several load cases** (printing vs. handling) in analysis and BESO.
5. A more accurate membrane element (lets you use coarser meshes).
6. **Your own toolpath design** for walls thicker than one bead, for example back-and-forth or
   woven paths in the style of WeaveSlicer (Friedman-Gerlicz et al., CHI 2024). Use the BESO
   thickness and the Print Layers curves as inputs. This is open design territory: build it
   as your own Grasshopper definition first, and only move it into the plugin once it works.
