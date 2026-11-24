using System.Collections.Generic;
using System.Text;
using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Neural;
using EFT;
using UnityEngine;

namespace Brutality.Euphoria
{
    internal enum NmPhase { Balance, Fall, Ground }

    internal enum NmPlanKind
    {
        Auto, Stagger, FallToKnees, Collapse, Windmill, Relax, RollDownStairs,
        /// <summary>Seated collapse: hips flex, pelvis drops, then torso falls backward.</summary>
        SitBack,
        /// <summary>Side collapse: body falls sideways in the frontal plane.</summary>
        SideCrumple,
        /// <summary>Dive: torso pitches forward, arms reach for the ground.</summary>
        Dive,
        /// <summary>Board fall: stiff body and arched spine (posturing), falling backward.</summary>
        ArchFall,
        /// <summary>All-fours fall: knees then hands, before flattening out.</summary>
        HandsKnees,
        /// <summary>Knee slide: knees on the ground with preserved momentum, then face-first.</summary>
        KneeSlide,
        /// <summary>Pivot: body spins on itself before balance gives way.</summary>
        Pivot,
        /// <summary>Spin: torso twists, body spirals toward the ground.</summary>
        TwistDown,
    }

    /// <summary>Performance plan, chosen at death based on mechanism, posture, velocity, and shot direction.</summary>
    internal sealed class NmPlan
    {
        public NmPlanKind Kind;
        public bool Conscious, LegsDead, Reach, Fling, ShotFromBehind, ShotInGuts, WindmillBack = true, Snap, ShockSpin;
        /// <summary>The victim had momentum at the time of death: only a moving fall can roll.</summary>
        public bool Moving;
        /// <summary>Arms reach for the ground during the fall (catchFall).</summary>
        public bool Catch = true;
        /// <summary>Board fall: body stays stiff and spine arched (posturing).</summary>
        public bool StiffFall;
        /// <summary>Topple azimuth in the pelvis frame: 0 = forward, +π/2 = right, π = backward.</summary>
        public float ToppleAzimuth;
        /// <summary>Topple intensity (loss-of-balance torque).</summary>
        public float ToppleGain = 3f;
        /// <summary>Topple delay: body slumps first, then topples.</summary>
        public float ToppleDelay;
        /// <summary>Per-death modulation (0.8 to 1.25).</summary>
        public float Signature = 1f;
        public float RelaxTime = 0.08f, RbVel;
        public DlBalanceOptions Balance;
    }

    /// <summary>
    /// Euphoria performance: orchestrates the Delirium port behaviors in the order Delirium chains them
    /// (<c>bodyBalance</c> → fall <c>catchFall</c> / <c>rollDownStairs</c> → <c>injuredOnGround</c>,
    /// <c>bodyWrithe</c>, <c>bodyFoetal</c>), applies ballistic impulses and corrective forces, and
    /// flushes effectors once per step to Brutality's PhysX drives.
    ///
    /// It coexists with Brutality programs (cerebral posturing, locks, agony, breathing), which write into
    /// the same command buffer: Delirium provides the behavioral skeleton, Brutality the lesion timeline.
    /// </summary>
    internal sealed class NmPerformance
    {
        public readonly BodyModel Body;
        public readonly DeathContext Ctx;
        public readonly System.Random Rng;
        public readonly NmCharacter Ch;
        public readonly NmImpulseQueue Impulses;
        public readonly NmPlan Plan;
        public readonly List<NmHit> Wounds = new List<NmHit>(4);
        public readonly StringBuilder Journal = new StringBuilder();

        /// <summary>Delirium body and effectors (nmdbm::Body / nmrs::Character).</summary>
        public readonly DlBody Dl;
        public readonly DlCharacter Eff;

        public NmPhase Phase;
        public float GroundT = -1f, FallT = -1f, LastStandUntil = -1f, Time;

        public DlBalance Balance => _balance;
        public DlShot Shot => _shot;
        public DlCatchFall CatchFall => _catchFall;
        public DlRollDownStairs RollDownStairs => _rds;
        public DlWindmill Windmill => _windmill;
        public DlGrab Grab => _grab;
        public DlFlinch Flinch => _flinch;
        public DlWrithe Writhe => _writhe;
        public DlFoetal Foetal => _foetal;
        public DlInjuredOnGround InjuredOnGround => _iog;

        private readonly NeuralController _neural;
        private readonly DlShot _shot;
        private DlBalance _balance;
        private DlCatchFall _catchFall;
        private DlRollDownStairs _rds;
        private DlWindmill _windmill;
        private DlPedal _pedal;
        private DlGrab _grab;
        private DlFlinch _flinch;
        private DlInjuredOnGround _iog;
        private DlWrithe _writhe;
        private DlFoetal _foetal;
        private float _leak = 1f, _lastBulletT = -1f, _nextGrabProbe;
        private bool _lastStandLogged;
        // shotSnap and setCharacterDamping
        private float _snapT0 = -1f, _injArmT0 = -1f, _injArmSign, _toppleT0 = -1f, _toppleGain;
        private Vector3 _snapAxis, _toppleAxis;
        // Ground roll-over: body rolls onto its side or back during agony.
        private float _turnT0 = -1f, _turnDur = 1f, _turnGain;
        private Vector3 _turnAxis;
        private bool _snapDone, _unsnapDone;

        public NmPerformance(NeuralController neural, BodyModel body, DeathContext ctx, System.Random rng)
        {
            _neural = neural;
            Body = body;
            Ctx = ctx;
            Rng = rng;
            Ch = new NmCharacter(body);
            Ch.Sense(0f);
            Dl = new DlBody(body, Ch);
            Eff = new DlCharacter(body);
            Impulses = new NmImpulseQueue(body, Ch);
            Plan = ChoosePlan();
            Log(0f, "plan " + Plan.Kind + " (topple " + Mathf.RoundToInt(Plan.ToppleAzimuth * Mathf.Rad2Deg) + "°, force "
                + Plan.ToppleGain.ToString("0.0") + ")");
            Ch.Sense(0f);

            var sp = new DlShotParams
            {
                AddShockSpin = Plan.ShockSpin,
                Fling = Plan.Fling,
                ReachForWound = Plan.Reach,
                UseHeadLook = true,
                FallTime = Plan.Conscious ? 1.2f : 0.4f,
                SpinePainTime = Plan.Conscious ? DlCfg.ShotSpinePainTime : 0.2f,
            };
            _shot = new DlShot(this, 0f, sp);
            Phase = NmPhase.Balance;
            if (ctx.Pose == EPlayerPose.Prone)
            {
                Phase = NmPhase.Fall;
                StartGround(0f);
            }
            else if (Plan.Balance != null) _balance = new DlBalance(this, 0f, Plan.Balance);
            else StartFall(0f, "no balance");
        }

        public void Log(float t, string s)
        {
            if (!Settings.DebugLogging.Value) return;
            Journal.Append("\n  t=").Append(t.ToString("0.00")).Append("s Euphoria : ").Append(s);
        }

        /// <summary>Global strength (setCharacterHealth): life fades until the end of agony.</summary>
        public float U(float t)
        {
            if (!Plan.Conscious) return Mathf.Max(0f, 1f - t / Mathf.Max(0.05f, Plan.RelaxTime));
            if (GroundT < 0f) return t < _neural.MusclesCap - 1f ? 1f : Mathf.Clamp01(_neural.MusclesCap - t);
            float end = Mathf.Max(_neural.AgonyEnd, GroundT > 0f ? GroundT + 0.5f : 0f);
            end = Mathf.Min(end, _neural.MusclesCap);
            return t < end - 1f ? 1f : Mathf.Clamp01((end - t) / 1f);
        }

        // ------------------------------------------------------------------ plan

        private NmPlan ChoosePlan()
        {
            var c = Ctx;
            var rng = Rng;
            var m = c.Mechanism;
            var plan = new NmPlan();
            float speed = NmCharacter.Flat(c.Velocity).magnitude;
            var th = Body[Seg.Thorax];
            Vector3 fwd = BMath.SafeNormalize(NmCharacter.Flat(th.F), Vector3.forward);
            Vector3 d = NmCharacter.Flat(c.Direction);
            bool hasDir = d.sqrMagnitude > 0.01f;
            if (hasDir) d.Normalize();
            bool torso = c.BodyPart == EBodyPart.Chest || c.BodyPart == EBodyPart.Stomach;
            bool fromBehind = hasDir && Vector3.Dot(d, fwd) > 0.4f;
            plan.Conscious = m != DeathMechanism.Cerebral && m != DeathMechanism.CervicalCord && m != DeathMechanism.Blast && m != DeathMechanism.FallTrauma;
            plan.LegsDead = m == DeathMechanism.CervicalCord || m == DeathMechanism.ThoracicCord || m == DeathMechanism.LumbarCord;
            // Brain destroyed: tone drops in a few tens of ms — blackout then instant collapse.
            plan.RelaxTime = m == DeathMechanism.Cerebral ? BMath.Range(rng, 0.02f, 0.06f) : 0.08f;
            plan.Reach = plan.Conscious && c.BodyPart != EBodyPart.Head && BMath.Chance(rng, 0.85f);
            plan.Fling = plan.Conscious && BMath.Chance(rng, 0.55f + 0.2f * Mathf.Min(1f, c.KickScale - 1f));
            // The "shot from behind" reaction is physical, not a roll: it governs spine curl, blocked step,
            // and buckling knees, which must be consistent on every back shot.
            plan.ShotFromBehind = plan.Conscious && fromBehind && torso;
            plan.Moving = speed > 0.8f;
            plan.ShotInGuts = m == DeathMechanism.Abdominal;
            int legHit = LegHit(c.ColliderType);

            // Azimuth of the projectile push in the horizontal plane: a body losing tone falls along the
            // shot axis. This is the reference fall direction, which each plan tilts or overrides.
            float pushAz = hasDir ? Mathf.Atan2(Vector3.Dot(d, NmCharacter.Flat(th.R)), Vector3.Dot(d, fwd)) : 0f;
            float jitter = BMath.Range(rng, -0.55f, 0.55f);
            int side = BMath.Chance(rng, 0.5f) ? -1 : 1;
            plan.Signature = BMath.Range(rng, 0.8f, 1.25f);
            plan.Catch = plan.Conscious && !plan.LegsDead;

            var kind = ParseForced(Settings.EuphoriaForce.Value);
            if (kind == NmPlanKind.Auto)
            {
                if (!plan.Conscious || plan.LegsDead) kind = NmPlanKind.Relax;
                else if (speed > 2.4f) kind = NmPlanKind.RollDownStairs;
                else if (speed > 1.8f) kind = NmPlanKind.Collapse;
                else
                {
                    // Rolling was previously only reachable while running: on a standing death, the body still has shot
                    // momentum and can roll. Delirium does not decide the message (the "shot" task chooses it): so this
                    // is indeed a plan setting, not a physical law.
                    float v = Settings.GestureVariety.Value;
                    // Base plans.
                    float wS = 2.4f, wF = 1.7f, wC = 1.1f, wW = 0.75f * v, wR = 0.65f * v;
                    // Added plans: falls cover all three anatomical planes and intermediate positions.
                    // Seated collapse stays rare: the pelvis gives way on a gut shot, it shouldn't sit down every death.
                    float wSit = 0.55f, wSide = 1.5f, wDive = 1.1f;
                    // Board fall is a posturing fall: outside destroyed brain, it must remain exceptional.
                    float wArch = (m == DeathMechanism.Cerebral ? 1.5f : 0.35f) * Mathf.Clamp(Settings.PosturingIntensity.Value, 0.4f, 2f);
                    float wKnees = 1.2f, wSlide = 0.85f * v, wPivot = 1f, wTwist = 1f;
                    if (m == DeathMechanism.Abdominal) { wF *= 1.5f; wSit *= 1.8f; }
                    if (m == DeathMechanism.Syncope) { wS = 0.4f; wF *= 1.4f; wW = 0f; wR *= 0.5f; wPivot *= 0.6f; wSit *= 1.4f; }
                    if (c.Pose == EPlayerPose.Duck) { wF *= 2f; wS *= 0.4f; wW *= 0.4f; wR *= 1.4f; wKnees *= 1.5f; wSit *= 1.6f; }
                    if (legHit != 0) { wC *= 2f; wS *= 0.7f; wR *= 0.4f; wSide *= 1.8f; wSit *= 0.7f; }
                    if (hasDir && Vector3.Dot(d, fwd) < -0.4f) { wW *= 2f; wR *= 1.4f; wArch *= 1.6f; }
                    if (hasDir && Vector3.Dot(d, fwd) > 0.4f) { wDive *= 1.5f; wKnees *= 1.4f; wSlide *= 1.3f; }
                    // Heavy projectiles topple and roll more readily ("rigid body" push).
                    if (c.KickScale > 1.2f) { wR *= 1.5f; wDive *= 1.2f; wPivot *= 1.2f; }
                    // A moving body pivots more readily than collapsing in place.
                    if (plan.Moving) { wPivot *= 1.6f; wTwist *= 1.5f; wSlide *= 1.3f; }
                    // A shot in the back does not make a standing body roll: the fall is a topple
                    // onto the knees, not a somersault. Rolling remains possible if the target was already running.
                    if (plan.ShotFromBehind && !plan.Moving) wR = 0f;

                    var kinds = new[]
                    {
                        NmPlanKind.Stagger, NmPlanKind.FallToKnees, NmPlanKind.Collapse, NmPlanKind.Windmill,
                        NmPlanKind.RollDownStairs, NmPlanKind.SitBack, NmPlanKind.SideCrumple, NmPlanKind.Dive,
                        NmPlanKind.ArchFall, NmPlanKind.HandsKnees, NmPlanKind.KneeSlide, NmPlanKind.Pivot, NmPlanKind.TwistDown,
                    };
                    var weights = new[] { wS, wF, wC, wW, wR, wSit, wSide, wDive, wArch, wKnees, wSlide, wPivot, wTwist };
                    float sum = 0f;
                    for (int i = 0; i < weights.Length; i++)
                    {
                        // Death memory: a recently seen plan weighs much less, and a fall in the same
                        // direction as the previous death even less (otherwise all bodies topple the same way).
                        weights[i] = Mathf.Max(0f, weights[i] * VarietyDirector.PlanMultiplier((int)kinds[i])
                            * VarietyDirector.SectorMultiplier(BaseAzimuth(kinds[i], pushAz, side)));
                        sum += weights[i];
                    }
                    float r = (float)rng.NextDouble() * Mathf.Max(1e-4f, sum);
                    kind = NmPlanKind.Collapse;
                    for (int i = 0; i < weights.Length; i++)
                    {
                        r -= weights[i];
                        if (r <= 0f && weights[i] > 0f) { kind = kinds[i]; break; }
                    }
                }
            }
            plan.Kind = kind;
            ConfigurePlan(plan, kind, pushAz, jitter, legHit, side, rng, c.Pose == EPlayerPose.Duck);
            if (plan.Balance != null)
            {
                if (c.Pose == EPlayerPose.Duck) plan.Balance.Sink0 = Mathf.Max(plan.Balance.Sink0, 0.25f);
                if (plan.Balance.InjuredLeg == 0) plan.Balance.InjuredLeg = legHit;
                if (plan.ShotFromBehind)
                {
                    plan.Balance.DontStepTime = 0.3f;
                    plan.Balance.GiveWay = 1f;
                    if (plan.Balance.BuckleMode == 0)
                    {
                        plan.Balance.BuckleMode = 3; // asymmetric buckling of both legs on a shot from behind
                        plan.Balance.BuckleLeg = side;
                        plan.Balance.BuckleDelay = 0.05f;
                        plan.Balance.BuckleStaggerDelay = BMath.Range(rng, 0.08f, 0.16f);
                    }
                }
                else if (legHit != 0 && plan.Balance.BuckleMode == 0)
                {
                    // Leg shot: clean buckling of the hit leg (1 leg gives way).
                    plan.Balance.BuckleMode = 1;
                    plan.Balance.BuckleLeg = legHit;
                    plan.Balance.BuckleDelay = 0.06f;
                }
                // Delirium leanInDirection: balancer lean steers stagger steps along the plan axis.
                if (plan.Balance.LeanAmount <= 0f && !plan.Balance.Sit)
                {
                    plan.Balance.LeanDir = ToppleDir(plan.ToppleAzimuth);
                    plan.Balance.LeanAmount = kind == NmPlanKind.Stagger ? BMath.Range(rng, 0.16f, 0.28f)
                        : kind == NmPlanKind.SideCrumple ? BMath.Range(rng, 0.18f, 0.30f)
                        : BMath.Range(rng, 0.10f, 0.20f);
                    plan.Balance.LeanAsForce = BMath.Chance(rng, 0.55f);
                }
            }
            float kindScale = kind == NmPlanKind.Relax ? 0.25f
                : kind == NmPlanKind.FallToKnees || kind == NmPlanKind.Collapse || kind == NmPlanKind.SitBack
                  || kind == NmPlanKind.HandsKnees || kind == NmPlanKind.KneeSlide ? 0.45f : 1f;
            plan.RbVel = kindScale * Mathf.Clamp(0.25f + 0.12f * Mathf.Max(1f, c.ImpactMomentum), 0.3f, 1.4f) * c.KickScale * Settings.FallDrive.Value;
            if (kind == NmPlanKind.Windmill) plan.RbVel = Mathf.Max(plan.RbVel, 0.9f * Settings.FallDrive.Value);
            string kc = c.KickClass ?? string.Empty;
            bool heavy = kc.Contains("buckshot") || kc.Contains("slug") || kc.Contains("heavy");
            plan.Snap = c.KickScale > 1.35f || heavy;
            plan.ShockSpin = heavy && torso;
            VarietyDirector.RecordPlan((int)kind, plan.ToppleAzimuth);
            return plan;
        }

        /// <summary>
        /// Nominal fall azimuth of a plan (without ConfigurePlan's random jitter), in the body frame:
        /// 0 = forward, ±π/2 = side, π = backward. Used to avoid plans that replay the previous death's direction.
        /// </summary>
        private static float BaseAzimuth(NmPlanKind kind, float pushAz, int side)
        {
            switch (kind)
            {
                case NmPlanKind.FallToKnees:
                case NmPlanKind.KneeSlide:
                case NmPlanKind.HandsKnees:
                case NmPlanKind.Dive:
                case NmPlanKind.RollDownStairs:
                    return 0f;
                case NmPlanKind.SitBack:
                case NmPlanKind.ArchFall:
                case NmPlanKind.Windmill:
                    return Mathf.PI;
                case NmPlanKind.SideCrumple:
                case NmPlanKind.TwistDown:
                    return side * Mathf.PI * 0.5f;
                default:
                    return pushAz;
            }
        }

        /// <summary>
        /// Per-plan configuration: balancer options, topple direction and strength, arm ground-reach,
        /// board stiffness. This is where the fall is oriented in the frontal, sagittal, or transverse
        /// plane, instead of always being the same forward topple.
        /// </summary>
        private void ConfigurePlan(NmPlan plan, NmPlanKind kind, float pushAz, float jitter, int legHit, int side, System.Random rng, bool duck)
        {
            float R(float a, float b) => BMath.Range(rng, a, b);
            plan.ToppleAzimuth = pushAz + jitter;
            plan.ToppleGain = 3f;

            switch (kind)
            {
                case NmPlanKind.Relax:
                    // Already dead body: slumps along the push axis, then topples gently.
                    plan.ToppleAzimuth = pushAz + R(-0.7f, 0.7f);
                    plan.ToppleGain = R(1.4f, 3.2f);
                    plan.ToppleDelay = plan.RelaxTime + R(0.02f, 0.12f);
                    plan.Catch = false;
                    break;

                case NmPlanKind.Stagger:
                    plan.Balance = new DlBalanceOptions
                    {
                        Stagger = true, MaxSteps = rng.Next(2, 6), MaxTime = R(2.6f, 3.6f), PerStep = R(1.2f, 2.1f),
                        BuckleMode = legHit != 0 ? 1 : (BMath.Chance(rng, 0.35f) ? 1 : 0),
                        BuckleLeg = legHit != 0 ? legHit : side,
                        BuckleDelay = R(0.28f, 0.75f),
                    };
                    plan.ToppleAzimuth = pushAz + jitter;
                    plan.ToppleGain = R(1.6f, 2.8f);
                    break;

                case NmPlanKind.FallToKnees:
                    plan.Balance = new DlBalanceOptions
                    {
                        FallToKnees = true, MaxSteps = 4, MaxTime = R(2.2f, 3f), KneelHold = R(0.3f, 1.1f),
                        BuckleMode = BMath.Chance(rng, 0.55f) ? 3 : 2,
                        BuckleLeg = legHit != 0 ? legHit : side,
                        BuckleDelay = R(0.15f, 0.40f),
                        BuckleStaggerDelay = R(0.10f, 0.22f),
                    };
                    plan.ToppleAzimuth = R(-0.55f, 0.55f);
                    plan.ToppleGain = R(3.0f, 4.6f);
                    break;

                case NmPlanKind.KneeSlide:
                    // Knees on ground, momentum preserved: body slides then pitches face-first.
                    plan.Balance = new DlBalanceOptions
                    {
                        FallToKnees = true, MaxSteps = 3, MaxTime = R(1.5f, 2.4f), KneelHold = R(0.12f, 0.4f),
                        BuckleMode = 2, BuckleDelay = R(0.08f, 0.22f),
                    };
                    plan.ToppleAzimuth = R(-0.3f, 0.3f);
                    plan.ToppleGain = R(4.2f, 5.8f);
                    plan.Catch = BMath.Chance(rng, 0.45f);
                    break;

                case NmPlanKind.HandsKnees:
                    plan.Balance = new DlBalanceOptions
                    {
                        FallToKnees = true, MaxSteps = 4, MaxTime = R(2.6f, 3.4f), KneelHold = R(0.6f, 1.4f),
                        BuckleMode = 3, BuckleLeg = side, BuckleDelay = R(0.12f, 0.32f), BuckleStaggerDelay = R(0.12f, 0.24f),
                    };
                    plan.ToppleAzimuth = R(-0.4f, 0.4f);
                    plan.ToppleGain = R(2.6f, 4.0f);
                    break;

                case NmPlanKind.Collapse:
                    plan.Balance = new DlBalanceOptions
                    {
                        MaxSteps = rng.Next(1, 3), MaxTime = R(0.6f, 1.4f),
                        BuckleMode = rng.Next(1, 4), // 1 leg, 2 legs together, or 2 legs staggered
                        BuckleLeg = legHit != 0 ? legHit : side,
                        BuckleDelay = R(0.05f, 0.18f),
                        BuckleStaggerDelay = R(0.10f, 0.22f),
                    };
                    plan.ToppleAzimuth = pushAz + R(-1.2f, 1.2f);
                    plan.ToppleGain = R(2.0f, 3.4f);
                    break;

                case NmPlanKind.Windmill:
                    plan.Balance = new DlBalanceOptions { Stagger = true, MaxSteps = rng.Next(1, 3), MaxTime = 1.6f, PerStep = 2.2f };
                    plan.ToppleAzimuth = Mathf.PI + R(-0.45f, 0.45f);
                    plan.ToppleGain = R(3.8f, 5.2f);
                    break;

                case NmPlanKind.RollDownStairs:
                    // Brief moment of balance: body leans under the impact, then rolling takes over.
                    plan.Balance = new DlBalanceOptions { MaxSteps = 1, MaxTime = R(0.12f, 0.35f) };
                    plan.ToppleAzimuth = R(-0.3f, 0.3f);
                    plan.ToppleGain = R(3.8f, 5f);
                    break;

                case NmPlanKind.SitBack:
                    // Pelvis gives way: body sits down, then topples onto its back.
                    plan.Balance = new DlBalanceOptions { Sit = true, MaxSteps = 1, MaxTime = R(0.4f, 0.85f), BuckleMode = 2, BuckleDelay = 0.06f };
                    plan.ToppleAzimuth = Mathf.PI + R(-0.5f, 0.5f);
                    plan.ToppleGain = R(2f, 3.4f);
                    break;

                case NmPlanKind.SideCrumple:
                    plan.Balance = new DlBalanceOptions
                    {
                        MaxSteps = rng.Next(1, 3), MaxTime = R(0.55f, 1.1f), Sink0 = 0.1f,
                        LeanSide = legHit != 0 ? legHit : side,
                        InjuredLeg = legHit != 0 ? legHit : side,
                        BuckleMode = 1, // one leg gives way on the topple side
                        BuckleLeg = legHit != 0 ? legHit : side,
                        BuckleDelay = R(0.04f, 0.14f),
                    };
                    plan.ToppleAzimuth = plan.Balance.LeanSide * (Mathf.PI * 0.5f) + R(-0.35f, 0.35f);
                    plan.ToppleGain = R(2.8f, 4.2f);
                    plan.Catch = BMath.Chance(rng, 0.5f);
                    break;

                case NmPlanKind.Dive:
                    // No balance phase: torso pitches forward and arms reach for the ground.
                    plan.ToppleAzimuth = R(-0.28f, 0.28f);
                    plan.ToppleGain = R(6f, 8f);
                    break;

                case NmPlanKind.ArchFall:
                    // Board fall: spine arches and body falls backward, stiff.
                    plan.ToppleAzimuth = Mathf.PI + R(-0.3f, 0.3f);
                    plan.ToppleGain = R(2.8f, 4f);
                    plan.StiffFall = true;
                    plan.Catch = false;
                    plan.WindmillBack = false; // a stiff board does not windmill its arms
                    break;

                case NmPlanKind.Pivot:
                    plan.Balance = new DlBalanceOptions
                    {
                        Stagger = true, MaxSteps = 1, MaxTime = R(0.45f, 0.9f), PerStep = 2f,
                        YawAccel = (BMath.Chance(rng, 0.5f) ? -1f : 1f) * R(6f, 11f),
                    };
                    plan.ToppleAzimuth = pushAz + R(-1.4f, 1.4f);
                    plan.ToppleGain = R(2.6f, 4f);
                    break;

                case NmPlanKind.TwistDown:
                    plan.Balance = new DlBalanceOptions
                    {
                        MaxSteps = 1, MaxTime = R(0.35f, 0.8f),
                        YawAccel = (BMath.Chance(rng, 0.5f) ? -1f : 1f) * R(4f, 8f),
                    };
                    plan.ToppleAzimuth = (BMath.Chance(rng, 0.5f) ? -1f : 1f) * (Mathf.PI * 0.5f) + R(-0.5f, 0.5f);
                    plan.ToppleGain = R(3.2f, 4.8f);
                    break;
            }

            if (duck && plan.Balance != null && plan.Balance.Sink0 < 0.25f) plan.Balance.Sink0 = 0.2f;
            // Topple follows the "Directed fall drive" setting and this death's unique signature.
            plan.ToppleGain = Mathf.Max(0f, plan.ToppleGain * Settings.FallDrive.Value * plan.Signature);
        }

        private static NmPlanKind ParseForced(string v)
        {
            switch (v)
            {
                case "Stagger": return NmPlanKind.Stagger;
                case "Fall to knees": return NmPlanKind.FallToKnees;
                case "Balance then collapse": return NmPlanKind.Collapse;
                case "Windmill": return NmPlanKind.Windmill;
                case "Slack collapse": return NmPlanKind.Relax;
                case "Roll": return NmPlanKind.RollDownStairs;
                case "Seated collapse": return NmPlanKind.SitBack;
                case "Side collapse": return NmPlanKind.SideCrumple;
                case "Dive": return NmPlanKind.Dive;
                case "Board fall": return NmPlanKind.ArchFall;
                case "All fours": return NmPlanKind.HandsKnees;
                case "Knee slide": return NmPlanKind.KneeSlide;
                case "Pivot": return NmPlanKind.Pivot;
                case "Spin": return NmPlanKind.TwistDown;
                default: return NmPlanKind.Auto;
            }
        }

        private static int LegHit(EBodyPartColliderType t)
        {
            switch (t)
            {
                case EBodyPartColliderType.LeftThigh: case EBodyPartColliderType.LeftCalf: return -1;
                case EBodyPartColliderType.RightThigh: case EBodyPartColliderType.RightCalf: return 1;
            }
            return 0;
        }

        // ------------------------------------------------------------------ bullets

        /// <summary>
        /// New projectile: spread ballistic impulse, "rigid body" share, Delirium shot reaction (looseness,
        /// react, reach for wound, fling arm, collapse leg, shock spin), and under burst fire the last
        /// stand (stayUpright lastStandMode).
        /// </summary>
        public void OnBullet(float t, NmHit hit)
        {
            Wounds.Add(hit);
            bool falling = Phase != NmPhase.Balance;
            _shot.NewHit(t, hit, falling);
            if (_lastBulletT >= 0f) _leak = Mathf.Min(1f, _leak + 0.5f * (t - _lastBulletT));
            float leak = Mathf.Max(0.2f, _leak);
            _leak = Mathf.Max(0.2f, _leak - 0.25f);
            _lastBulletT = t;
            Vector3 rbDir = hit.Dir;
            rbDir.y = Mathf.Max(0f, rbDir.y);
            rbDir = BMath.SafeNormalize(rbDir, Ch.Fwd);
            Vector3 rb = rbDir * (Body.TotalMass * Plan.RbVel * leak * (Wounds.Count == 1 ? 1f : 0.6f) * (falling ? 0.5f : 1f));
            Impulses.Queue(t, hit.Part, hit.World, hit.Dir * hit.P, rb);
            // Headshot: the projectile imparts a torque to the head around the neck, not just translation.
            // Without this torque, the trunk moves before the head and the relaxed neck leaves it behind (extension),
            // as if the bullet came from the front. The torque closes on the thorax: no net angular momentum is added.
            if (hit.Part.Id == Seg.Head)
            {
                Vector3 ax = Vector3.Cross(hit.Part.U, hit.Dir);
                if (ax.sqrMagnitude > 1e-6f)
                {
                    ax.Normalize();
                    float dw = Mathf.Clamp(hit.P / 3f, 0.4f, 2f) * NmBullets.HeadWhip;
                    var hb = hit.Part.Rb;
                    var tb = Body[Seg.Thorax].Rb;
                    hb.angularVelocity += ax * dw;
                    float ih = (hb.inertiaTensor.x + hb.inertiaTensor.y + hb.inertiaTensor.z) / 3f;
                    float it = (tb.inertiaTensor.x + tb.inertiaTensor.y + tb.inertiaTensor.z) / 3f;
                    tb.angularVelocity -= ax * (dw * ih / Mathf.Max(1e-4f, it));
                }
            }
            if (Plan.Snap) { _snapT0 = t; _snapDone = false; _unsnapDone = false; }
            if (hit.ArmSide != 0)
            {
                _injArmT0 = t;
                float sgn = Mathf.Sign(Vector3.Cross(hit.World - Ch.Com, hit.Dir).y);
                _injArmSign = sgn == 0f ? 1f : sgn;
            }
            if (_balance != null && _balance.Active && !_balance.Failed && Wounds.Count > 1)
            {
                LastStandUntil = t + 0.4f;
                _balance.MaxTime += 0.4f;
                _balance.MaxSteps += 1;
                _balance.Sink = Mathf.Min(0.3f, _balance.Sink + 0.024f);
                if (!_lastStandLogged) { _lastStandLogged = true; Log(t, "stayUpright (lastStandMode)"); }
            }
            // upperBodyFlinch (Delirium nmfl): protective reflex in front of face/chest on frontal shot or burst.
            bool frontal = Vector3.Dot(hit.Dir, Ch.Fwd) < -0.25f;
            if (_flinch == null && Plan.Conscious && !Plan.StiffFall && Phase != NmPhase.Ground && frontal
                && (Wounds.Count > 1 || BMath.Chance(Rng, 0.25f * Mathf.Clamp01(Settings.GestureVariety.Value))))
            {
                Vector3 threat = hit.World - hit.Dir * 2f;
                _flinch = new DlFlinch(this, t, threat, BMath.Range(Rng, 0.45f, 0.75f));
            }
        }

        public void OnKneesImpact(float t) => Log(t, "knees on ground");

        /// <summary>Additional support force when the knees bear the weight (shotFallToKnees).</summary>
        public void OnKneeSupport(float t, float dt, float s)
        {
            if (s <= 0.01f) return;
            var pb = Body[Seg.Pelvis];
            var th = Body[Seg.Thorax];
            float f = Physics.gravity.magnitude * 0.35f * s * Mathf.Max(20f, Body.TotalMass);
            pb.Rb.AddForce(Vector3.up * (f * 0.6f), ForceMode.Force);
            th.Rb.AddForce(Vector3.up * (f * 0.4f), ForceMode.Force);
        }

        // ------------------------------------------------------------------ phases

        private void StartWindmill(float t)
        {
            if (_windmill != null) return;
            _windmill = new DlWindmill(this, t);
        }

        private void StartFall(float t, string why)
        {
            if (Phase != NmPhase.Balance) return;
            Phase = NmPhase.Fall;
            FallT = t;
            Log(t, "fall (" + why + ")");
            _balance?.Fail(t, why);
            // Delirium chains bodyBalance → fall: the shot fall sub-behavior starts here.
            _shot.Fallen = true;

            // Each plan falls in its anatomical plane: forward, backward, side, or diagonal. The topple is a bounded
            // whole-body torque applied over 0.45 s. It decides the fall direction, instead of an identical
            // forward topple on every death.
            Vector3 dir = ToppleDir(Plan.ToppleAzimuth);
            StartTopple(t + Plan.ToppleDelay, dir, Plan.ToppleGain);

            Vector3 vH = NmCharacter.Flat(Ch.Vel);
            bool back = Vector3.Dot(dir, BMath.SafeNormalize(NmCharacter.Flat(Ch.Fwd), Vector3.forward)) < -0.35f;
            // A standing fall that topples does not become a roll: momentum at death is required.
            // A standing fall that topples does not become a roll: momentum is required (and a dive stays a catchFall).
            if (Plan.Kind == NmPlanKind.RollDownStairs || (Plan.Kind != NmPlanKind.Dive && Plan.Moving && vH.magnitude > 1.6f))
            {
                Vector3 rollDir = vH.sqrMagnitude > 0.04f ? vH.normalized : dir;
                _rds = new DlRollDownStairs(this, t, rollDir);
            }
            else
            {
                if (back && Plan.WindmillBack && Plan.Conscious) StartWindmill(t);
                // Immediately probes surrounding scenery (wall, crate, ledge) to grab before falling.
                if (Plan.Conscious && !Plan.StiffFall) TryStartGrab(t, dir);
                // A conscious body catches itself; an already-dead body falls limp (catchFall only writes at U≈0).
                if (Plan.Conscious && !Plan.LegsDead && Plan.Catch) _catchFall = new DlCatchFall(this, t);
            }
        }

        private void TryStartGrab(float t, Vector3 fallHint)
        {
            if (_grab != null && _grab.Active) return;
            if (!Plan.Conscious || Plan.StiffFall || _rds != null) return;
            if (DlGrab.ProbeEnvironment(Dl, Ch, fallHint, out DlGrabTarget target))
            {
                _grab = new DlGrab(this, t, target,
                    DlCfg.GrabStrength * BMath.Range(Rng, 0.8f, 1.25f),
                    BMath.Range(Rng, 0.9f, DlCfg.GrabHoldMaxTime));
                Log(t, $"grab detected ({(target.IsLedge ? "ledge" : "wall/obstacle")})");
            }
        }

        /// <summary>Topple direction in the horizontal plane: azimuth 0 = forward, +π/2 = right, π = backward.</summary>
        private Vector3 ToppleDir(float azimuth)
        {
            Vector3 f = NmCharacter.Flat(Ch.Fwd), r = NmCharacter.Flat(Ch.Right);
            f = BMath.SafeNormalize(f, Vector3.forward);
            r = r.sqrMagnitude > 1e-4f ? r.normalized : Vector3.Cross(Vector3.up, f);
            return BMath.SafeNormalize(f * Mathf.Cos(azimuth) + r * Mathf.Sin(azimuth), f);
        }

        /// <summary>Arms the loss-of-balance topple around the axis perpendicular to the chosen direction.</summary>
        private void StartTopple(float t, Vector3 dir, float gain)
        {
            Vector3 h = NmCharacter.Flat(dir);
            h = h.sqrMagnitude > 1e-4f ? h.normalized : Ch.Fwd;
            _toppleAxis = Vector3.Cross(Vector3.up, h);
            _toppleGain = Mathf.Max(0f, gain);
            _toppleT0 = t;
        }

        private void StartGround(float t)
        {
            if (Phase == NmPhase.Ground) return;
            Phase = NmPhase.Ground;
            GroundT = t;
            Log(t, "on ground");
            _catchFall?.Stop(t);
            _windmill?.Stop(t);
            _balance?.Stop(t);
            _pedal?.Stop(t);
            // Roll: Delirium keeps rollDownStairs active as long as the body is spinning, instead of stopping at impact.
            if (_rds == null && Plan.Conscious)
            {
                Vector3 vH = NmCharacter.Flat(Ch.Vel);
                bool tumble = Plan.Moving && (vH.magnitude > 1.4f || Body[Seg.Thorax].W.magnitude > 3f);
                if (tumble)
                {
                    _rds = new DlRollDownStairs(this, t, vH.sqrMagnitude > 0.01f ? vH.normalized : Ch.Fwd, 2.4f);
                    Log(t, "ground roll (momentum)");
                }
            }
            var m = Ctx.Mechanism;
            if (Plan.Conscious)
            {
                _iog = new DlInjuredOnGround(this, t);
                _writhe = new DlWrithe(this, t, (m == DeathMechanism.Burn ? 1.2f : m == DeathMechanism.Abdominal ? 0.6f : 0.75f)
                * Settings.SpasmVigor.Value * BMath.Range(Rng, 0.8f, 1.25f));
                if (m == DeathMechanism.Abdominal || m == DeathMechanism.Burn
                    || BMath.Chance(Rng, 0.15f + 0.3f * Mathf.Clamp01(Settings.GestureVariety.Value)))
                    _foetal = new DlFoetal(this, t);
            }
            _toppleT0 = -1f;
            _neural.OnNmGround(t);

            // Ground signature: body can roll onto its side or turn over during agony, instead of
            // remaining in the orientation it had when touching the ground.
            if (Plan.Conscious && BMath.Chance(Rng, 0.3f + 0.25f * Mathf.Clamp01(Settings.GestureVariety.Value)))
            {
                _turnT0 = t + BMath.Range(Rng, 0.25f, 1.6f);
                _turnDur = BMath.Range(Rng, 0.7f, 1.4f);
                _turnAxis = BMath.Chance(Rng, 0.65f) ? NmCharacter.Flat(Ch.Fwd) : Vector3.up;
                _turnGain = BMath.Range(Rng, 2f, 4.2f) * (BMath.Chance(Rng, 0.5f) ? 1f : -1f) * Settings.FallDrive.Value;
            }
        }

        public void Update(NeuralState ns, float t, float dt)
        {
            Time = t;
            Ch.Sense(t);
            float w = U(t);

            // Exact Delirium execution order (nm.rs:1449-1824):
            //   1. DynamicBalanceManager + BodyBalance (legs + upper-body balancer)
            //   2. Shot (looseness, spine pain, reach for wound, fling arm, collapse leg)
            //      → runs AFTER Balance so Balance never overwrites Shot effectors!
            //   3. UpperBodyFlinch / fall behaviors (catchFall, rollDownStairs+rollUp, armsWindmill, pedalLegs)
            //   4. Grab (scenery grip, takes priority over arms it locks via Ch.Busy)
            //   5. Ground behaviors (injuredOnGround, bodyWrithe, bodyFoetal, residual rollDownStairs)
            if (Phase == NmPhase.Balance)
            {
                if (_balance == null || !_balance.Active || _balance.Failed)
                    StartFall(t, _balance != null ? (_balance.Why.Length > 0 ? _balance.Why : "end") : "relaxation");
                else
                {
                    _balance.Tick(ns, t, dt);
                    if (_balance.Failed) StartFall(t, _balance.Why.Length > 0 ? _balance.Why : "imbalance");
                }
                if (Phase == NmPhase.Balance && Plan.Kind == NmPlanKind.Windmill && t > 0.08f) StartWindmill(t);
                // Probe scenery during stagger/buckling or as soon as torso leans to grab a nearby wall or crate.
                if (Phase == NmPhase.Balance && t >= _nextGrabProbe && (_balance != null && (_balance.Steps > 0 || _balance.LegBuckle[0] > 0.2f || _balance.LegBuckle[1] > 0.2f || Ch.Vel.magnitude > 0.7f || Ch.Hip2HeadUp < 0.96f)))
                {
                    _nextGrabProbe = t + 0.14f;
                    TryStartGrab(t, Ch.Vel.sqrMagnitude > 0.04f ? Ch.Vel : ToppleDir(Plan.ToppleAzimuth));
                }
            }

            if (Phase != NmPhase.Ground) _shot.Tick(t, dt, w);

            if (_flinch != null && _flinch.Active && Phase != NmPhase.Ground)
                _flinch.Tick(t, dt, w);

            // armsWindmill can start during Balance (Windmill plan) and continues during Fall.
            if (Phase == NmPhase.Balance && _windmill != null && _windmill.Active)
                _windmill.Tick(t, dt, Mathf.Max(w, 0.2f));

            if (Phase == NmPhase.Fall)
            {
                float backV = -Vector3.Dot(NmCharacter.Flat(Ch.Vel), Ch.Fwd);
                if (_windmill == null && _rds == null && Plan.Conscious && Plan.WindmillBack && backV > 0.5f) StartWindmill(t);
                if (Ch.Airborne && _pedal == null && t - FallT > 0.25f && Plan.Conscious) _pedal = new DlPedal(this, t);
                if (_grab == null && t >= _nextGrabProbe && t - FallT < 0.75f)
                {
                    _nextGrabProbe = t + 0.12f;
                    TryStartGrab(t, Ch.Vel.sqrMagnitude > 0.04f ? Ch.Vel : ToppleDir(Plan.ToppleAzimuth));
                }

                _catchFall?.Tick(t, dt, w);
                _windmill?.Tick(t, dt, Mathf.Max(w, 0.2f));
                _rds?.Tick(t, dt, w);
                _pedal?.Tick(t, dt, Ch.Airborne ? w : w * 0.3f);

                bool slow = Ch.Vel.magnitude < (_rds != null ? 0.7f : 0.9f);
                bool low = (Body[Seg.Thorax].Com.y - Ch.GroundY < 0.6f && t - FallT > 1f) || t - FallT > 1.2f;
                if (((Ch.Lying || low) && slow) || t - FallT > (_rds != null ? 3.5f : 2.6f)) StartGround(t);
            }

            // Grab runs after catchFall / shot (like nm.rs:1816-1820) and can slide briefly onto the ground.
            if (_grab != null && _grab.Active)
            {
                if (Phase == NmPhase.Ground && t - GroundT > 0.65f) _grab.Stop(t);
                else _grab.Tick(t, dt, w);
            }

            if (Phase == NmPhase.Ground)
            {
                float g = BMath.SmoothStep(GroundT, GroundT + 0.4f, t);
                float I = ns.Agony;
                float ig = Mathf.Max(I, Plan.Conscious ? 0.35f * w : 0f) * g;
                _iog?.Tick(t, ig, w);
                _writhe?.Tick(t, dt, ig * 0.7f, _iog != null ? 0.4f : 1f);
                _foetal?.Tick(t, ig * 0.45f, w);
                // Roll runs last: as long as it spins, it drives the whole-body posture.
                if (_rds != null && _rds.Active) _rds.Tick(t, dt, w);
            }

            // Board fall (posturing): body stays stiff and spine arched, falling like a plank.
            if (Plan.StiffFall && Phase == NmPhase.Fall)
            {
                // Board stiffness, faded out at the end of the fall so the body doesn't go limp all at once.
                float k = Mathf.Clamp01((1.5f - (t - FallT)) / 0.5f);
                if (k > 0.01f)
                {
                    Eff.SetStiffness(Mathf.Lerp(DlCfg.RestStiffness, 15f, k), 1.2f);
                    for (int j = 0; j < DlCharacter.NJ; j++) Eff.SetMuscleStiffness(j, Mathf.Lerp(0.6f, 0.95f, k));
                    Eff.BlendDesired((int)JointId.Lumbar, 0, -0.22f, 0.85f * k);
                    Eff.BlendDesired((int)JointId.Neck, 0, -0.3f, 0.85f * k);
                    // Tonic pronation (decerebration) of forearms during the board fall.
                    Eff.BlendDesired((int)JointId.LElbow, 2, 1.0f * Motor.InternalRotSign(-1), 0.85f * k);
                    Eff.BlendDesired((int)JointId.RElbow, 2, 1.0f * Motor.InternalRotSign(1), 0.85f * k);
                    Eff.SetOpposeGravity((int)JointId.Lumbar, 0f);
                }
            }

            // Delirium effectors are the sole command source for Euphoria behaviors: once per step.
            Eff.Flush(ns, Body, w);
        }

        // ------------------------------------------------------------------ forces

        public void Forces(float t, float dt)
        {
            float w = U(t);
            _balance?.Forces(t, dt);
            // oppose_gravity: Delirium effectors compensate part of the gravity torque.
            DlPhys.OpposeGravity(Eff, Body, w);
            if (_rds != null && _rds.Active) _rds.Forces(t, dt, w);
            if (_grab != null && _grab.Active) _grab.Forces(t, dt, w);
            if (_writhe != null && _writhe.Active) _writhe.Forces(t, dt, w);
            if (_toppleT0 >= 0f)
            {
                float x = t - _toppleT0;
                if (x > 0.45f || Phase == NmPhase.Ground) _toppleT0 = -1f;
                else if (x >= 0f && _toppleAxis.sqrMagnitude > 1e-6f)
                {
                    // An already-dead body has no tone left, but it still falls: the topple does not fade with w.
                    float wt = Plan.Conscious ? w : 0.6f;
                    NmPhysics.BodyAngularAccel(Body, _toppleAxis * (_toppleGain * Mathf.Sin(Mathf.PI * x / 0.45f) * wt));
                }
            }
            if (_turnT0 >= 0f)
            {
                // Ground roll-over: whole-body rotation around its long axis (rolling onto side) or
                // vertical (turning over), without writing a pose: agony continues underneath.
                float x = t - _turnT0;
                if (x > _turnDur) _turnT0 = -1f;
                else if (x >= 0f)
                {
                    Vector3 ax = _turnAxis.sqrMagnitude > 1e-4f ? _turnAxis.normalized : Ch.Fwd;
                    NmPhysics.BodyAngularAccel(Body, ax * (_turnGain * Mathf.Sin(Mathf.PI * x / _turnDur) * w));
                }
            }
            if (_iog != null && _iog.Active) _iog.Forces(t, _neural.AgonyIntensity(t), w);
            _shot.Forces(t, dt, w);

            if (_snapT0 >= 0f)
            {
                if (!_snapDone)
                {
                    _snapDone = true;
                    var last = Wounds[Wounds.Count - 1];
                    Vector3 ax = Vector3.Cross(Vector3.up, last.Dir);
                    ax = BMath.SafeNormalize(ax, Ch.Right);
                    ax = (ax + new Vector3(BMath.Range(Rng, -1f, 1f), BMath.Range(Rng, -1f, 1f), BMath.Range(Rng, -1f, 1f)) * 0.3f).normalized;
                    _snapAxis = ax;
                    SnapImpulse(ax * 4f);
                }
                else if (!_unsnapDone && t - _snapT0 >= 0.01f)
                {
                    _unsnapDone = true;
                    SnapImpulse(-_snapAxis * (4f * 0.7f));
                    _snapT0 = -1f;
                }
            }
            if (_injArmT0 >= 0f)
            {
                float x = t - _injArmT0;
                if (x > 0.25f) _injArmT0 = -1f;
                else NmPhysics.BodyAngularAccel(Body, Vector3.up * (_injArmSign * 8f * Mathf.Sin(Mathf.PI * x / 0.25f)));
            }
            if (Phase != NmPhase.Ground || t - GroundT < 1f)
            {
                float ls = Vector3.Dot(Ch.L, Ch.Right), lc = Vector3.Dot(Ch.L, Ch.Fwd);
                Vector3 tq = Ch.Right * Excess(ls, NmCharacterDamping.SomersaultThresh, NmCharacterDamping.SomersaultDamp)
                           + Ch.Fwd * Excess(lc, NmCharacterDamping.CartwheelThresh, NmCharacterDamping.CartwheelDamp);
                if (tq.sqrMagnitude > 1e-6f) NmPhysics.BodyAngularAccel(Body, tq / Ch.IBody);
            }
        }

        private static float Excess(float L, float thresh, float k) => Mathf.Abs(L) > thresh ? -Mathf.Sign(L) * (Mathf.Abs(L) - thresh) * k * 4f : 0f;

        private void SnapImpulse(Vector3 dwThorax)
        {
            var th = Body[Seg.Thorax];
            var pv = Body[Seg.Pelvis];
            th.Rb.AddTorque(dwThorax, ForceMode.VelocityChange);
            pv.Rb.AddTorque(-dwThorax * (0.5f * th.Mass / Mathf.Max(1f, pv.Mass)), ForceMode.VelocityChange);
            Body[Seg.Head].Rb.AddTorque(dwThorax * 0.6f, ForceMode.VelocityChange);
        }
    }
}
