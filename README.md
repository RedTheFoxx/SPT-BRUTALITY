# Brutality 0.4.0 — Biomechanical Ragdolls for SPT

[![Brutality Formal Proofs](https://github.com/RedTheFoxx/SPT-BRUTALITY/actions/workflows/verify.yml/badge.svg)](https://github.com/RedTheFoxx/SPT-BRUTALITY/actions/workflows/verify.yml)

Client-side BepInEx plugin for SPT 4.1.x that replaces Tarkov's vanilla death dynamics with a
biomechanical simulation grounded in the project's scientific corpus
(`Scientific Corpus — Biomechanics for an Ultra-Realistic Ragdoll.md`).

The vanilla ragdoll is an inert puppet that the game launches with a fixed 150 N·s impulse.
Brutality turns it into a **dying body**. The nervous system shuts down according to the lesion,
muscles lose tone, contract, or lock, and tissues resist extreme ranges of motion. Everything settles
no later than **10 seconds** after death.

---

## Installation

```bash
dotnet build src/Brutality/Brutality.csproj -c Release -p:Install=true
```

This command builds and copies `Brutality.dll` into `BepInEx/plugins/Brutality/`.
The game path is configured in `Directory.Build.props`. For a custom installation path:
`-p:SptGame=/path/to/SPT`.

Manual installation: copy `src/Brutality/bin/Release/Brutality.dll` into `BepInEx/plugins/Brutality/`.

Nothing needs to be installed server-side: the plugin is 100% client-side. With Fika, it should be
installed on the **host** (and ideally on clients).

---

## What Is Simulated

### 1. Passive Tissues (the body is dead, yet remains a body)
- **Per-segment masses and inertias** from de Leva (1996): centers of mass and radii of gyration
  aligned to each bone's true longitudinal axis. Body mass varies from individual to individual
  (80 kg ± 10% by default).
- **Gear**: rig, armor plates, magazines, and backpack add weight to the thorax and shift its
  center of mass (backpack shifts it backward). Helmet and headset add weight to the head.
- **Anatomical ranges of motion** (NASA Source Book, Norkin & White, Panjabi & White).
  Expressed in ISB-style joint coordinate frames (Wu 2002/2005) computed on the skeleton at the
  moment of death. Vanilla limits are relative to the death pose and therefore anatomically wrong.
- **PhysX anatomical joints**: EFT's joints are replaced at death with `ConfigurableJoint`s whose
  limits match true anatomical ranges. The flexion limit is exact and asymmetric (a knee cannot
  hyperextend backward). Its sign, as well as the drives' sign, is **measured in-game** at the start
  of each raid by an invisible PhysX probe spawned under the map. Limits are "soft" (damped spring,
  stiffness derived from the exponential passive torque of Yoon & Mansour / Riener & Edrich):
  resistance ramps up past the range instead of hitting a hard stop.
- **Biarticular coupling**: hamstrings (hip ↔ knee) and rectus femoris. A straight leg cannot
  lift past roughly 80°.
- **Joint viscosity** and **skin/clothing ground friction** (bodies no longer slide as if on ice).

### 2. Muscles
- **Torque-angle-velocity** actuators per degree of freedom (Anderson et al. 2007), with **Hill**
  force-velocity relation, distinct peak torques in flexion and extension, and **activation
  dynamics** of 15 ms activation and 50 ms deactivation (Zajac, Thelen).
- Muscles are **PhysX acceleration drives** (mass-independent), implicitly integrated by the
  solver: zero numerical jitter (Yeo et al. 2023). Anatomical target poses are converted into joint
  rotations by a damped least-squares pose solver.
- **Collisions**: speculative continuous collision detection and bounded angular velocity so limbs
  no longer clip through walls and floors.
- **Localized damage response**: a destroyed limb goes limp; an injured limb is weakened
  proportionally to its remaining health. The limb hit by the fatal blow gives way immediately.
- **Spinal cord paralyses**: a shot to the nape (`NeckBack`) renders all four limbs flaccid.
  `SpineTop` paralyzes legs and trunk; `SpineDown` paralyzes the legs.

### 3. Nervous System: Fall and Agony
**Euphoria falls.** The fall is no longer a canned choreography: it is driven by the NaturalMotion
behaviors from the Delirium port (`docs/EUPHORIA.md`). The plan is selected at death from the injury
mechanism, posture, velocity, and shot direction:

| Plan | What happens |
|---|---|
| Stagger (`staggerFall`) | 2 to 5 real steps on the capture point, knees buckling a little more on each step, then collapse |
| Fall to knees (`shotFallToKnees`) | body drops onto its knees, holds briefly, then tips over |
| Knee slide | knees hit the ground with preserved momentum: the body slides on its knees then pitches forward |
| All fours | knees then hands, a brief support pause, then flattening out |
| Collapse | balance lost very quickly: body slumps in place |
| Seated collapse | hips flex, pelvis drops under body weight, torso leans back |
| Side collapse | body falls sideways in the frontal plane, toward the struck side when possible |
| Dive | torso pitches forward, arms reach for the ground |
| Board fall | arched spine and stiff body (posturing): falls backward like a plank |
| Windmill (`armsWindmill`) | frontal hit: arms flail through the air, torso pitches backward |
| Pivot | body spins around before balance gives way |
| Spin | trunk twists and body spirals down to the ground |
| Roll (`rollDownStairs`) | momentum at death: tip, tumble-roll, then agony on the ground |
| Slack | brain death, blast, fatal fall, or destroyed limb: tone cuts off, body drops limp |

Every plan is also **directional**. The loss-of-balance tip is a whole-body torque directed along
the shot, opposite, sideways, or diagonally depending on the plan and an azimuth drawn per death:
the body therefore falls forward, backward, onto its left or right flank, and never always along
the same axis. Even slackening (headshot, blast, spinal cord transection) receives its own tip
direction and strength: a toneless body no longer collapses the same way twice. On the ground, agony
can roll the body onto its side or flip it over.

**Death memory.** A variety director remembers the plans of recent deaths and strongly penalizes
them: the same plan does not repeat for several bodies, and each death gets its own signature (tip
force, gesture cadence, body asymmetry).

Hit reaction follows the `shot` behavior: looseness, spine pain, hand on wound, flung arm, leg
give-way, shock spin, and "last stand" when a burst hits a body still on its feet. Muscles track
Delirium effectors; physics has the final say on ground contacts.

Each death is classified by its **injury mechanism**: exact collider zone, damage type, caliber.
This mechanism sets the death timeline:

| Mechanism | Trigger | Fall | Agony |
|---|---|---|---|
| Brain | head (skull, eyes, ears, jaw) | instant blackout and drop: tone off in 20–50 ms, no posturing or rigidity | rare myoclonus, weak |
| Cervical spinal | nape | immediate flaccid quadriplegia | head only |
| Cervical vascular | throat | flinch, collapse, hands to throat | intense, 5–8.5 s |
| Thoracic | upper ribcage | flinch, knees buckling | anoxic myoclonus, gasps, clutches chest |
| Abdominal | abdomen, pelvis | guarding, trunk flexion | fetal curl, rolls, hands on wound |
| Limb | arm / leg | fall toward injured side | clutches limb, kicks with healthy leg |
| Blast | explosion | immediate flaccidity, body launched | myoclonus |
| Syncope | hemorrhage, exhaustion, toxins | progressive slump | convulsive syncope myoclonus |
| Fall | fall damage | inert | almost none |
| Burn | fire | flinch | violent: rolls, curl, flailing limbs |

**Involuntary motor programs.** Randomly drawn and weighted by mechanism, ground orientation
(supine, prone, or side), and limbs still capable of moving:

- residual postural tone, fading group by group;
- joint locks (muscle freezing);
- hand to wound via inverse kinematics;
- fetal curl;
- tonic extension (opisthotonos);
- myoclonus bursts;
- pedaling;
- kicking;
- flailing arm;
- roll attempt;
- head turn;
- agonal gasps;
- lolling head;
- arms suddenly extending (also at impact);
- knees jerking up;
- global flexion or extension jerks;
- continuous contortions: each joint drifts randomly between discrete gestures;
- **decorticate seizures** (elbows and wrists flexed, arms drawn to chest, stiff legs), with
  paroxysms and clonus;
- **continuous postural rigidity** between gestures, extending the fall posture (especially brain
  injuries);
- **Lazarus sign**: arms slowly rise and cross over the chest, then lower;
- tonic-clonic seizure: tonic phase followed by slowing jerks;
- hand reaching skyward (or forward), trembling, then dropping suddenly;
- crawl attempt and push-up attempt (when prone); with paraplegia, the body drags itself with its
  arms;
- attempt to prop up on elbows (when supine); head lifting then dropping back;
- knee drawing up then sliding down; heels drumming; scissoring legs;
- slapping the ground with an open palm; arms shielding the face; hands to face; rocking from
  flank to flank;
- **bridging** (pelvis arched up, supported on feet and shoulders), **elbow crawl**, **head banging
  the ground**, **crossed legs**, **arms stretched overhead**, **hands pressing chest**,
  **continuous axial twist**, **heel sliding toward glute**, **shoulders shrugging on each gasp**,
  **arched neck**, **full-body shudder**;
- **visible labored breathing** (chest, head, shoulders), rapid then slowing down to a **final
  breath**: deep inhale, pause, then head rolling to the side.

The same gesture rarely repeats twice in a row. Gestures depend on ground orientation (crawling
only happens prone) and still-mobile limbs.

On every death, a draw builds a unique **palette**: four to six gestures are favored, three to five
are attenuated, and cadence, body asymmetry, and contortion amplitude vary with it. Memory of the
previous two deaths further reduces the weight of already-seen gestures. No two agonies play the
same selection, order, or cadence — and a destroyed arm, paraplegia, or detached limb automatically
excludes impossible gestures.

A first clear gesture is guaranteed as soon as the body touches the ground, and up to three gestures
can overlap.

**Real impacts.** A bullet transfers its momentum p = m·v at the impact point, using mass and
velocity read from the ammo template with distance-based velocity loss. The actually transferred
fraction varies per shot within a caliber-class interval (deformation, fragmentation, tumbling,
exit), with slight angular scatter and variable lever arm:

| Class | Multiplier | Scatter |
|---|---|---|
| Pistol | ×0.85 to 1.4 | 9° |
| Intermediate (5.45, 5.56…) | ×0.75 to 1.55 | 12° |
| Full-power (7.62×51, 7.62×54R, .338…) | ×0.9 to 1.85 | 6° |
| Heavy subsonic (9×39, .300 BLK sub…) | ×1.05 to 1.75 | 5° |
| Heavy caliber (12.7 mm…) | ×1.15 to 2.1 | 6° |
| Slug / buckshot | ×1.1 to 2.1 / ×0.7 to 1.7 | 7° / 20° |
| Armor-piercing (penetration > 40) | interval narrowed up to −28% | ×0.7 |

The fatal shot draw also modulates directional fall force and impact reaction intensity. Blast
impulses vary from ×0.7 to ×1.4 and distribute across segments by mass and distance. Projectile
energy modulates reflex intensity.

**Guaranteed stop within 10 s max.** Agony stays at full intensity for nearly half its window, then
fades out no later than 1.5 s before the end. A dissipation phase brings the body to rest, then the
game freezes rigidbodies (unless permanent simulation is enabled). If the body settles earlier,
shutdown happens early to save CPU.

---

### 3.1 Headshot

A headshot cuts tone in 20 to 50 ms: the body drops from standing height, limp, without stiffening.
No posturing is played (it requires an intact brainstem), no background rigidity sets in, and agonal
breathing is nearly absent. Released spinal jerks (clonus, spasms, Lazarus sign) remain later on
the ground.

### 3.2 Shot in the Back

A shot hitting the trunk from behind no longer tips the body over as a rigid block on stiff legs.
The spine bends by only half the usual pain response, legs are no longer stiffened, and the
quadriceps gives way under body weight: the pelvis drops, knees flex, and the fall begins with leg
collapse. Rolling is no longer triggered from a stationary death; it remains possible if the victim
was running when hit.

## Euphoria Engine and Permanently Simulated Corpses

The fall runs a port of [Delirium](https://github.com/PVCPC/delirium-physics), the open-source
reverse-engineering project of Euphoria (the GTA engine): Delirium's laws with GTA V
`behaviours.xml` default values (Delirium's own constants are extracted from the GTA IV binary and
are not redistributable). Since 0.2.0, it is the **sole animation engine**: 0.1.0 procedural
choreographies were removed, while Brutality's agony and cerebral posturing remain active on top.
The dying character reacts to gunfire (looseness, hand on wound with `pull` traction and palm
pronation, flung arm, `upperBodyFlinch` guard reflex on frontal bursts), staggers with real steps,
gives way on one or both legs (simultaneously or in two stages), reaches to grab nearby walls,
crates, and ledges (`grab` with break force), windmills out of phase while falling backward, catches
itself or rolls (`rollDownStairs` + `bodyRollUp`), then writhes on the ground. Under a burst, it
briefly stays upright ("last stand"). Corpses are no longer frozen: they stay simulated and react
when shot.
Details, Delirium mapping, and formal proofs: `docs/EUPHORIA.md` and `tests/Brutality.DeliriumTests`.

## Mod Compatibility

| Mod | Status | Action |
|---|---|---|
| **Smoothie** | Compatible. Its "Corpses stop moving after (s)" setting is neutralized for Brutality-managed bodies. Its 60 Hz physics cap is respected: Brutality runs before each `Physics.Simulate` step. | **Nothing to disable.** Optional: set "Corpses stop moving after (s)" to **10** to align unmanaged bodies (Brutality disabled or beyond cap). |
| **HollywoodFX** | Compatible if its ragdolls stay disabled. Its "Gore" option applies up to 200 N·s impulse at death: Brutality **neutralizes** it ("Neutralize death impulses from other mods"). Blood and decals are preserved. | Keep "Enable Ragdoll Effects (RESTART)" and "Enable Cinematic Ragdolls (RESTART)" set to **false**. If enabled, HollywoodFX reanimates frozen corpses when shot (a warning is logged). |
| **Fika** | Compatible. Host simulates death; clients animate their local copy and Fika applies the host's final pose. | None. |
| **ReClassic** (unconsciousness) | Compatible. An unconscious body that dies is taken over by Brutality. | None. |
| **Rupture** | Conflict: both mods drive ragdolls. | Disable Rupture if installed. |

---

## Configuration

File `BepInEx/config/com.brutality.ragdoll.cfg`, editable in-game with F12 (ConfigurationManager).

| Section | Setting | Default | Role |
|---|---|---|---|
| General | Enable Brutality | true | Disabled: vanilla behavior. |
| | Max motion duration (s) | 10 | 3 to 10 s. |
| | Apply to your own corpse | true | |
| | Apply to observed corpses (Fika client) | true | |
| | Max simultaneous simulated corpses | 12 | Beyond cap: passive physics only, no agony. |
| | Full simulation distance (m) | 150 | Beyond distance: no agony. |
| Body | Average body mass (kg) | 80 | |
| | Body mass variation | 0.10 | |
| | Include carried gear mass | true | |
| | Tissue ground friction | 0.8 | |
| Behavior | Agony intensity | 1 | 0 = no agony, 2 = double. |
| | Spasm vigor | 1 | Sharpness and force of involuntary gestures, contortion amplitude. 0.5 = subtler. |
| | Directional fall drive | 1 | Loss-of-balance strength and initial tip of Euphoria plans. 0 = collapse under weight alone. |
| | Cerebral posturing intensity | 1 | Decerebration, decortication, Lazarus sign, board falls. 0 = never. |
| | Gesture variety | 1 | Frequency of elaborate gestures and posture-specific falls, spread between per-death palettes. 0 = basic gestures and neutral palettes. |
| | Visible agonal breathing | true | Labored breathing and final breath. |
| | Reflex intensity | 1 | Flinch, posturing, joint locks. |
| | Muscle strength | 1 | |
| | Passive joint stiffness | 1 | |
| Impacts | Bullet momentum multiplier | 1 | 1 = real physics. |
| | Explosion blast multiplier | 1 | |
| | Neutralize death impulses from other mods | true | |
| Stability | Joint mass balancing | 1 | 1 = EFT method, most stable. |
| | Joint drive convention | Auto | Auto = verified during the first deaths of the raid (logged). |
| | PhysX solver iterations | 12 | |
| Debug | Detailed logging | false | Motor timeline of each death. |

---

## Verifying It Works (First Test)

In `BepInEx/LogOutput.log`, on load:

```
Brutality: wound tracking on N ApplyDamageInfo implementation(s).
Brutality: Smoothie detected — early corpse freeze neutralized ...
Brutality 0.4.0 loaded.
```

If these lines are missing, the plugin did not activate: look for `Brutality: invalid configuration`
or `initialization failed` in the log.

Then on each death:

```
Brutality: <name> died — Thoracic, zone Chest/RibcageUp, 545x39 (intermediate, push x1.12), 1612 J, 3.2 N·s, mass 83 kg (+11.4 kg gear), posture Stand, fall: reach
```

To diagnose, enable "Detailed logging": each triggered motor program is listed with its timestamp
and intensity, along with any neutralized impulses.

At raid start, the PhysX probe should log a line like
`Brutality: PhysX probe — drive 30.0° (target 30°) -> normal; X limit 60.0° -> normal.`
If it says `undetermined`, share the log.

Messages to watch for:
- `unsupported ragdoll (missing segment…)`: unexpected skeleton, body stays vanilla.
- `no Physics.Simulate step intercepted`: fallback mode, less accurate.
- `simulation error`: body falls back to passive ragdoll; please share the log.

---

## Off-Game Tests

Anatomical conventions and Delirium port fidelity are covered by two standalone test benches that
compile the actual C# plugin sources directly without the game:

```bash
dotnet run --project tests/Brutality.KinematicsTests
dotnet run --project tests/Brutality.DeliriumTests
```

The automatically generated proof sheet (**77 / 77** checks: Delirium laws, 60 Hz simulation,
60-death statistical diversity audit, ISB kinematics, IK, and BepInEx config) can be viewed in
[`docs/VERIFICATION.md`](docs/VERIFICATION.md), with SHA-256 digests and reproducible scenarios in
`demo/test-bench.html`.

To regenerate `docs/VERIFICATION.md` locally from a fresh run:

```bash
dotnet run --project tests/Brutality.DeliriumTests -- --report docs/VERIFICATION.md
dotnet run --project tests/Brutality.KinematicsTests -- --append-report docs/VERIFICATION.md
```

A ready-to-use GitHub Actions workflow (`.github/workflows/verify.yml`) also runs both suites on
Linux without any proprietary DLLs and publishes the sheet as a build artifact and workflow summary.

---

## Architecture

```
src/Brutality/
  Plugin.cs, Settings.cs, Compat.cs        BepInEx entry point, config, compatibility
  Anatomy/AnatomyData.cs                   segments, joints, de Leva, ranges, torques
  Core/
    JointKinematics.cs                     anatomical angles (pure functions, off-game tested)
    BodyModel.cs                           segments, frames, masses/inertias, joints, state
    JointRig.cs                            replaces EFT joints with PhysX anatomical joints
    DriveController.cs                     muscles as PhysX drives: activation, Hill, biarticular coupling
    DeathContext.cs                        death analysis: mechanism, ballistics, health, gear
    Wounds.cs                              wound history, ammo database
    Impacts.cs                             ballistic & blast impulses, ammo-class variation
    BrutalityRagdoll.cs                    corpse lifecycle, physics clock, stop <= 10 s
    VarietyDirector.cs                     anti-repetition memory for plans & gestures, per-death palette
  Neural/
    MechanismProfiles.cs                   timelines per injury mechanism
    NeuralController.cs                    program construction and agony scheduler
    Programs.cs, Motor.cs                  motor programs, command blending, IK
    Postures.cs                            decortication, posturing seizures, rigidity, Lazarus sign
    AgonyMoves.cs                          impact reaction, breathing, final breath, elaborate agony moves
  Patches/BrutalityPatches.cs              Physics.Simulate, Player.OnDead, ApplyDamageInfo, TryPutToSleep
tests/Brutality.KinematicsTests/           off-game kinematics test bench (46 tests)
tests/Brutality.DeliriumTests/             Delirium fidelity & diversity formal proofs (31 tests)
docs/SCIENCE.md                            scientific corpus -> implementation mapping
docs/EUPHORIA.md                           Delirium port architecture
docs/VERIFICATION.md                       test-generated formal proof sheet (77 tests)
.github/workflows/verify.yml               GitHub Actions CI workflow (77 tests without game DLLs)
demo/test-bench.html                       interactive 3D demo studio
```

## Limitations of Version 0.4.0
- Not yet tested in-game: orders of magnitude (tone, gains, intensities) will be tuned after the
  first live test via configuration or the tables in `AnatomyData.cs` and `MechanismProfiles.cs`.
- Feet and hands are not simulated segments (absent from EFT's ragdoll): they are merged with the
  calf and forearm.
- No wobbling mass for soft tissues (corpus lead, §10). Planned for a later version.
- Euphoria engine and permanently simulated corpses (sources and parameter mapping:
  `docs/EUPHORIA.md`): not yet tested in-game. With permanent simulation enabled, keep HollywoodFX
  ragdolls disabled.
- Legacy 0.1.0 "Brutality" and "Mixed" modes were removed: configuration no longer offers an
  "Animation engine" selector. Euphoria is the permanent engine.
