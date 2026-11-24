# Euphoria (NaturalMotion) Engine in Brutality

The "Euphoria" engine is a **port of Delirium**, the open-source reverse-engineering project of Euphoria (the GTA engine). Only the laws useful for an SPT ragdoll are ported.

Since version 0.2.0, it is the **sole animation engine** of the plugin: the legacy "Brutality" mode (procedural fall choreographies) was removed. Brutality's agony, cerebral posturing, breathing, and reflexes remain active and blend with Delirium behaviors in the same motor buffer.

## Sources
- **[PVCPC/delirium-physics](https://github.com/PVCPC/delirium-physics)** (Rust, GPL-3.0): reimplementation of Euphoria from the GTA IV binary. The port follows its architecture (`nmrs` = skeleton and effectors, `nmdbm` = dynamic balance, `nmbb` = upper-body balancer, `nmshot` = `shot` behavior, `nmcf`, `nmrds`, `nmbw`, `nmbf`, `nmiog`) and its numerical laws (setpoint cone, `blend_to_zero`, `set_stiffness`, `oppose_gravity`, capture point, arm counterbalance, gesture envelopes).
- **GTA V `behaviours.xml`**: Delirium's numerical values are read from the GTA IV binary (`kv!` macro) and are not redistributable. The port therefore preserves the **laws** and uses the values published by NaturalMotion (`NmParams.cs`, via [alexguirre/GTA-V-Euphoria-Behaviours-Parser](https://github.com/alexguirre/GTA-V-Euphoria-Behaviours-Parser)). Places where Delirium chooses its own constant are marked with a `Delirium:` comment.
- Post-mortem idiomuscular contraction (Zsako phenomenon): Madea (ed.), *Estimation of the Time Since Death*, 3rd ed., CRC 2016, chapter on supravital reactions. (Brutality program, outside Delirium.)

## Port Architecture
| File | Contents |
|---|---|
| `Euphoria/Delirium/DlCharacter.cs` | `DlCharacter`: effectors per joint and degree of freedom (absolute desired angle, stiffness ω, damping ζ, `muscleStiffness`, `oppose_gravity`), message masks (`ub`, `ua`, `uc`, `uw`, `us`, `un`, `ll`, `lr`, `lb`), `blend_to_zero`, setpoint cone; `DlBody` (sensors, per-segment forces and torques); `DlPhys` (oppose-gravity as real torque, raycast, noise, envelope). |
| `Euphoria/Delirium/DlBalance.cs` | `bodyBalance` / `staggerFall` / `shotFallToKnees`: upper-body law, arm counterbalance, balance manager (capture point, steps, pelvis, leg IK), support and righting forces. |
| `Euphoria/Delirium/DlShot.cs` | `shot` behavior state machine: looseness, reaction, spine pain, reach for wound, flung arm, leg give-way, look-at, shock spin. |
| `Euphoria/Delirium/DlBehaviours.cs` | `catchFall`, `rollDownStairs`, `bodyRollUp`, `armsWindmill`, `pedalLegs`, `bodyWrithe`, `bodyFoetal`, `injuredOnGround`. |
| `Euphoria/Delirium/DlGrab.cs` | `grab` + `GrabHelper` (8-direction environment probes, break-force physical grip, palm pronation, release after breakaway) and `upperBodyFlinch` (`nmfl`). |
| `Euphoria/NmPerformance.cs` | orchestrator: death plan, phase sequencing (`Balance → Shot → Flinch/Fall → Grab → Ground`), ballistic impulses, snap, angular damping, `setCharacterHealth` (residual liveliness), logging. |

## Effector Units
- Delirium: `set_stiffness(s, d)` stores `strength = s²` and `damping = 2·s·d`; its engine derives `stiffness = √strength` (natural frequency ω) and `damping ratio = damping / (2·stiffness)`. Brutality writes these exact two quantities into `MotorCommandBuffer.AddEffector`, so **ω = s and ζ = d with no conversion**.
- `muscleStiffness` scales passive stiffness (1.01 − looseness).
- `oppose_gravity` is a **fraction** (0..1) of the segment's gravity torque, applied as a real torque (child + reaction on parent). It is used only on limbs (arms, legs): applying it to the spine would cancel trunk gravity and the body would no longer fall. Body weight is borne by the legs and their ground contacts, never by a vertical balancer push.
- Behaviors never act directly on a rigidbody: they write effectors, then `DlCharacter.Flush` forwards them once per step to PhysX drives. Brutality programs (posturing, locks, agony, breathing) write into the same buffer and blend together.

## Message Mapping
| Euphoria Message (Delirium) | Port |
|---|---|
| `bodyBalance`, `staggerFall`, `shotFallToKnees` | `DlBalance` (+ `stayUpright`: weight support and "last stand" under a burst) |
| `shot` (`nmshot`) | `DlShot` |
| `configureBullets`, `applyBulletImpulse` | `NmPerformance.OnBullet` + `NmImpulseQueue` (triangular impulse, rigid-body share, burst damping) |
| `shotSnap`, `shotShockSpin`, `setCharacterDamping` | `NmPerformance.Forces`, `DlShot.Forces` |
| `catchFall`, `rollDownStairs`, `bodyRollUp`, `armsWindmill`, `pedalLegs` | `DlBehaviours` |
| `grab`, `upperBodyFlinch` | `DlGrab` (`DlGrab` + `GrabHelper` + `DlFlinch`) |
| `injuredOnGround`, `bodyWrithe`, `bodyFoetal` | `DlBehaviours`, coexisting with Brutality agony |
| `setCharacterHealth` | `NmPerformance.U(t)`: liveliness fades with health (the body goes limp) |

## Formal Proofs and Delirium Fidelity (1.0.0)
The `tests/Brutality.DeliriumTests` suite compiles and runs the actual C# plugin sources directly in an off-game 3D simulation at 60 Hz:
- **Execution order (`nm.rs`)**: `Balance` runs before `Shot` so it does not overwrite reaching for the wound or giving way at the knees.
- **Effector laws (`nmrs`)**: `set_stiffness(s, d)` (`strength = s²`, `damping = 2·s·d`, `ω = √strength`, `ζ = damping / (2·ω)`), `set_relaxation` with invariant `ζ`, cone `1 + 25 / (strength + 256)`, masks and `lock`/`unlock`, and DOF 2 pronation emission on elbows.
- **Real physics (`nmdbm`, `nmshot`, `nmgr`)**: bounded `oppose_gravity` projected onto hinge axes (exact action + reaction), `Shot::wrist` / `ForearmPronationForNormal`, `Shot::pull` hand ↔ wound action/reaction, `shockSpin` in N·m proportional to lever arm, `Grab` on walls/ledges with zero false positives on flat ground, and unilateral (1-leg), synchronous (2-leg), and staggered (2-stage) leg give-ways.

The complete table of **77 assertions** (31 Delirium + 46 Kinematics/ISB/BepInEx), verifiable SHA-256 digests, and reproducible scenarios in `demo/test-bench.html` are collected in [`docs/VERIFICATION.md`](VERIFICATION.md).

## What Is Not Ported, and Why
- **SDK, playroom, file readers** (XML/messages): irrelevant outside Delirium's tool.
- **`getup`, `ledge` (climbing), `walk`/`run`, portals, skate**: assume a living agent getting back up or moving around; an SPT death ragdoll never uses them (`grab` is ported, however, to grab walls, crates, and ledges during the fall).
- **Clavicles and wrists**: absent from the EFT ragdoll (hands are merged with the forearm). The `uc` mask targets shoulders; `uw` is empty.
- **`balancerCollisionsReaction`** (sliding along a wall): not ported; environment contact remains handled by PhysX.
- **Ankle and foot joints**: the foot is not simulated; pelvis tilt that Delirium sends to the ankle goes to the spine so it does not fight leg inverse kinematics.

## Plan Selection and Rolling
Delirium does not decide on its own that a body rolls: the game's "shot" task selects the message (`rollDownStairs`, `staggerFall`, …). The port therefore reuses the rolling law, while **plan selection** is our own:

- momentum ≥ 2.4 m/s at death (running, hit while moving) → immediate roll; ≥ 1.8 m/s → collapse;
- otherwise weighted draw among thirteen plans: stagger, fall to knees, knee slide, all fours, collapse, seated collapse, side collapse, dive, board fall, windmill, pivot, spin, and roll (weight 0.65 to 0.85 × variety for momentum-requiring plans; × 1.4 to 2 depending on shot direction, crouched posture, hit leg, and movement);
- the loss-of-balance tip is directional: an azimuth is drawn in the pelvis frame (0 = forward, π/2 = right, π = backward), offset by the shot axis and plan. It is applied as a whole-body torque over 0.45 s rather than a single forward tip; this makes the body fall forward, backward, sideways, or diagonally depending on the death;
- a **variety director** remembers the last five plans and divides their weight down to 0.12: the same plan does not recur for several bodies. Each death also receives a signature (tip force, gesture cadence, asymmetry);
- even slackening (brain death, blast, spinal cord transection) is directional: the body slumps along the push axis and then tips with a force drawn per death;
- a roll plan keeps a brief balance window (0.12–0.35 s) then pitches forward: without this tip, the body crouches in place and rotation is barely visible;
- during the fall, rolling replaces catch-fall as soon as lateral momentum reaches 1.2 m/s;
- on the ground, it is no longer cut off at the instant of contact: it continues (or starts) if the body still has momentum (> 1.4 m/s or > 3 rad/s thorax angular velocity), then fades out on its own after 4 s falling / 2.4 s on the ground, or as soon as translation drops below 0.45 m/s after 1.2 s;
- on the ground as well, a roll-over may be drawn (rolling onto a flank or turning over): it is a whole-body torque, not a pose, so agony continues on top of it;
- the board fall (posturing) forces a stiffness of 15 and an arched spine for 1.5 s: the body falls backward stiffly instead of collapsing.

## Agony Gestures and Variety
Involuntary gestures are not merely weighted by mechanism: each death draws a **palette** (four to six favored gestures, three to five attenuated ones), a cadence, an asymmetry, and a contortion amplitude. Memory of the previous two deaths further reduces the weight of recently seen gestures (`Core/VarietyDirector.cs`). The added cross-cutting gestures (bridging, elbow crawl, head banging the ground, crossed legs, arms overhead, hands pressing chest, continuous axial twist, heel sliding toward glute, shoulders shrugging, arched neck, full-body shudder) are automatically filtered by joint capacities: paraplegia prevents bridging, a destroyed arm cannot press the chest.

Windmill arms no longer start concurrently with a roll: both wrote the same posture.

## Fall Sound
EFT only plays the corpse drop sound (`Corpse.PlayCorpseDropSound`, `ProneDropSoundBank` by surface) at **ragdoll creation** — inside `Corpse.TryToCreateRagdoll` — i.e., at the moment of death rather than on ground contact. Brutality replays it once when the body actually reaches the ground (Euphoria "on ground" phase, or otherwise a 35 cm pelvis drop and settled velocity), and only if the body truly fell (acquired vertical speed ≥ 0.8 m/s); a prone death triggers nothing.

## Permanently Simulated Corpses
After the active phase, the body is no longer frozen. PhysX puts it to sleep at rest, which costs almost nothing. A bullet (shot delegate from `BallisticsCalculator`) wakes it up: spread impulse, ground bounce, and local idiomuscular contraction, then a fresh `shot` reaction if the dying body is still conscious. The game's `OnRigidbodyStopped` callback is invoked once. Beyond the simulated corpse cap, the oldest ones are frozen as in vanilla. Observed bodies on a Fika client retain vanilla behavior.
