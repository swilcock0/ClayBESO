# KarambaCheck — cross-check against Karamba3D

A developer tool that runs the same shells through the ClayBeso core and through
[Karamba3D](https://karamba3d.com) and prints the results side by side. Use it after changing the
elements, the solver, buckling or BESO, next to the unit tests.

It is **not** part of `ClayBeso.sln`, so the normal build and tests never need Karamba, and nothing
from Karamba ships with the plugin.

## Requirements

- Karamba3D Community 3.1 installed in Rhino 8 through the Package Manager. It is free (MIT licence).
  The tool expects it in
  `%APPDATA%\McNeel\Rhinoceros\packages\8.0\Karamba3D Community\3.1.60921\net8.0-windows`;
  for another version or folder add `-p:KarambaDir="<folder with KarambaCommon.dll>"` to the build.
- The .NET 8 SDK (as for the rest of the repo). Rhino does not need to be running.

## Running

```bash
dotnet build tools/KarambaCheck -c Release
dotnet tools/KarambaCheck/bin/Release/net8.0-windows/KarambaCheck.dll                 # ~3 min
dotnet tools/KarambaCheck/bin/Release/net8.0-windows/KarambaCheck.dll static buckling # ~20 s
```

Groups: `static`, `buckling`, `thickness`, `void` (all four by default) and `convergence`
(mesh study of the example ellipse vase, slow, only when asked). Each compared line ends in `ok`
or `CHECK`, and the exit code is the number of `CHECK`s.

## What is compared

| Group | Cases | Tolerance |
|---|---|---|
| static | cantilever strip (beam theory), Scordelis-Lo roof 16² and 48², clay vase under self-weight and a rim load, ellipse vase | 1 % (3 % roof, 5 % ellipse) on compliance |
| buckling | Euler column, Greenhill (self-weight), axially loaded cylinder, vases under self-weight | 1–5 % on the first load factor |
| thickness | our graded wall re-analysed in Karamba: stiffness gain over a uniform wall with the same clay | 2 % (5 % ellipse) |
| void | our void mode vs Karamba's *BESO for Shells* at 50 %, both designs analysed by both solvers | ours no more than 10 % worse |

Karamba's shell element (TRIC) differs from ours (CST + DKT), so small differences are expected.
On strongly curved, coarse meshes Karamba is stiffer and converges towards our result as the mesh
is refined (run `convergence`).

## Results (October 2026, Karamba 3.1.60921)

- Static: within 0.2 % on the vases, 1.2–1.4 % on the roof, exact on the cantilever.
- Buckling: within 0.0–1.2 %, 3.4 % on the ellipse vase.
- Thickness mode: same stiffness gain in both solvers (e.g. 0.623 vs 0.623).
- Void mode: this check found two bugs with self-weight (removed faces 10⁶× floppier than their
  weight, and a ranking that cut the load path). Our design was ~40× worse than Karamba's; after the
  fix it is within 7 %, and with only a rim load 15 % better. It also found that void mode spent
  ~80 % of its time in failed reanalysis solves; it is now 4–7× faster, within about 1.5× of Karamba.

## Notes for changing the tool

- Karamba's raw API works in kN, m, kN/m²; `KarambaModel` converts from ClayBeso's N, mm, MPa.
- Karamba renumbers nodes, so supports, loads and results go by position, not index.
- Karamba reports elastic energy, which is half the compliance.
- Buckling needs `Karamba.Algorithms.Analyze` (not `AnalyzeThI`) first, for the normal forces.
- Karamba's BESO marks removed faces by shrinking their thickness (soft-kill factor).
