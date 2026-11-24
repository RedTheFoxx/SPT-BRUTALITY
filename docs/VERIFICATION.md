# Brutality 1.1.1 — Formal Proof Sheet

> **Automatically generated document** by running the out-of-game test suites (`Brutality.DeliriumTests` and `Brutality.KinematicsTests`) directly against the plugin's real C# sources. No result row below is hand-typed.

## Metadata & Traceability

| Field | Value |
|---|---|
| Plugin version | `1.1.1` |
| Execution date | `2026-11-24 21:40 UTC` |
| Runtime & OS | `.NET 10.0.14` / `Unix 7.3.6.1` |
| SHA-256 hash of the 15 tested C# sources | `f6075d47627af2a7e2f76c61ef46f9ae1370ce02154fc729bb07abdaa6bce4ac` |
| SHA-256 hash of `Brutality.dll` (Release) | `49132b76897bdbe70100d646d27b063cb61fddb5d70222313c72bdaf78f1176a` (248,320 bytes) |
| Local reproduction | `dotnet run --project tests/Brutality.DeliriumTests -- --report docs/VERIFICATION.md` then `dotnet run --project tests/Brutality.KinematicsTests -- --append-report docs/VERIFICATION.md` |

## Overall Summary

| Suite | Scope | Result | Status |
|---|---|---:|---|
| `tests/Brutality.DeliriumTests` | Delirium laws (`nmrs`, `nmdbm`, `nmbb`, `nmshot`, `nmcf`, `nmgr`, `nmrds`, `nmfl`, `nmbr`, `nmfow`, `nmhf`, `onfire`), 60 Hz simulation & 60-death diversity | **40 / 40** | `PASS` |
| `tests/Brutality.KinematicsTests` | ISB anatomy, IK, pose solver, thorax envelope, BepInEx keys | **48 / 48** | `PASS` |
| **Verified total** | **Complete out-of-game formal proofs** | **88 / 88** | `PASS` |

## Level 1 — Law-by-Law Mathematical Fidelity (Delirium Rust vs Brutality C#)

| # | Delirium Reference | Brutality Source | Verified Invariant | Observed Measurement | Status |
|---:|---|---|---|---|---|
| 1 | `nmrs.rs (set_stiffness)` | `DlCharacter.cs (SetStiffness)` | nmrs::set_stiffness: strength = s², damping = 2·s·d | `(strength=121,0, damping=16,5)` | `PASS` |
| 2 | `nmrs.rs (muscles)` | `DlCharacter.cs (Flush)` | nmrs::muscles → Flush: ω = √strength and ζ = damping/(2·ω) without conversion | `(ω=11,00, ζ=0,75, ms=0,90)` | `PASS` |
| 3 | `nmrs.rs (set_relaxation)` | `DlCharacter.cs (SetRelaxation)` | nmrs::set_relaxation(0.4): ω = 0.4·base, invariant ζ = baseDamping | `(ω=4,80, ζ=1,00)` | `PASS` |
| 4 | `nmrs.rs:545 (targets)` | `DlCharacter.cs (DlCfg.ConeRadius)` | nmrs::targets: ConeRadius(strength) = 1 + 25/(strength+256) decreasing with strength | `(kWeak=1,096, kStiff=1,052)` | `PASS` |
| 5 | `nmrs.rs (set_relative, targets)` | `DlCharacter.cs (SetRelative, Targets)` | nmrs::set_relative + targets: weak muscle allows wider cone overshoot than stiff muscle | `(weak=164,4°, stiff=160,1°, hi=155,0°)` | `PASS` |
| 6 | `nmrs.rs (mask)` | `DlCharacter.cs (Mask)` | nmrs::mask("fb") = 10 joints | `(10 joints)` | `PASS` |
| 7 | `nmrs.rs (mask)` | `DlCharacter.cs (Mask)` | nmrs::mask("ub") = 6 (spine+neck+arms) | `(6 joints)` | `PASS` |
| 8 | `nmrs.rs (mask)` | `DlCharacter.cs (Mask)` | nmrs::mask("ut") = 5 (trunk+arms, WITHOUT neck) | `(5, Neck absent=True)` | `PASS` |
| 9 | `nmrs.rs (mask)` | `DlCharacter.cs (Mask)` | nmrs::mask("uk") = 2 (spine+neck) | `(2 joints)` | `PASS` |
| 10 | `nmrs.rs (mask)` | `DlCharacter.cs (Mask)` | nmrs::mask("uw") = 0 (wrist absent in EFT) | `(0 joints)` | `PASS` |
| 11 | `nmrs.rs (mask)` | `DlCharacter.cs (Mask)` | nmrs::mask("lb") = 4, "ll" = 2, "lr" = 2 | `(lb=4, ll=2, lr=2)` | `PASS` |
| 12 | `nmrs.rs (lock/unlock)` | `DlCharacter.cs (Lock/Unlock)` | nmrs::lock/unlock("lb"): preserves upper body and keeps legs | `(shR=1,23 rad, kneeL=1,11 rad)` | `PASS` |
| 13 | `nmrs.rs (muscles) + nmshot` | `DlCharacter.cs (Flush DOF 2)` | Flush elbow pronation (DOF 2): emitted on L/R elbows, ignored on knees | `(R=-48,7°, L=48,7°, knee=False)` | `PASS` |
| 14 | `nmshot (wrist)` | `DlCharacter.cs (DlPhys.ForearmPronationForNormal)` | Shot::wrist / ForearmPronationForNormal: L/R palms rotated to face the floor | `(pR=-75,0°, dotR=0,97 ; pL=75,0°, dotL=0,97)` | `PASS` |
| 15 | `nmdbm.rs (oppose_gravity)` | `DlCharacter.cs (DlPhys.OpposeGravity)` | nmdbm::oppose_gravity: action+reaction=0, bounded torque, and hinge projected onto axis | `(\|Στ\|=0,0000, τSh=8,4 N·m, τEl=2,3 N·m, align=1,000)` | `PASS` |
| 16 | `nmbb.rs (BodyBalance)` | `DlBalance.cs (Tick)` | nmbb::BodyBalance: shoulder=11, elbow=6.6, Busy preserved, BlendToZero no longer freezes on rifle | `(ωSh=11,0, ωEl=6,6, busyKept=True, shDes=50,3°)` | `PASS` |
| 17 | `nm.rs (Balance → Shot order)` | `NmPerformance.cs (Tick)` | nm.rs order Balance → Shot: Shot survives Balance (Reach active, collapsed knee not overwritten) | `(reached=True, ωKneeL=3,7, ωKneeR=13,0, phase=Balance)` | `PASS` |

## Level 2 — Behavioral Proofs in 60 Hz Physics Simulation

| # | Delirium Reference | Brutality Source | Verified Invariant | Observed Measurement | Status |
|---:|---|---|---|---|---|
| 18 | `nmshot (reach, pull, wrist)` | `DlShot.cs (Reach, Forces)` | nmshot::reach + pull + wrist: hand brought closer to wound, exact action/reaction pull, active pronation | `(d0=0,57m→min=0,25m, pull=True, ΣF=0:True, pron=61,2°)` | `PASS` |
| 19 | `nmshot (shock_spin)` | `DlShot.cs (OnBullet, Forces)` | nmshot::shock_spin: torque in N·m (20..90 N·m, not Deg2Rad) proportional to lever arm | `(spin=58,7 N·m, τThorax=58,7 N·m)` | `PASS` |
| 20 | `nmshot (LOOSE_L)` | `DlShot.cs (OnBullet, Tick)` | nmshot::LOOSE_L: hit left arm goes limp, right arm reaches for injured arm | `(ωElL=2,2, ωElR=11,0, reachR=True)` | `PASS` |
| 21 | `nmshot (GiveWay)` | `DlShot.cs, DlBalance.cs` | nmshot::GiveWay (back shot): knee stiffness drops (<4), knees flex, pelvis descends | `(minωKnee=1,8, kneeFlex=117,0°, drop=0,45m)` | `PASS` |
| 22 | `nmshot + nmdbm` | `DlBalance.cs (LegBuckle, Forces)` | Leg buckling: 1-leg (unilateral), 2-leg (synchronous) AND 2-leg staggered (2-step) proven | `(uni=True, sync=True, staggerMid=True, bothLate=True)` | `PASS` |
| 23 | `nmcf (CatchFall)` | `DlBehaviours.cs (DlCatchFall)` | nmcf::CatchFall: probes toward FLOOR (y<0), forward>0, hits floor, neck in rad, palm pronation | `(down=True, fwd=True, hit=True, neckRad=True, pron=True)` | `PASS` |
| 24 | `nmgr (Grab, GrabHelper)` | `DlGrab.cs (DlGrab)` | nmgr::Grab + GrabHelper: detects wall & crate, 0 flat-ground false positives, physical grip + force + snap + Busy release | `(act=True, grip=True, force=True, snap=True, rel=True[b0=False,b1=False,gAct=False], openFP=False, crate=True)` | `PASS` |
| 25 | `nmrds + nmru` | `DlBehaviours.cs (DlRollDownStairs, DlRollUp)` | nmrds::RollDownStairs + nmru::RollUp: active roll, real rotation, Lumbar without oppose_gravity | `(plan=RollDownStairs, peakW=2,9 rad/s, lumbarOg0=True)` | `PASS` |
| 26 | `nmaw (ArmsWindmill)` | `DlBehaviours.cs (DlWindmill)` | nmaw::Windmill: out-of-phase arms (≠ synchronous robot) and individual stop on impact | `(phase=3,75 rad, Δsh=151,8°, leftStopped=True)` | `PASS` |
| 27 | `nmbw (BodyWrithe)` | `DlBehaviours.cs (DlWrithe)` | nmbw::Writhe: asymmetric L/R legs + real rollOver torque in Forces() | `(Δhip=10,2°, peakRollTq=9,3 N·m)` | `PASS` |
| 28 | `nmbf (BodyFoetal)` | `DlBehaviours.cs (DlFoetal)` | nmbf::Foetal: per-joint stiffness asymmetry + crossed pronation over chest | `(sL=66,2, sR=77,6, pronL=39,3°, pronR=-40,5°)` | `PASS` |
| 29 | `nmfl (UpperBodyFlinch)` | `DlGrab.cs (DlFlinch)` | nmfl::UpperBodyFlinch: protective reflex on frontal burst + forearm pronation | `(flinch=True, peakPron=32,4°)` | `PASS` |
| 30 | `nmdbm (staggerFall)` | `DlBalance.cs` | nmdbm::Balance: grounded leg weight support sustains >= 2 stagger steps under rifle recoil | `(steps=5, horizDisp=1,44 m)` | `PASS` |
| 31 | `nmdbm (staggerFall)` | `DlBalance.cs + DlShot.cs` | nmdbm::Stagger (backward): knees bend during swing/stance (not locked straight), LowerK decays without Shot overwrite, bounded vertical support | `(steps=4, kneeL=128,0°, kneeR=131,1°, maxUp=1,04·mg, minKneeω=1,8)` | `PASS` |
| 32 | `nmbr (Brace)` | `DlExtraBehaviours.cs (DlWallBrace)` | nmbr::BalancerCollisionsReaction: wall brace + slump softening behind character | `(brace=True, slump=True, kneeRed=7,7)` | `PASS` |
| 33 | `nmfow (FallOverWall)` | `DlExtraBehaviours.cs (DlWallBrace)` | nmfow::FallOverWall: low obstacle at waist height with clear air above triggers tip-over | `(fallOverWall=True, wallBrace=True)` | `PASS` |
| 34 | `nmhf (HighFall)` | `DlExtraBehaviours.cs (DlHighFall)` | nmhf::HighFall: airborne orientation + time-to-impact landing brace on downward descent | `(highFall=True, preparingToLand=True)` | `PASS` |
| 35 | `onfire (OnFire)` | `DlExtraBehaviours.cs (DlOnFire)` | onfire::OnFire: Molotov stagger swatting + ground stop-drop-roll torque | `(onFire=True, peakGroundRollTq=34,0 N·m)` | `PASS` |
| 36 | `meleetask (Melee)` | `DlExtraBehaviours.cs (DlMeleeGuard)` | meleetask::MeleeGuard: defensive knee duck + overhead pronated forearm guard | `(meleeGuard=True, peakPron=42,2°, sink=0,23 m)` | `PASS` |
| 37 | `shottask.rs` | `NmPerformance.cs (ClassifyWeapon)` | weapons.gif::ClassifyWeapon: distinct Pistol / SmgBurst / Sniper / Shotgun classes + scaled recoil | `(pistol=Pistol 0,02m/s, smg=SmgBurst, sniper=Sniper 0,14m/s, shotgun=Shotgun 0,13m/s)` | `PASS` |
| 38 | `nmrds (RollDownStairs)` | `NmPerformance.cs (Update)` | nmrds::RollDownStairs: high horizontal knockback velocity triggers roll without Plan.Moving | `(moving=False, roll=True, peakω=3,2 rad/s)` | `PASS` |

## Level 3 — Statistical Kinematic Diversity Audit (60 Tactical Deaths, Seeds 1000..1059)

| # | Delirium Reference | Brutality Source | Verified Invariant | Observed Measurement | Status |
|---:|---|---|---|---|---|
| 39 | `VarietyDirector + NmPlan` | `NmPerformance.cs, VarietyDirector.cs` | 60-death diversity: ≥ 8 distinct plans, no plan > 35%, all 3 sectors covered | `(12 plans, max=9/60, Fwd/Side/Back=24/12/24)` | `PASS` |
| 40 | `Full Delirium suite` | `DlBalance, DlShot, DlGrab, DlBehaviours` | 60-death diversity: env grabs + wounds + pronations + rolls + 1L & 2L buckles all proven | `(10 grabs, 31 wounds, 54 pronations, 15 rolls, 1L=10, 2L=32, NaN=0)` | `PASS` |

### Observed Metrics Across 60 Deaths (12 Scenarios × 5 Iterations)

| Indicator | Formal Threshold | Observed Measurement | Status |
|---|---|---|---|
| Distinct Euphoria plans | `>= 8` | `12` | `PASS` |
| Max share of a single plan | `<= 21 / 60 (35%)` | `9 / 60 (15,0%)` | `PASS` |
| Azimuth sectors (Forward / Side / Back) | `>= 5` per sector | `24 / 12 / 24` | `PASS` |
| Environment grabs (`grab`) | `>= 6` | `10 / 60` | `PASS` |
| Hands reaching for wound (`Shot::reach`) | `>= 25` | `31 / 60` | `PASS` |
| Forearm pronations (`> 15°`) | `>= 35` | `54 / 60` | `PASS` |
| Rolls (`rollDownStairs` / `rollUp`) | `>= 5` | `15 / 60` | `PASS` |
| 1-leg buckling (unilateral) | `>= 8` | `10 / 60` | `PASS` |
| 2-leg buckling (synchronous / staggered) | `>= 8` | `32 / 60` | `PASS` |
| Arm windmills (`armsWindmill`) | informational | `15 / 60` | `INFO` |
| Guard reflexes (`upperBodyFlinch`) | informational | `14 / 60` | `INFO` |
| Numerical errors (`NaN`) | `0` | `0` | `PASS` |

### Observed Distribution by Euphoria Plan (`NmPlanKind`)

| Plan | Count (out of 60) | Share |
|---|---:|---:|
| `Stagger` | 9 | 15.0% |
| `FallToKnees` | 7 | 11.7% |
| `RollDownStairs` | 7 | 11.7% |
| `Windmill` | 6 | 10.0% |
| `Collapse` | 5 | 8.3% |
| `Relax` | 5 | 8.3% |
| `TwistDown` | 5 | 8.3% |
| `HandsKnees` | 4 | 6.7% |
| `SitBack` | 4 | 6.7% |
| `KneeSlide` | 3 | 5.0% |
| `SideCrumple` | 3 | 5.0% |
| `Pivot` | 2 | 3.3% |

## Level 4 — Kinematics, ISB Anatomy, and BepInEx Configuration

Tests executed on `Core/BMath.cs`, `Core/JointKinematics.cs`, `Anatomy/AnatomyData.cs`, and `Settings.cs` with zero EFT/SPT dependencies.

| # | Domain | Check | Observed measurement | Status |
|---:|---|---|---|---|
| 1 | Knee | straight right knee = 0° | `compliant` | `PASS` |
| 2 | Knee | knee: lower leg backward = flexion +60° | `(60,0°)` | `PASS` |
| 3 | Knee | knee: hyperextension = negative | `compliant` | `PASS` |
| 4 | Knee | knee flexed at death: 60° read back | `compliant` | `PASS` |
| 5 | Knee | flexed knee: +10° backward = 70° | `compliant` | `PASS` |
| 6 | Elbow | elbow: forearm forward = flexion +90° | `(90,0°)` | `PASS` |
| 7 | Elbow | elbow: pronation-supination read on twist | `compliant` | `PASS` |
| 8 | Hip | neutral hip = 0/0/0 | `compliant` | `PASS` |
| 9 | Hip | hip: thigh forward = flexion +45° | `compliant` | `PASS` |
| 10 | Hip | right hip: spread outward = abduction +30° | `(30,0°)` | `PASS` |
| 11 | Hip | left hip: spread outward = abduction +30° | `(30,0°)` | `PASS` |
| 12 | Hip | hip: thigh backward = extension (negative) | `compliant` | `PASS` |
| 13 | Shoulder | shoulder: arm raised forward to 170° | `compliant` | `PASS` |
| 14 | Shoulder | shoulder: 190° read as 190° (not -170°) | `(190,0°)` | `PASS` |
| 15 | Shoulder | right shoulder: abduction +60° | `compliant` | `PASS` |
| 16 | Shoulder | left shoulder: arm toward midline = adduction (negative) | `compliant` | `PASS` |
| 17 | Internal rotation & pronation | right shoulder: internal rotation read ±30° | `(-30,0°)` | `PASS` |
| 18 | Internal rotation & pronation | internal rotation: sign = -side (right -, left +) | `(R -30,0°, L 30,0°)` | `PASS` |
| 19 | Internal rotation & pronation | hip: internal rotation, same convention as shoulder | `(R -30,0°, L 30,0°)` | `PASS` |
| 20 | Internal rotation & pronation | right elbow: pronation (palm inward then backward) = negative | `(-40,0°)` | `PASS` |
| 21 | Lumbar spine | neutral spine = 0/0/0 | `compliant` | `PASS` |
| 22 | Lumbar spine | spine: leaned forward = flexion +30° | `compliant` | `PASS` |
| 23 | Lumbar spine | spine: right lateral bend = +20° | `compliant` | `PASS` |
| 24 | Lumbar spine | spine: twist +25° read +25° | `compliant` | `PASS` |
| 25 | Lumbar spine | positive twist = left shoulder forward (roll to the right) | `compliant` | `PASS` |
| 26 | Lumbar spine | spine: twist at death read back (20°) | `compliant` | `PASS` |
| 27 | Neck | neck: twist +40° = looking right | `compliant` | `PASS` |
| 28 | Neck | neck: chin toward chest = flexion +30° | `compliant` | `PASS` |
| 29 | Numerical gradients | consistent numerical gradients (dφ = g·ω dt) | `(180/180)` | `PASS` |
| 30 | Inverse kinematics | IK: shoulder angles ↔ arm direction | `(flex 2°, abd 27°, elbow 99°)` | `PASS` |
| 31 | Inverse kinematics | IK: forearm reaches target | `compliant` | `PASS` |
| 32 | Inverse kinematics | IK: elbow flexed for close target | `compliant` | `PASS` |
| 33 | Inverse kinematics | leg IK L: leg reaches ankle target | `(hip 29°/2°, knee 19°)` | `PASS` |
| 34 | Inverse kinematics | leg IK L: knee flexed forward, thigh flexed | `compliant` | `PASS` |
| 35 | Inverse kinematics | leg IK R: leg reaches ankle target | `(hip 29°/2°, knee 19°)` | `PASS` |
| 36 | Inverse kinematics | leg IK R: knee flexed forward, thigh flexed | `compliant` | `PASS` |
| 37 | Inverse kinematics | leg IK: vertical stance = nearly straight leg | `(5,5°, 0,0°, 10,9°)` | `PASS` |
| 38 | Pose solver | pose solver: anatomical targets reached (< 0.5°) | `(240/240, worst 0,05°)` | `PASS` |
| 39 | Pose solver | pose solver: center pose for every joint | `compliant` | `PASS` |
| 40 | Stability, axes & thorax envelope | elbow folded across torso: positive flexion | `(114,9°)` | `PASS` |
| 41 | Stability, axes & thorax envelope | analytic JointAxes: flexion direction + orthogonality + shoulder 89.5° | `compliant` | `PASS` |
| 42 | Stability, axes & thorax envelope | SolvePose: shoulder at 200° returns through the front + normalized quaternion | `(final 15,0°, \|q\|=1,00000)` | `PASS` |
| 43 | Stability, axes & thorax envelope | StepCriticallyDamped: stable and overshoot-free from 60 Hz to 4 Hz | `compliant` | `PASS` |
| 44 | Stability, axes & thorax envelope | shoulder: adduction clamped at 0° along thorax, open in front (~-22°), tightened near head | `(0°:-0,0°, -30°:-0,0°, 90°:-22,0°, 175°:-8,3°)` | `PASS` |
| 45 | Stability, axes & thorax envelope | thorax envelope: detection, outward normals, and exterior projection | `(dSide=61cm, dFront=111cm)` | `PASS` |
| 46 | Stability, axes & thorax envelope | neck ROM: bounded extension, lateral tilt, and twist | `(flex [-28,42], lat ±30, twist ±55)` | `PASS` |
| 47 | Stability, axes & thorax envelope | shoulder ROM: clear of 180° wrap, 90° abduction pole, and bounded twist | `(flex [-40,155], abd [-22,78], twist ±48)` | `PASS` |
| 48 | BepInEx Configuration | configuration: valid sections and keys for BepInEx | `(38 names)` | `PASS` |

## See It Yourself in the 3D Studio (`demo/test-bench.html`)

The embedded 3D studio in the repository lets you visually reproduce each behaviour above in a browser, using the same logic as version `1.1.0`:

1. **Wall grab + break (`DlGrab`)** — Choose the **`Close right wall (grab)`** scene, **`Standing`** posture, **`Auto`** behaviour (or **`Side collapse`**), then click the mannequin's left flank to throw it toward the right: the right arm reaches toward the wall, the forearm pronates palm toward the surface, a yellow anchor point appears on contact, then snaps when the load exceeds the threshold.
2. **Crate / ledge grab (`GrabHelper`)** — Choose **`Crate / ledge ahead (grab)`**, **`Standing`** posture, and shoot the back or thorax to topple forward: probes detect the top edge of the crate in front of the body and lock a grip.
3. **1- or 2-leg buckling & back shot (`GiveWay`)** — Choose **`Standard room`**, orbit the camera behind the mannequin, and shoot the back or a thigh: the pelvis sinks without stiffening the legs, with visible flexion of one knee (leg shot) or both knees (simultaneous or staggered).
4. **Guard reflex (`upperBodyFlinch`) & "last stand"** — Choose **`Standard room`**, **`Standing`** posture, and click **`Burst ×5`** (**`B`** key) from the front: the arms rise to shield the upper body with forearm pronation while the trunk briefly resists before giving way.
5. **Momentum roll (`rollDownStairs` / `bodyRollUp`) and vanilla comparison** — Switch posture to **`Running (3.5 m/s)`** or **`Sprinting (5.5 m/s)`**, lower **`Simulation speed`** to **`0.25×`** and shoot the thorax to observe the roll and ground agony; enable **`Muscle target pose (ghost)`** or check **`Inert ragdoll, no Brutality (comparison)`** then click **`Replay`** on the same seed to compare directly against the inert ragdoll.

## Proof Scope and Transparency

| Formally proven out-of-game | Remaining to validate in-game (Unity / PhysX / SPT) |
|---|---|
| All Delirium mathematical laws (`nmrs`, `nmdbm`, `nmbb`, `nmshot`, `nmbr`, `nmfow`, `nmhf`, `nm.rs`) executed directly on the plugin's real `.cs` files. | Calibration of magnitudes on real Tarkov skeletons (`PhysxProbe` for drive/limit directions at raid start). |
| 60 Hz closed-loop physics (`Shot`, `Balance`, `WallBrace`/`FallOverWall`, `HighFall`, `OnFire`, `MeleeGuard`, `Grab`, `CatchFall`, `RollDownStairs`/`RollUp`, `Windmill`, `Writhe`, `Foetal`, `Flinch`). | Interactions on complex SPT map geometry (stairs, doors, walls, low ledges, furniture) and full carried equipment masses. |
| ISB conventions for all joints, arm/leg IK, `SolvePose` solver, thorax envelope, and validity of all BepInEx keys. | In-raid coexistence with third-party mods (`Smoothie`, `HollywoodFX`, `Fika`, `ReClassic`) and multi-body performance. |
