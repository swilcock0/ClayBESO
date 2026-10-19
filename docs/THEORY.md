# Theory notes

Units throughout: N, mm, MPa.

## 1. Shell element

Each triangle is a flat facet with 6 DOFs per node (ux, uy, uz, rx, ry, rz) in global axes.
In the element's local frame (e1 along edge 0→1, n the facet normal, e2 = n × e1):

- **Membrane**: constant-strain triangle (CST). `Km = A Bmᵀ Dm Bm`, with `Dm = E t/(1−ν²) [...]`.
- **Bending**: Discrete Kirchhoff Triangle (DKT), Batoz, Bathe & Ho (1980), 3-point Gauss rule,
  `Db = E t³ / (12(1−ν²)) [...]`. Local DOFs (w, rx, ry) with rx = ∂w/∂y, ry = −∂w/∂x.
- **Drilling**: a weak spring on rz, `k = 10⁻³ E t A`, with zero energy for a rigid rotation.
  It only removes the singularity of coplanar facets.

Global stiffness `K = Tᵀ K_local T`. Matrices are stored at unit thickness, so for a wall of
thickness t: `K_e(t) = t·Km + t³·Kb`.

Stresses: `σ = N/t ± 6M/t²` on the two faces; von Mises is the larger of the two.

Validation (`tests/`): rigid-body modes have zero energy; constant strain and curvature are
reproduced exactly; a cantilever plate strip is within 0.01 % of beam theory; the Scordelis-Lo
roof (reference 0.3024) gives 0.265 / 0.296 / 0.299 at 16² / 48² / 96² meshes.

## 2. Loads

- Point loads at the nearest vertex.
- Self-weight `γ = ρ g` per volume: each element contributes `γ t A / 3` to each of its nodes,
  along gravity. It **depends on the design** (thinner = lighter).

## 3. BESO

Objective: minimise compliance `C = Fᵀu` at a target volume `V*`.

Design-dependent load sensitivity (for any scaling `K_e = fm(x) Km + fb(x) Kb`,
`f_e = fw(x) w_e`):

```
dC/dx_e = 2 u_eᵀ (dfw/dx) w_e − u_eᵀ (dfm/dx Km + dfb/dx Kb) u_e
α_e = −(dC/dx_e) / (dV_e/dx_e)              (sensitivity number per unit volume)
```

- **Thickness mode** (x = t): fm = t, fb = t³, fw = t, dV/dt = A. Update by optimality criteria
  `t ← clamp(t (α⁺/λ)^η)` with move limit, and bisection on λ to hit this iteration's volume.
- **Void mode** (x ∈ {x_min, 1}): fm = xᵖ t, fb = xᵖ t³, fw = x t (soft-kill, Huang & Xie 2010).
  Elements are ranked by α and the top ones are kept until the volume is reached, with at most
  `AR·V_ref` re-admitted per iteration.

By default thickness mode keeps the starting volume (`V* = Σ A t₀`) and only redistributes it.

**Negative sensitivities (self-weight).** The OC factor `(α/λ)^η` can only shrink faces with
α ≤ 0. Under self-weight many faces have α < 0. When the faces with α > 0 cannot hold `V*` even at
their upper bounds, the update shifts every α by a constant (`α ← α − α_min + 0.05(α_max − α_min)`)
before the bisection. A constant shift leaves the optimality condition unchanged (α equal on all
faces strictly between the bounds, with a free-sign multiplier for the volume equality) and
restores the volume. Without it, the update lost about 19% of the clay on fine meshes and never
converged.

Both modes:
- volume schedule `V_{k+1} = max(V*, V_k (1 − ER))`,
- linear-hat filter over element centroids with radius r (mesh independence),
- history averaging `α_k ← (α_k + α_{k−1})/2`, on in void mode only (it damps the discrete
  oscillation but slows the continuous thickness update),
- convergence when the volume is at the target and
  `|Σ_{i<N} C_{k−i} − Σ_{i<N} C_{k−N−i}| / Σ C_{k−i} ≤ τ`, with N = 3 (thickness) or 5 (void).
- thickness mode move limit 0.3 (tMax − tMin) per iteration.

These defaults bring typical thickness runs from ~21 to ~10 iterations with the same compliance.

**Reanalysis.** Factorising K takes about 98% of an iteration. After the first iteration, the
previous Cholesky factor preconditions a CG solve of the new K (warm start from the last
displacements, relative residual 1e-10). The matrix is refactorised only when CG becomes as
expensive as a factorisation. Results match direct solves to ~1e-11.

The analytic sensitivities are checked against central finite differences in the tests.

## 4. Layer heights from thickness

With constant extrusion flow Q and feed v, the bead cross-section `A_b = Q/v` is constant.
A squashed bead of width w and height h has `w·h ≈ A_b`. Setting the bead width equal to the
optimised wall thickness gives:

```
h(x) = clamp(A_b / t(x), hMin, hMax)
```

So you get a variable wall thickness **without changing flow or speed**, only the layer height.
The field is averaged to vertices and smoothed.

The bead is modelled as a stadium (rectangle with semicircular sides, as in Slic3r):
`A_b(w, h) = (w − h) h + π h²/4` for `w ≥ h`. Mapping 0 solves `A_b(t, h) = A` for h.

**With flow control** (e.g. the WASP 40100) width and height are independent. Mapping 2 sets
`h = k t` (constant squash ratio), and the slicer reports at every path point
`A_b(w, h_realised)` and the flow factor `A_b / A_nominal`. Because the realised height is used,
any height error from the conforming layer field is compensated by flow, and the wall width
still equals the optimised thickness.

## 5. Non-planar layers

Layer k is the iso-curve `φ = k` of a scalar field φ on the mid-surface. The local layer
spacing is `1/|∇φ|`, so the goal is `|∇φ| = s(x)` with slowness `s = c(x)/h(x)`. Here
`c = |∇_s z| = √(1 − n_z²)` when heights are measured vertically, and `c = 1` when they are
measured along the wall.

- **Eikonal**: solve `|∇φ| = s` with φ = 0 at the base by fast marching (Kimmel & Sethian
  1998 triangle update, edge fallback). The local spacing is exact. The top rim is generally not
  a level set, so the last layers can be partial.
- **Conforming**:
  1. harmonic ψ with ψ = 0 at the base and ψ = 1 at the rim gives the stacking direction
     `d = ∇ψ/|∇ψ|`,
  2. least-squares fit `min ∫ |∇φ − s d|²` with φ = 0 at the base gives the natural layer
     count N ≈ mean φ on the rim,
  3. the same fit with φ = round(N) pinned on the rim.

  Every layer is a closed loop and the rim is the last layer. When the targets vary around the
  circumference, no flat-rim layering can match them exactly. The error is shown per face.

Iso-curves are extracted per triangle and chained through shared edges. Closed loops are
oriented counter-clockwise about +Z and start at the seam direction.

## References

- Batoz, Bathe, Ho (1980). A study of three-node triangular plate bending elements. IJNME 15.
- Huang, Xie (2007). Convergent and mesh-independent solutions for the BESO method. FEAD 43.
- Huang, Xie (2010). *Evolutionary Topology Optimization of Continuum Structures*. Wiley.
- Huang, Xie (2011). ETO of continuum structures including design-dependent self-weight loads. FEAD 47.
- Kimmel, Sethian (1998). Computing geodesic paths on manifolds. PNAS 95.
- Scordelis, Lo (1964); MacNeal, Harder (1985) — standard shell benchmarks.
