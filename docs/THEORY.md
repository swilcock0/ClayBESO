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

The bead width is the optimised wall thickness, `w = t`. The bead cross-section is modelled as a
stadium (rectangle with semicircular sides, as in Slic3r):
`A_b(w, h) = (w − h) h + π h²/4` for `w ≥ h`. The extrusion flow is `Q = A_b · v` for feed v.

**With flow control** (e.g. the WASP 40100) width and height are independent, and both can follow
the design. The flow supplies whatever bead they need:

- Mapping 3, *proportional* (default): `h = hMin + (hMax − hMin)·(t − t_lo)/(t_hi − t_lo)`, with
  `[t_lo, t_hi]` the optimiser's thickness range when given. A higher-demand region gets a wider
  **and** taller bead.
- Mapping 2, *constant aspect ratio*: `h = k t`, the same bead shape everywhere.

The slicer reports at every path point `A_b(w, h_realised)` and the flow factor
`A_b / A_nominal`. Because the realised height is used, any height error from the conforming
layer field is compensated by flow, and the wall width still equals the optimised thickness.

The clay per unit of wall area, which is what the shell model sees, is `t`. Layer height doesn't
change it. It sets the clay per pass and the number of layers. The structural analysis
currently ignores layer height (e.g. interlayer bond strength).

**Fixed flow** (Q and v constant, so `A_b` constant): the only way to get a wider bead is a lower
layer. Mapping 0 solves `A_b(t, h) = A` for h (thick walls come from squashing), and mapping 1 is
the linear version of the same idea.

All mappings are averaged to vertices, clamped to [hMin, hMax] and smoothed.

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

## 6. Interlayer check

**Bond model.** A stadium bead of width w and height h shares a flat strip of width w − h with the
next layer, so the bonded fraction of the wall is

```
c = clamp((w − h)/w, c_min, 1),     w = t (wall thickness),  h = realised layer height
```

A bond efficiency η ∈ (0, 1] scales the strength per unit contact area (calibrated from tests).

**Layer axes.** On each face, the stacking direction is `s = ∇φ/|∇φ|` (in the face plane), and the
along-layer direction is `a = n × s`. The realised layer height is `h = c_z/|∇φ|`, as in section 5.

**Interface stresses.** The membrane forces N and moments M (per unit length, local axes) give
`N_ss = sᵀNs`, `N_as = aᵀNs`, and likewise for M. Only the contact strip of width `c t` carries them:

```
σ_n = N_ss/(c t) ± 6 M_ss/(c t)²        τ = N_as/(c t) ± 6 M_as/(c t)²     (both faces)
```

**Utilisation** (worst over both faces):

```
U_bond    = sqrt(⟨σ_n⟩₊² + τ²) / (η f)
U_bearing = ⟨−σ_n⟩₊ / f
U         = max(U_bond, U_bearing)
```

Faces without a layer direction (e.g. a flat floor at φ = 0) are not checked.

Validation: on a self-weighted cylinder the bearing stress at mid-height matches the hand
calculation `γ (H − z)/c` to 1.5%. Bond utilisation scales with 1/η.

## 7. Printed layers in the stiffness (orthotropic wall)

**Material.** With layer axes a (along the bead) and s (across the layers), the plane-stress
matrix is split linearly in an across-layer factor κ:

```
Q(κ) = Q0 + κ·Q1,    Q1 = [[0, νE', 0], [νE', E', 0], [0, 0, G]]   (a, s axes),   E' = E/(1 − ν²)
```

κ = 1 gives the isotropic matrix exactly. κ < 1 scales the across-layer modulus, the Poisson
coupling and the shear; Q stays positive definite for κ > 0. Q1 is rotated into each element's
axes (standard Q̄ transformation), so element stiffness is

```
K_e = t·(Km + (κ − 1)·Km1) + t³·(Kb + (κ − 1)·Kb1)
```

with Km1 and Kb1 precomputed once per element (CST and DKT with Q1 and Q1/12). In BESO the
layer direction is horizontal (a = n × z); faces within ~3° of flat stay isotropic. The Interlayer
Check's orthotropic option uses the same matrices.

**Bond.** κ = η·c(t, h) with c = (t − h)/t, the bond model of section 6. Inside BESO, h = h(t) from
the shared Layer Rule (automatic thickness range = tMin..tMax). The Interlayer Check uses the
realised heights from φ.

**Sensitivity** (thickness mode). With κ = κ(t):

```
dK/dt = (Km + (κ − 1)Km1) + 3t²(Kb + (κ − 1)Kb1) + κ'(t)·(t·Km1 + t³·Kb1)
```

so α gains `κ'(t)·(t·u'Km1u + t³·u'Kb1u)/A`, with κ' by central difference. Checked against
finite differences for the proportional and constant-bead-area mappings, and in void mode (κ
constant).

Validation: for an axially loaded cylinder with ν = 0, compliance scales exactly with 1/κ
(κ = 0.5 → 2.00000, κ = 0.25 → 4.00000).

## References

- Batoz, Bathe, Ho (1980). A study of three-node triangular plate bending elements. IJNME 15.
- Huang, Xie (2007). Convergent and mesh-independent solutions for the BESO method. FEAD 43.
- Huang, Xie (2010). *Evolutionary Topology Optimization of Continuum Structures*. Wiley.
- Huang, Xie (2011). ETO of continuum structures including design-dependent self-weight loads. FEAD 47.
- Kimmel, Sethian (1998). Computing geodesic paths on manifolds. PNAS 95.
- Scordelis, Lo (1964); MacNeal, Harder (1985) — standard shell benchmarks.
