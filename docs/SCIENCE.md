# From the Scientific Corpus to Code

This document maps each layer of the corpus
(`Scientific Corpus — Biomechanics for an Ultra-Realistic Ragdoll.md`) to its implementation
in Brutality 1.0.0. Numerical values are adult male orders of magnitude, subject to
recalibration after in-game testing.

## 1. Mass and Inertia — de Leva (1996)

`Anatomy/AnatomyData.cs` → `Inertia`, applied in `Core/BodyModel.cs` → `ApplyAnthropometry`.

| Brutality Segment | Composition | Mass Fraction | CoM (from proximal) | Radii of Gyration (sag / trans / long) |
|---|---|---|---|---|
| Pelvis | lower trunk + ½ mid trunk | 19.34% | auto (colliders) | auto |
| Thorax | upper trunk + ½ mid trunk (+ gear) | 24.12% | auto, shifted by backpack & armor plates | auto |
| Head | head-neck (+ helmet) | 6.94% | center of cranial colliders | auto |
| Upper Arm | upper arm | 2.71% | 57.72% | 28.5 / 26.9 / 15.8% |
| Forearm | forearm + hand | 2.23% | 68% | 44 / 43 / 12% |
| Thigh | thigh | 14.16% | 40.95% | 32.9 / 32.9 / 14.9% |
| Calf | calf + foot | 5.70% | 60% | 36 / 36 / 11% |

The EFT ragdoll has no rigidbodies for hands and feet. For merged segments, the
center of mass and radii of gyration are recomputed via the parallel-axis theorem from
de Leva's values (hand 0.61%, foot 1.37%). The inertia tensor is aligned to the
actual longitudinal axis of the bone measured on the skeleton at death
(`inertiaTensorRotation`). Body mass variability follows a normal distribution, in the
spirit of ANSUR II.

## 2. Joints and Coordinate Frames — ISB (Wu 2002, 2005), Grood & Suntay

`Core/JointKinematics.cs`, tested off-game in `tests/Brutality.KinematicsTests`.

Each segment carries an anatomical frame (R right, U proximal/superior, F anterior), built
from joint centers: hip axis, shoulder axis, bone-to-bone direction.

Angles use ISB-style decompositions:

- **Descending ball-and-socket joints** (hip, shoulder): distal direction of child in parent's
  frame. Flexion is the angle in the sagittal plane, abduction the out-of-plane angle
  (left/right mirrored). Axial rotation is obtained via *swing-twist* decomposition.
- **Ascending ball-and-socket joints** (spine, neck): same principle on the child's superior axis;
  axial rotation is absolute, with an offset measured at death.
- **Hinges** (knee, elbow): the flexion axis is derived from the plane of the two segments,
  oriented so flexion is positive (the knee brings the calf backward, the elbow brings the
  forearm forward). Varus/valgus and pronation-supination are added.

The ±180° discontinuity is placed opposite the physiological zone: a shoulder at 190° is still
read as 190°.

Torque for each degree of freedom is applied along the **numerical angular gradient**
(∂φ/∂ω). This makes the model insensitive to sign conventions and conserves angular
momentum, since each torque is applied as +τ to the child and −τ to the parent.

## 3. Ranges of Motion and Passive Resistance

`Anatomy/AnatomyData.cs` → `BuildSpecs`, `Core/DriveController.cs`.

- Ranges of motion: NASA Anthropometric Source Book (vol. I), Norkin & White, Panjabi & White for the
  thoracolumbar and cervical spine.
- Passive torque: τp = A·e^(−B(φ−φmin)) − A·e^(−B(φmax−φ)) + K0(φneutral − φ), double-exponential
  form from Yoon & Mansour (1982), Riener & Edrich (1999), and Hoang et al. (2005).
  Per the corpus (§10), there is **no hard stop** inside the physiological zone.
- Biarticular coupling (corpus §10, Riener & Edrich): passive hamstring elements
  (stretched by hip flexion + knee extension, 70° threshold) and rectus femoris (hip extension +
  knee flexion). Their tension acts on both joints with relative lever arms 1 / 0.75 and 0.4 / 0.6.
- Limits are enforced by PhysX `ConfigurableJoint`s whose reference pose is the death pose.
  The flexion limit is exact: [min − φ0, max − φ0], in the direction measured in-game by a PhysX
  probe; abduction and rotation (cone) limits cover the range around φ0. They are soft: stiffness
  6·A·B (derivative of the exponential torque at the limit), damping 6% of stiffness. EFT's
  `CharacterJoint`s, whose limits are relative to the death pose, are removed.
- Passive resting tone and biarticular coupling are restoring terms added to drive targets.

## 4. Muscle Force — Anderson et al. (2007), Hill (1938)

`Core/DriveController.cs` → `MuscleTorque`.

Each active degree of freedom has two antagonistic groups (TPos / TNeg). Maximum torque is:

T = a · Tmax · f(φ) · g(φ̇)

- **f**: bell-shaped torque-angle relation centered at mid-range.
- **g**: Hill force-velocity relation. Concentric: g = (1 − v/vmax)/(1 + v/(0.25·vmax)),
  with curvature a/F0 = 0.25. Eccentric: g plateaus around 1.8.
- **a**: activation, first-order (Zajac 1989, Thelen 2003), τ = 15 ms activation and
  50 ms deactivation. Integration is exact (exponential), hence unconditionally stable.

This is the cost/realism trade-off recommended by the corpus (§10: "torque-angle-velocity
actuators before full Hill muscles").

**Numerical stability** (Yeo et al. 2023). Muscles are PhysX acceleration drives
(stiffness a·ωn², damping 2ζωn√a), implicitly integrated by the solver: zero numerical oscillation
regardless of timestep. Maximum drive torque is a·Tmax·f·g. The target pose is converted to a
rotation by a damped least-squares solver (Levenberg-Marquardt) over the numerical Jacobian of
anatomical angles. Computation runs before each `Physics.Simulate`.

## 5. Musculoskeletal Models (OpenSim, Rajagopal 2016)

Chosen maximum torques and velocities are orders of magnitude compatible with Rajagopal's
upper-body actuators and Frey-Law (2012) strength surfaces. Direct extraction of OpenSim
parameters (§9.5 of the corpus) is a lead for a future version.

## 6. Soft Tissues and Impacts — Gruber (1998), Nahum & Melvin

`Core/Impacts.cs`.

- **Bullet**: transfer of real momentum p = m·v. Mass and initial velocity come from the EFT ammo
  template; velocity loss depends on distance. Gruber notes that a rigid model already overestimates
  impact forces, so the multiplier defaults to 1. The vanilla 150 N·s impulse and HollywoodFX's
  impulse are neutralized.
- **Blast**: impulse distributed as mass/(1 + 2·distance), with an upward component (ground
  reflection) and disordered rotation of distal segments.
- **Ground**: skin/clothing friction (μd = 0.8) and near-zero restitution.
- Soft-tissue wobbling mass (corpus §10) is not yet implemented.

## 7. Real-Time Articulated Dynamics — Featherstone, Geijtenbeek 2013, Lee 2009

PhysX solves joint constraints (Featherstone is not reimplemented). Brutality adds two layers
above it, in the spirit of Geijtenbeek's muscle controllers:

- an internal torque layer applied in pairs;
- a neural layer producing time-varying excitations.

## 8. Pathophysiology of Death (Neural Layer)

`Neural/MechanismProfiles.cs`, `Neural/Programs.cs`.

- **Tone extinction** by muscle group (lower limbs, trunk, neck, upper limbs), with
  mechanism-specific latency and time constant.
- **Decorticate posturing** after brain injury (decerebrate posturing was removed from the mod).
  **Fencing response** (Hosseini & Lifshitz 2009).
- **Spinal cord transection**: flaccid paralysis below the lesion level (cervical, thoracic,
  lumbar).
- **Circulatory arrest**: anoxic myoclonus, agonal gasps. **Convulsive syncope**: brief myoclonus.
- **Abdominal guarding**, hand reaching for wound (two-segment inverse kinematics), fetal curl,
  tonic extension, pedaling, kicking, rolling, 6–10 Hz tremors.
- All parameters are randomly sampled within ranges. Agony events follow a Poisson process whose
  intensity decays and shuts off no later than 1.7 s before the forced stop at 10 s.
