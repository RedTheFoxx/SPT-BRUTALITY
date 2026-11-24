using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Euphoria
{
    /// <summary>Balancer options, derived from the death plan (Delirium: bodyBalance / staggerFall / shotFallToKnees message).</summary>
    internal sealed class DlBalanceOptions
    {
        public bool Stagger, FallToKnees;
        public int MaxSteps = 3;
        public float MaxTime = 2f, PerStep = 1f, KneelHold = 0.6f, Sink0, DontStepTime;
        public int InjuredLeg;
        /// <summary>Shot in the back: quadriceps give way, pelvis drops onto the knees instead of toppling.</summary>
        public float GiveWay;
        /// <summary>Seated collapse: hips flex, pelvis drops, no step is attempted.</summary>
        public bool Sit;
        /// <summary>Imposed lateral lean (-1 left, +1 right): fall goes sideways.</summary>
        public int LeanSide;
        /// <summary>Twist around vertical at balance start (rad/s²): body pivots before falling.</summary>
        public float YawAccel;
        /// <summary>Delirium leanInDirection: horizontal lean direction of the balancer.</summary>
        public Vector3 LeanDir;
        /// <summary>Intensity of leanInDirection (0 = inactive, 0.1..0.35 = typical Delirium).</summary>
        public float LeanAmount;
        /// <summary>Apply leanInDirection as horizontal force on the pelvis (Delirium: applyAsForce).</summary>
        public bool LeanAsForce;
        /// <summary>Leg buckling mode: 0 = none, 1 = one leg gives way, 2 = both legs together, 3 = both legs staggered.</summary>
        public int BuckleMode;
        /// <summary>Leg that gives way first (-1 left, +1 right).</summary>
        public int BuckleLeg;
        /// <summary>Delay before initial buckling (s) and stagger delay for the second leg in mode 3 (s).</summary>
        public float BuckleDelay = 0.08f, BuckleStaggerDelay = 0.16f;
    }

    /// <summary>
    /// Dynamic balancer, ported from Delirium's <c>nmbb::BodyBalance</c> (upper-body law) and <c>nmdbm</c>
    /// (dynamic balance manager: foot placement, pelvis control, leg inverse kinematics).
    ///
    /// Same principle: the character has no actuated ankles, so the pelvis is held by a corrective force
    /// (Delirium: hip stiffness and "stabilise") while the legs are placed via inverse kinematics onto a
    /// capture point (Hof 2005; Pratt 2006) clamped by hip range. Each step reduces leg stiffness; when the
    /// capture point exits the support polygon and the balancer gives up, the body falls ("balance" return, then catchFall).
    ///
    /// Upper limbs are counterbalanced by hip kinematics as in Delirium:
    /// the shoulder tracks the opposite hip, the elbow tracks desired/actual hip error, abduction tracks
    /// hip abduction, and a "windmill" term (somersault / sideSomersault) is added when the body spins fast.
    /// </summary>
    internal sealed class DlBalance
    {
        public readonly DlBalanceOptions O;
        public readonly string Name;
        public bool Active = true;
        public bool Failed;
        public string Why = "";
        public float FailT = 1e9f;
        public float LowerK;
        public int Steps;
        public bool KneesDown;
        public float Sink;
        public float MaxTime;
        public int MaxSteps;
        public Vector3 LeanVec;
        /// <summary>Per-leg knee stiffness scale (Delirium: dbm_knee_scale, lowered by shot.collapse).</summary>
        public readonly float[] KneeScale = { 1f, 1f };
        /// <summary>Instantaneous buckling level [0..1] per leg (0 = left, 1 = right).</summary>
        public readonly float[] LegBuckle = { 0f, 0f };
        /// <summary>Additional pelvis lean requested by shot.collapse (Delirium: dbm_acc).</summary>
        public float AccBias;
        /// <summary>Cumulative leg stiffness reduction from wall contact / slump (balancerCollisionsReaction).</summary>
        public float ExternalKneeStiffReduction { get; private set; }
        /// <summary>True while DlWallBrace is actively cushioning against a wall.</summary>
        public bool WallBracing { get; set; }

        public void ReduceLegStiffness(float delta)
        {
            ExternalKneeStiffReduction = Mathf.Min(Mathf.Max(0f, LowerK - 3.5f), ExternalKneeStiffReduction + Mathf.Max(0f, delta));
        }

        private readonly DlBody _b;
        private readonly DlCharacter _ch;
        private readonly NmCharacter _s;
        private readonly NmPerformance _perf;
        private readonly System.Random _rng;
        private readonly float _start, _y0, _legLength, _footWidth;
        private float _now;
        private readonly bool[] _shotCollapsed = new bool[2];
        private bool _buckleLogged;

        private readonly Vector3[] _ankle = new Vector3[2];
        private readonly Vector3[] _swingFrom = new Vector3[2];
        private readonly bool[] _planted = { true, true };
        private float _stepT, _stepDur, _stepClamp = DlCfg.StepClamp;
        private int _swing, _lastSwing;
        private float _bendT = -1f, _kneelT;
        private float _lean, _p52, _p56;
        private float _yaw0, _yaw1;
        private float _somersault, _sideSomersault, _armsOut;

        public DlBalance(NmPerformance perf, float t, DlBalanceOptions o)
        {
            _perf = perf;
            _b = perf.Dl;
            _ch = perf.Eff;
            _s = perf.Ch;
            _rng = perf.Rng;
            O = o;
            Name = o.Stagger ? "staggerFall" : o.FallToKnees ? "shotFallToKnees" : o.Sit ? "sitDown" : "bodyBalance";
            _start = t;
            MaxTime = o.MaxTime;
            MaxSteps = o.MaxSteps;
            Sink = o.Sink0;
            LowerK = o.Stagger ? 13f : DlCfg.BalanceLegStiffness;
            _legLength = _b.Body[Seg.LThigh].Length + _b.Body[Seg.LCalf].Length;
            _y0 = _b.Body[Seg.Pelvis].Com.y;
            _footWidth = Mathf.Clamp(DlBody.Horiz(_s.Ankle[0] - _s.Ankle[1]).magnitude * 0.5f, 0.09f, 0.18f);
            for (int k = 0; k < 2; k++) _ankle[k] = _s.Ankle[k];
            Activate(t);
        }

        private void Activate(float t)
        {
            _ch.ResetAll();
            foreach (var j in DlCharacter.Mask("us")) _ch.SetMuscleStiffness((int)j, 0.8f);
            foreach (var j in DlCharacter.Mask("un")) _ch.SetMuscleStiffness((int)j, 0.8f);
            // Only limbs are gravity-compensated: supporting the spine would cancel trunk gravity.
            _ch.SetOpposeGravity("ua", 1f);
            _ch.SetOpposeGravity("lb", 0.4f);
            _ch.BlendToZero("uc", 1f);
            _somersault = 1f;
            _sideSomersault = 1f;
            _armsOut = 0f;
            _now = t;
            _perf.Log(t, Name);
        }

        public float Strength(float t)
        {
            float effK = Mathf.Max(3.5f, LowerK - ExternalKneeStiffReduction);
            float k01 = Mathf.Clamp01((effK - 4f) / (DlCfg.BalanceLegStiffness - 4f)) * _perf.U(t);
            if (!Failed) return k01;
            return k01 * (1f - Mathf.Clamp01((t - FailT) / 0.18f));
        }

        public void Stop(float t) => Active = false;

        public void Fail(float t, string why)
        {
            if (Failed) return;
            Failed = true;
            FailT = t;
            Why = why;
            _perf.Log(t, "imbalance: " + why);
        }

        /// <summary>Connects <c>nmshot::Shot::collapse</c> to the balancer (Delirium: dbm_knee_scale + dbm_acc).</summary>
        public void CollapseLeg(int side, float strength01)
        {
            if (side == 0) return;
            int k = NmCharacter.Si(side);
            float w = Mathf.Clamp01(strength01);
            _shotCollapsed[k] = w > 0.05f;
            KneeScale[k] = Mathf.Min(KneeScale[k], Mathf.Lerp(1f, 0.25f, w));
            LegBuckle[k] = Mathf.Max(LegBuckle[k], w);
            AccBias = Mathf.Min(AccBias, -0.5f * w);
        }

        // ------------------------------------------------------------------ upper body (nmbb::BodyBalance::tick)

        public void Tick(NeuralState ns, float t, float dt)
        {
            if (!Active) return;
            var b = _b;
            _now = t;
            if (Failed)
            {
                if (Strength(t) <= 0.01f) Active = false;
                return;
            }
            float lean = Mathf.Sqrt(Mathf.Max(0f, 1f - _s.Hip2HeadUp * _s.Hip2HeadUp));
            bool lastStand = _perf.LastStandUntil > t;
            if (!O.FallToKnees && lean > (lastStand ? 0.85f : (O.Stagger ? 0.68f : DlCfg.BalanceAbortThreshold))) { Fail(t, "lean"); return; }
            if (!O.FallToKnees && _s.Com.y - _s.FootMinY < DlCfg.GiveUpHeight) { Fail(t, "height"); return; }
            if (t - _start > MaxTime) { Fail(t, "max duration"); return; }
            if (!O.FallToKnees && t - _start > 0.22f && DlBody.Horiz(_s.Vel).magnitude > 2.8f) { Fail(t, "velocity"); return; }
            if (O.Stagger && (LowerK - ExternalKneeStiffReduction) <= 8.01f && !WallBracing) { Fail(t, "legs"); return; }

            // Base limb stiffnesses (Delirium nmbb.rs:159-167: only elbows take 0.6 × bodyStiffness,
            // shoulders keep bodyStiffness).
            _ch.SetStiffness("ub", DlCfg.BalanceBodyStiffness, DlCfg.BalanceDamping);
            _ch.SetStiffness("ua", DlCfg.BalanceBodyStiffness, DlCfg.BalanceDamping);
            _ch.SetStiffness("un", DlCfg.BalanceBodyStiffness, 1f);
            _ch.SetStiffness("uc", DlCfg.BalanceBodyStiffness, 1f);
            float ke = DlCfg.BalanceBodyStiffness * 0.6f;
            _ch.SetStiffness(JointId.LElbow, ke, DlCfg.BalanceDamping);
            _ch.SetStiffness(JointId.RElbow, ke, DlCfg.BalanceDamping);
            foreach (var j in DlCharacter.Mask("us")) _ch.SetMuscleStiffness((int)j, 0.8f);
            foreach (var j in DlCharacter.Mask("un")) _ch.SetMuscleStiffness((int)j, 0.8f);

            // Spine flexion: resting S-curve, then trunk lean compensation.
            float roll = Mathf.Asin(Mathf.Clamp(Vector3.Dot(_b.Part(Seg.Thorax).U, _b.Right), -1f, 1f)) * Mathf.Rad2Deg;
            var spine = DlCharacter.Mask("us");
            DlCfg.Range((int)JointId.Lumbar, 1, out float lo1, out float hi1);
            float mid1 = 0.5f * (lo1 + hi1);
            float[] baseLean = { -0.10f, 0.01f, -0.10f, 0.10f };
            float[] twistGain = { 0.35f, 0.20f, 0.60f, 0.10f };

            // Twist: left/right hip difference (Delirium: tw = (hip0.desired - hip1.desired)·a + (hip0.actual - hip1.actual)).
            float hipDesDiff = _ch.Desired((int)JointId.LHip).x - _ch.Desired((int)JointId.RHip).x;
            float hipActDiff = _ch.Actual((int)JointId.LHip, _b.Body).x - _ch.Actual((int)JointId.RHip, _b.Body).x;
            float tw = hipDesDiff * 0.3f + hipActDiff;

            for (int i = 0; i < spine.Length; i++)
            {
                int j = (int)spine[i];
                DlCfg.Range(j, 0, out float lo, out float hi);
                float mid = 0.5f * (lo + hi);
                _ch.SetDesired(j, 0, mid + baseLean[i]);
                _ch.SetDesired(j, 1, mid1 + 0.5f * (roll * BMath.Deg) * 0.5f);
                DlCfg.Range(j, 2, out float lo2, out float hi2);
                _ch.SetDesired(j, 2, 0.5f * (lo2 + hi2) + Mathf.Clamp(tw * twistGain[i], -0.35f, 0.35f));
            }

            // Arms: hip counterbalancing (Delirium nmbb.rs:275-318: skips arms busy with grab/shot).
            bool grounded = _s.Contact[0] || _s.Contact[1];
            for (int side = 0; side < 2; side++)
            {
                if (_ch.Busy[side] || !grounded) continue;
                int sd = side == 0 ? -1 : 1;
                var sh = AnatomyData.Shoulder(sd);
                var el = AnatomyData.Elbow(sd);
                var hipOther = AnatomyData.Hip(sd == -1 ? 1 : -1);
                var hipMine = AnatomyData.Hip(sd);
                float hoAct = _ch.Actual((int)hipOther, _b.Body).x;
                float hoDes = _ch.Desired((int)hipOther).x;
                float hsAct = _ch.Actual((int)hipMine, _b.Body).x;

                float x = -hoAct * 0.3f + hoDes;
                x = (-hoAct * 0.3f + hsAct) * 0.2f + x;
                x += _somersault * _armsOut * 0.4f;
                float shFlex = x * DlCfg.BalanceShoulder + _ch.Actual((int)sh, _b.Body).x;
                _ch.SetDesired((int)sh, 0, shFlex);
                DlCfg.Range((int)sh, 2, out float slo, out float shi);
                _ch.SetDesired((int)sh, 2, 0.5f * (slo + shi) - 0.35f);

                float elbow = (hoDes - hoAct) * DlCfg.BalanceElbow + _ch.Actual((int)el, _b.Body).x;
                DlCfg.Range((int)el, 0, out float elo, out float ehi);
                _ch.SetDesired((int)el, 0, Mathf.Clamp(elbow, elo + 0.15f, ehi - 0.05f));

                float l2 = _ch.Actual((int)hipMine, _b.Body).y * 0.5f + 0.5f;
                if (_armsOut > 0f) l2 += _somersault * _armsOut * 0.5f;
                DlCfg.Range((int)sh, 1, out float alo, out float ahi);
                _ch.SetDesired((int)sh, 1, 0.5f * (alo + ahi) + 0.5f * (ahi - alo) * Mathf.Clamp(l2 * 2f - 1f, -1f, 1f));
                _ch.SetOpposeGravity((int)sh, 1f);
            }

            // "Windmill" tied to angular velocities: Delirium clamps each rotation above a threshold and
            // feeds it back into abduction (somersaultAngle / sideSomersaultAngle) when armsOutOnPush is active.
            Vector3 w = _b.AngVel;
            float ratePitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(_b.Part(Seg.Thorax).U, BMath.SafeNormalize(_b.Vel, _b.Fwd)), -1f, 1f)) * Mathf.Rad2Deg;
            float overPitch = Over(ratePitch, 25f);
            float overRoll = Over(roll, 25f);
            _somersault = Mathf.Lerp(_somersault, 1f + Mathf.Clamp(overPitch, 0f, 1f), 1f - Mathf.Exp(-dt * 6f));
            _sideSomersault = Mathf.Lerp(_sideSomersault, 1f + Mathf.Clamp(overRoll, 0f, 1f), 1f - Mathf.Exp(-dt * 6f));
            if (w.magnitude > 4f) _armsOut = Mathf.Min(1f, _armsOut + dt * 2f);
            else _armsOut = Mathf.Max(0f, _armsOut - dt);

            // Imposed lateral lean: torso goes sideways, fall occurs in the frontal plane.
            if (O.LeanSide != 0)
                _ch.SetDesired((int)JointId.Lumbar, 1, mid1 + O.LeanSide * 0.22f);

            // Speed: partial blend_to_zero of upper body (Delirium nmbb.rs:322: k ∈ [0.25, 0.53], never 1.0).
            float f19 = Mathf.Clamp01(_s.Vel.magnitude);
            float blendK = 0.25f + 0.28f * (1f - f19);
            foreach (var j in DlCharacter.Mask("ub"))
            {
                if ((j == JointId.LShoulder || j == JointId.LElbow) && _ch.Busy[0]) continue;
                if ((j == JointId.RShoulder || j == JointId.RElbow) && _ch.Busy[1]) continue;
                _ch.BlendToZero((int)j, blendK);
            }

            // Balance manager: feet, pelvis, legs.
            Legs(ns, t, dt, lean);
        }

        private static float Over(float x, float thr) => Mathf.Clamp(Mathf.Abs(x), thr, 60f) - thr;

        // ------------------------------------------------------------------ legs (nmdbm)

        private void Legs(NeuralState ns, float t, float dt, float lean)
        {
            var body = _b.Body;
            var pelvis = body[Seg.Pelvis];
            float g = Mathf.Max(1f, -Physics.gravity.y);
            float w0 = Mathf.Sqrt(g / Mathf.Max(0.4f, _s.H));
            Vector3 comH = DlBody.Horiz(_s.Com), velH = DlBody.Horiz(_s.Vel);

            // Capture point: the foot must land where the center of mass will arrive during the step.
            float pred = O.Stagger ? 0.14f : 0.04f;
            Vector3 apex = comH + velH * (1f / w0 + pred);
            // Lateral fall: capture point is offset toward the lean side, body loses support.
            if (O.LeanSide != 0) apex += _s.Right * (O.LeanSide * 0.24f);

            float giveWay = O.GiveWay * BMath.SmoothStep(0.01f, 0.18f, t - _start);
            // Scheduled buckling of one or both legs (always updated before Sit/FallToKnees early returns).
            UpdateBuckle(t, giveWay);

            // Seated collapse: hips flex, pelvis drops, feet stop moving.
            if (O.Sit && t - _start > 0.1f)
            {
                if (_bendT < 0f) { _bendT = t; _perf.Log(t, "sitDown: hips bend"); }
                Sink = Mathf.Min(0.46f, Sink + 1.1f * dt);
                float k = Mathf.Clamp01(Sink / 0.46f);
                for (int sd = -1; sd <= 1; sd += 2)
                {
                    int hip = (int)AnatomyData.Hip(sd), knee = (int)AnatomyData.Knee(sd);
                    DlCfg.Range(hip, 0, out float hlo, out float hhi);
                    DlCfg.Range(knee, 0, out float klo, out float khi);
                    _ch.SetDesired(hip, 0, Mathf.Lerp(hlo + 0.2f * (hhi - hlo), hlo + 0.7f * (hhi - hlo), k));
                    _ch.SetDesired(knee, 0, Mathf.Lerp(klo + 0.2f * (khi - klo), klo + 0.9f * (khi - klo), k));
                    _ch.SetStiffness((JointId)hip, LowerK, 1f);
                    _ch.SetStiffness((JointId)knee, LowerK * 0.55f, 1f);
                }
                return;
            }

            // shotFallToKnees: progressive flexion, knee support, then collapse.
            if (O.FallToKnees && t - _start > 0.7f)
            {
                if (_bendT < 0f) { _bendT = t; _perf.Log(t, "shotFallToKnees: flexion"); }
                Sink = Mathf.Min(0.44f, Sink + 0.5f * dt);
                float kk = 1f - Sink / 0.44f;
                for (int sd = -1; sd <= 1; sd += 2)
                {
                    int hip = (int)AnatomyData.Hip(sd), knee = (int)AnatomyData.Knee(sd);
                    DlCfg.Range(hip, 0, out float hlo, out float hhi);
                    DlCfg.Range(knee, 0, out float klo, out float khi);
                    _ch.SetDesired(hip, 0, Mathf.Lerp(hlo + 0.25f * (hhi - hlo), hlo + 0.55f * (hhi - hlo), 1f - kk));
                    _ch.SetDesired(knee, 0, Mathf.Lerp(klo + 0.25f * (khi - klo), klo + 0.75f * (khi - klo), 1f - kk));
                    _ch.SetStiffness((JointId)hip, LowerK, 1f);
                    _ch.SetStiffness((JointId)knee, LowerK, 1f);
                }
                if (!KneesDown && Mathf.Min(_s.Knee[0].y, _s.Knee[1].y) - _s.GroundY < 0.12f)
                {
                    KneesDown = true;
                    _kneelT = t;
                    MaxTime = Mathf.Max(MaxTime, t - _start + O.KneelHold + 0.1f);
                    _perf.OnKneesImpact(t);
                }
                // Support force: knees bear weight instead of falling.
                if (KneesDown) _perf.OnKneeSupport(t, dt, Strength(t) * 0.6f);
                if (KneesDown && t - _kneelT > O.KneelHold) Fail(t, "knees");
                return;
            }

            // Step decision.
            if (_swing == 0 && t - _start >= O.DontStepTime)
            {
                Vector3 a = DlBody.Horiz(_s.Ankle[0]), c = DlBody.Horiz(_s.Ankle[1]);
                Vector3 ac = c - a;
                float u01 = Mathf.Clamp01(Vector3.Dot(apex - a, ac) / Mathf.Max(1e-6f, ac.sqrMagnitude));
                float dist = Vector3.Distance(apex, a + ac * u01);
                if (dist > 0.065f || ac.magnitude > 0.23f + 0.45f)
                {
                    if (Steps >= MaxSteps) { Fail(t, "max steps"); return; }
                    float lat = Vector3.Dot(apex - comH, _s.Right);
                    int sw = Mathf.Abs(lat) > 0.06f ? (lat > 0f ? 1 : -1)
                        : (Vector3.Distance(a, apex) > Vector3.Distance(c, apex) ? -1 : 1);
                    if (sw == _lastSwing && Mathf.Abs(lat) < 0.22f) sw = -sw;
                    if (O.InjuredLeg == sw && BMath.Chance(_rng, 0.5f)) sw = -sw;
                    _swing = sw;
                    _stepT = 0f;
                    _stepDur = BMath.Range(_rng, 0.24f, 0.32f) + (1f - Mathf.Clamp01(Strength(_now))) * 0.08f;
                    _swingFrom[NmCharacter.Si(sw)] = _s.Ankle[NmCharacter.Si(sw)];
                    _planted[NmCharacter.Si(sw)] = false;
                }
            }

            float hipH = _y0 - Sink;
            // Delirium leanInDirection: tilt of reference up vector (or horizontal push if LeanAsForce).
            if (O.LeanAmount > 0.005f && O.LeanDir.sqrMagnitude > 1e-4f)
            {
                Vector3 ld = DlBody.Horiz(O.LeanDir);
                if (ld.sqrMagnitude > 1e-4f) LeanVec = ld.normalized * O.LeanAmount;
            }
            for (int sd = -1; sd <= 1; sd += 2)
            {
                int k = NmCharacter.Si(sd);
                var thigh = body[sd < 0 ? Seg.LThigh : Seg.RThigh];
                Vector3 target;
                float swingArc = 0f;
                if (sd == _swing)
                {
                    _stepT += dt;
                    float ph = Mathf.Clamp01(_stepT / _stepDur);
                    swingArc = Mathf.Sin(Mathf.PI * ph);
                    Vector3 tgt = apex + _s.Right * (sd * _footWidth);
                    tgt = ClampReach(sd, tgt, _stepClamp);
                    Vector3 from = _swingFrom[k];
                    target = Vector3.Lerp(from, tgt, BMath.SmoothStep(0f, 1f, ph));
                    float lift = DlCfg.StepHeight * swingArc;
                    target.y = _s.GroundY + 0.06f + lift;
                    if (ph >= 1f || (ph > 0.72f && _s.Contact[k]))
                    {
                        Steps++;
                        _lastSwing = sd;
                        _swing = 0;
                        _planted[k] = true;
                        _ankle[k] = _s.Ankle[k];
                        _perf.Log(t, "step " + Steps + (sd < 0 ? " (L)" : " (R)"));
                        if (O.Stagger)
                        {
                            Sink = Mathf.Min(0.24f, Sink + 0.035f);
                            if (Steps >= 2) LowerK = Mathf.Max(8f - 0.02f, LowerK - O.PerStep);
                        }
                    }
                }
                else
                {
                    // Stance leg: re-anchor within comfortable leg reach if the body slid so the stance
                    // knee never locks straight at full extension.
                    Vector3 hipHPos = DlBody.Horiz(thigh.Pos);
                    Vector3 ancH = DlBody.Horiz(_ankle[k]);
                    Vector3 dAnc = ancH - hipHPos;
                    float maxStance = _legLength * 0.48f;
                    if (dAnc.magnitude > maxStance)
                        ancH = hipHPos + dAnc.normalized * maxStance;
                    target = new Vector3(ancH.x, _s.GroundY + 0.06f, ancH.z);
                    _ankle[k] = target;
                }
                LegTo(sd, target, hipH, dt, LegBuckle[k], swingArc);
            }

            // Lateral imbalance sharing between both hips and spine (Delirium: over-range sharing).
            float overL = HipOver(-1), overR = HipOver(1);
            float aO = Mathf.Max(overL, -overR), bO = Mathf.Max(overR, -overL);
            if (aO > 0f && bO <= 0f)
            {
                _ch.SetDesired((int)JointId.RHip, 1, _ch.Desired((int)JointId.RHip).y + aO);
                if (_planted[0]) _ch.SetDesired((int)JointId.LHip, 1, _ch.Desired((int)JointId.LHip).y - aO);
                _ch.SetDesired((int)JointId.Lumbar, 1, _ch.Desired((int)JointId.Lumbar).y - aO);
            }
            else if (bO > 0f && aO <= 0f)
            {
                _ch.SetDesired((int)JointId.LHip, 1, _ch.Desired((int)JointId.LHip).y + bO);
                if (_planted[1]) _ch.SetDesired((int)JointId.RHip, 1, _ch.Desired((int)JointId.RHip).y - bO);
                _ch.SetDesired((int)JointId.Lumbar, 1, _ch.Desired((int)JointId.Lumbar).y - bO);
            }

            // Pelvis control: forward/back and lateral tilt from CoM offset relative to
            // supports (Delirium: pelvis_control), forwarded to spine and hip tilt.
            Vector3 support = SupportPoint(comH);
            float ahead = Vector3.Dot(comH - support, _s.Fwd);
            float side = Vector3.Dot(comH - support, _s.Right);
            float vAhead = Vector3.Dot(velH, _s.Fwd), vSide = Vector3.Dot(velH, _s.Right);
            float leanF = Mathf.Clamp(ahead * 1.2f + vAhead * 0.12f + AccBias * 0.4f, -0.45f, 0.45f);
            float leanS = Mathf.Clamp(side * 1.5f + vSide * 0.15f, -0.5f, 0.5f);
            _p52 = Mathf.Lerp(_p52, leanS, 1f - Mathf.Exp(-dt * 8f));
            _p56 = Mathf.Lerp(_p56, leanF, 1f - Mathf.Exp(-dt * 8f));
            _ch.SetDesired((int)JointId.Lumbar, 1, Mathf.Clamp(_ch.Desired((int)JointId.Lumbar).y + _p52 * 0.5f, -0.5f, 0.5f));
            // Delirium applies pelvis tilt to spine and ankle; without an ankle joint on the EFT
            // ragdoll, it goes to the spine alone so it doesn't fight leg inverse kinematics.
            _ch.SetDesired((int)JointId.Lumbar, 0, _ch.Desired((int)JointId.Lumbar).x + _p56 * 0.5f);
            // Do not overwrite the stiffness of buckling legs (LegBuckle / KneeScale).
            float effLowerK = Mathf.Max(3.5f, LowerK - ExternalKneeStiffReduction);
            for (int sd = -1; sd <= 1; sd += 2)
            {
                int k = NmCharacter.Si(sd);
                float buckle = LegBuckle[k];
                float wk = (O.InjuredLeg == sd ? 0.7f : 1f) * Mathf.Lerp(1f, 0.35f, buckle);
                float knk = effLowerK * wk * KneeScale[k] * Mathf.Lerp(1f, 0.22f, buckle);
                _ch.SetStiffness(AnatomyData.Hip(sd), effLowerK * wk, 1f);
                _ch.SetStiffness(AnatomyData.Knee(sd), Mathf.Max(1.8f, knk), 1f);
            }

            // staggerFall: lean in walking direction (leanInDirection), gradual knee sag, and spine flexion with speed.
            if (O.Stagger)
            {
                Sink = Mathf.Min(0.22f, Sink + 0.04f * dt);
                float vf = Vector3.Dot(velH, _s.Fwd);
                float max = vf >= 0f ? 0.10f : 0.30f;
                _lean = Mathf.Min(max, _lean + dt);
                if (velH.sqrMagnitude > 1e-4f) LeanVec = velH.normalized * _lean;
                float bend = Mathf.Clamp(0.4f * vf, -0.3f, 0.4f);
                _ch.SetDesired((int)JointId.Lumbar, 0, _ch.Desired((int)JointId.Lumbar).x + bend);
            }
        }

        private void UpdateBuckle(float t, float giveWay)
        {
            float elapsed = t - _start;
            float b0 = giveWay, b1 = giveWay;
            if (O.BuckleMode > 0 && elapsed >= O.BuckleDelay)
            {
                int firstSide = O.BuckleLeg != 0 ? O.BuckleLeg : (O.InjuredLeg != 0 ? O.InjuredLeg : 1);
                int firstIdx = NmCharacter.Si(firstSide);
                int secondIdx = 1 - firstIdx;
                float g1 = BMath.SmoothStep(O.BuckleDelay, O.BuckleDelay + 0.18f, elapsed);
                if (O.BuckleMode == 1)
                {
                    if (firstIdx == 0) b0 = Mathf.Max(b0, g1); else b1 = Mathf.Max(b1, g1);
                }
                else if (O.BuckleMode == 2)
                {
                    b0 = Mathf.Max(b0, g1);
                    b1 = Mathf.Max(b1, g1);
                }
                else if (O.BuckleMode == 3)
                {
                    float g2 = BMath.SmoothStep(O.BuckleDelay + O.BuckleStaggerDelay, O.BuckleDelay + O.BuckleStaggerDelay + 0.18f, elapsed);
                    if (firstIdx == 0) { b0 = Mathf.Max(b0, g1); b1 = Mathf.Max(b1, g2); }
                    else { b1 = Mathf.Max(b1, g1); b0 = Mathf.Max(b0, g2); }
                }
                if (!_buckleLogged && g1 > 0.1f)
                {
                    _buckleLogged = true;
                    _perf.Log(t, O.BuckleMode == 1 ? $"leg buckle {(firstSide < 0 ? "L" : "R")}"
                        : O.BuckleMode == 2 ? "both legs buckle" : "asymmetric buckle (2-step)");
                }
            }
            LegBuckle[0] = Mathf.Max(_shotCollapsed[0] ? LegBuckle[0] : 0f, b0);
            LegBuckle[1] = Mathf.Max(_shotCollapsed[1] ? LegBuckle[1] : 0f, b1);
        }

        /// <summary>Delirium: reach() clamps the step target within the hip range cone.</summary>
        private Vector3 ClampReach(int sd, Vector3 target, float clamp)
        {
            int hip = (int)AnatomyData.Hip(sd);
            Vector3 h = _b.JointPos((JointId)hip);
            Vector3 d = target - h;
            float legLen = _legLength * 0.95f;
            Vector3 dh = DlBody.Horiz(d);
            // Hip extension is anatomically much tighter than flexion (-20° vs +125°): clamp backward
            // step targets tighter so backward stagger steps bend the knee instead of locking straight.
            float fwdProj = Vector3.Dot(dh, _s.Fwd);
            float maxH = fwdProj < 0f ? legLen * clamp * 0.42f : legLen * clamp * 0.72f;
            if (dh.magnitude > maxH) dh = dh.normalized * maxH;
            float maxV = 0.12f;
            Vector3 dv = Vector3.up * Mathf.Clamp(d.y, -legLen, maxV);
            Vector3 r = dh + dv;
            if (r.magnitude > legLen) r = r.normalized * legLen;
            return h + r;
        }

        /// <summary>Delirium nmdbm.rs:806-810: elliptical over-range (flexion + abduction) of the hip.</summary>
        private float HipOver(int sd)
        {
            var hip = AnatomyData.Hip(sd);
            int j = (int)hip;
            DlCfg.Range(j, 0, out float lo0, out float hi0);
            DlCfg.Range(j, 1, out float lo1, out float hi1);
            float mid0 = 0.5f * (lo0 + hi0), half0 = Mathf.Max(1e-4f, 0.5f * (hi0 - lo0));
            float mid1 = 0.5f * (lo1 + hi1), half1 = Mathf.Max(1e-4f, 0.5f * (hi1 - lo1));
            var d = _ch.Desired(j);
            float l1 = (d.x - mid0) / half0;
            float l2 = (d.y - mid1) / half1;
            float e = Mathf.Sqrt(l1 * l1 + l2 * l2);
            return Mathf.Max(0f, e - 1f) / (e + 1e-4f) * (d.y - mid1);
        }

        private void LegTo(int sd, Vector3 ankle, float hipH, float dt, float buckle, float swingArc = 0f)
        {
            var body = _b.Body;
            var pelvis = body[Seg.Pelvis];
            var thigh = body[sd < 0 ? Seg.LThigh : Seg.RThigh];
            var jm = AnatomyData.Hip(sd);
            var km = AnatomyData.Knee(sd);
            if (!Nm.LegIK(body, sd, thigh.Pos, ankle, pelvis.R, pelvis.U, pelvis.F, out float f, out float ab, out float kn))
                return;
            int k = NmCharacter.Si(sd);
            float effLowerK = Mathf.Max(3.5f, LowerK - ExternalKneeStiffReduction);
            float wk = (O.InjuredLeg == sd ? 0.7f : 1f) * Mathf.Lerp(1f, 0.35f, buckle);
            float knk = effLowerK * wk * KneeScale[k] * Mathf.Lerp(1f, 0.22f, buckle);
            _ch.SetStiffness(jm, effLowerK * wk, 1f);
            _ch.SetStiffness(km, Mathf.Max(1.8f, knk), 1f);
            // When the leg buckles, the knee closes sharply and the hip flexes under the weight.
            // During a swing step, add explicit knee/hip clearance flexion so the foot actually lifts
            // and bends instead of dragging straight across the ground.
            float extraKneeDeg = buckle * 52f + swingArc * 38f;
            float extraHipDeg = buckle * 20f + swingArc * 16f;
            float minKneeDeg = O.Stagger ? 16f : 10f;
            _ch.SetDesired((int)jm, 0, (f + extraHipDeg) * BMath.Deg);
            _ch.SetDesired((int)jm, 1, Mathf.Clamp(ab, -18f, 32f) * BMath.Deg);
            _ch.SetDesired((int)km, 0, Mathf.Max(kn + extraKneeDeg, minKneeDeg) * BMath.Deg);
            // Foot orientation: ankle follows movement direction (yaw) and local slope.
            Vector3 dir = DlBody.Horiz(_s.Vel);
            float yaw = 0f;
            if (dir.sqrMagnitude > 0.04f)
            {
                dir.Normalize();
                yaw = BMath.SignedAngleOnPlane(_s.Fwd, dir, Vector3.up);
            }
            float cur = k == 0 ? _yaw0 : _yaw1;
            cur = Mathf.Lerp(cur, Mathf.Clamp(yaw, -0.9f, 0.9f), 1f - Mathf.Exp(-8f * Mathf.Max(1e-3f, dt)));
            if (k == 0) _yaw0 = cur; else _yaw1 = cur;
            _ch.SetDesired((int)jm, 2, cur * 0.5f);
        }

        /// <summary>Effective support point: projection of CoM onto the segment between both feet.</summary>
        private Vector3 SupportPoint(Vector3 comH)
        {
            Vector3 a = DlBody.Horiz(_s.Ankle[0]), c = DlBody.Horiz(_s.Ankle[1]);
            if (!_s.Contact[0] && !_s.Contact[1]) return comH;
            if (_s.Contact[0] && !_s.Contact[1]) return a;
            if (!_s.Contact[0] && _s.Contact[1]) return c;
            Vector3 ac = c - a;
            float u = Mathf.Clamp01(Vector3.Dot(comH - a, ac) / Mathf.Max(1e-6f, ac.sqrMagnitude));
            return a + ac * u;
        }

        // ------------------------------------------------------------------ forces (pelvis_control + stabilise)

        /// <summary>
        /// Balancer forces. The legs (drives + contacts) support the body: applying a whole-body upward
        /// push would levitate the dying character, who would slide in place instead of falling.
        /// The balancer therefore provides only horizontal braking when in contact, and trunk uprighting.
        /// </summary>
        public void Forces(float t, float dt)
        {
            if (!Active) return;
            float s = Strength(t);
            if (s <= 0.005f) return;
            var body = _b.Body;
            var pelvis = body[Seg.Pelvis];
            var thorax = body[Seg.Thorax];
            float g = Mathf.Max(1f, -Physics.gravity.y);
            float M = Mathf.Max(20f, body.TotalMass);

            // Pivot spin: body rotates on itself before balance gives way.
            if (O.YawAccel != 0f)
            {
                float x = t - _start;
                if (x >= 0f && x < 0.45f)
                    NmPhysics.BodyAngularAccel(body, Vector3.up * (O.YawAccel * Mathf.Sin(Mathf.PI * x / 0.45f) * s));
            }

            // Seated collapse: pelvis drops under body weight while hips flex.
            if (O.Sit)
            {
                float sit = Mathf.Clamp01((t - _start) / 0.5f);
                if (sit > 0.01f) pelvis.Rb.AddForce(Vector3.down * (0.35f * sit * M * g * s), ForceMode.Force);
            }

            // Buckling of one or both legs (shot in back, shot in leg, or knee collapse):
            // quadriceps no longer hold the load, pelvis drops and rolls toward the yielding side if asymmetric.
            float bMax = Mathf.Max(LegBuckle[0], LegBuckle[1]);
            float bAvg = 0.5f * (LegBuckle[0] + LegBuckle[1]);
            if (bMax > 0.01f)
            {
                float drop = Mathf.Max(0f, _y0 - pelvis.Com.y);
                float load = 0.55f * bAvg * (1f - Mathf.Clamp01(drop / 0.28f)) * M * g * s;
                if (load > 0.1f)
                {
                    pelvis.Rb.AddForce(Vector3.down * load, ForceMode.Force);
                    thorax.Rb.AddForce(Vector3.down * (load * 0.35f), ForceMode.Force);
                }
                float asym = LegBuckle[1] - LegBuckle[0];
                if (Mathf.Abs(asym) > 0.1f)
                {
                    // Only one leg gives way: pelvis drops on that side (natural frontal roll).
                    Vector3 sideRoll = -_s.Fwd * (asym * 28f * s);
                    pelvis.Rb.AddTorque(sideRoll, ForceMode.Force);
                }
            }

            // Delirium leanInDirection (applyAsForce): gentle horizontal push on the pelvis when in contact.
            if (O.LeanAsForce && LeanVec.sqrMagnitude > 1e-5f && (_s.Contact[0] || _s.Contact[1]))
            {
                Vector3 uLean = (Vector3.up + DlBody.Horiz(LeanVec)).normalized;
                Vector3 gVec = Vector3.down * (g * M);
                Vector3 fLean = DlBody.Horiz(gVec - uLean * Vector3.Dot(uLean, gVec));
                pelvis.Rb.AddForce(Vector3.ClampMagnitude(fLean * s, 0.28f * M * g), ForceMode.Force);
            }

            // Grounded vertical leg support (Delirium stayUpright / foot_support): strictly contact-gated
            // and capped below full body weight so the stance feet always maintain real ground normal force
            // and friction (preventing hovercraft sliding), while never levitating when knees buckle.
            if (!Failed && !O.Sit && (_s.Contact[0] || _s.Contact[1]))
            {
                float avgHold = Mathf.Clamp01(1f - bAvg);
                float cUp = Mathf.Clamp01(Vector3.Dot(pelvis.U, Vector3.up));
                float hTarget = _y0 - Sink - 0.28f * (1f - avgHold);
                if (pelvis.Com.y <= hTarget + 0.08f)
                {
                    float footScale = (_s.Contact[0] && _s.Contact[1]) ? 1f : 0.78f;
                    float supportFrac = Mathf.Clamp01(avgHold * s * cUp * cUp * footScale);
                    float yErr = hTarget - pelvis.Com.y;
                    // Bounded vertical support (capped at 1.03 g, never launching upward). During stagger,
                    // keep baseline slightly below 1 g (0.96 g) so stance feet remain loaded on the floor
                    // with real friction instead of levitating.
                    float baseG = O.Stagger ? 0.96f * g : g;
                    float upAccel = (baseG + Mathf.Clamp(yErr * 22f - pelvis.V.y * 7.5f, -10f, 3.0f)) * supportFrac;
                    float fUp = Mathf.Clamp(M * upAccel, 0f, 1.03f * M * g);
                    pelvis.Rb.AddForce(Vector3.up * (fUp * 0.62f), ForceMode.Force);
                    thorax.Rb.AddForce(Vector3.up * (fUp * 0.38f), ForceMode.Force);
                }
            }

            // Horizontal stabilization (Delirium nmdbm::stabilise) + mass-scaled foot braking, only in contact.
            if (_s.Contact[0] || _s.Contact[1])
            {
                Vector3 midFoot = 0.5f * (_s.Ankle[0] + _s.Ankle[1]);
                Vector3 dCom = _s.Com - midFoot;
                float cUp = Mathf.Max(0f, Vector3.Dot(dCom.normalized, Vector3.up));
                float c4 = cUp * cUp * cUp * cUp;
                float stabK = (1f - 0.7f * bAvg) * s * c4;
                Vector3 vH = NmCharacter.Flat(_s.Vel);
                float brakeRate = O.Stagger ? 0.16f : 0.22f;
                Vector3 fStab = -DlBody.Horiz(dCom + vH * 0.12f) * (M * 0.18f * stabK) - vH * (M * brakeRate * s);
                Vector3 fH = Vector3.ClampMagnitude(fStab, 0.16f * M * g * s);
                pelvis.Rb.AddForce(fH * 0.7f, ForceMode.Force);
                thorax.Rb.AddForce(fH * 0.3f, ForceMode.Force);
            }

            // Trunk uprighting via real torque: upper-body inertia around the hips. Torque is bounded
            // so the trunk can actually topple when the balancer gives up.
            Vector3 upT = (Vector3.up + LeanVec).normalized;
            float Iub = 0.5f;
            foreach (var id in new[] { Seg.Pelvis, Seg.Thorax, Seg.Head, Seg.LUpperArm, Seg.RUpperArm, Seg.LForearm, Seg.RForearm })
                Iub += body[id].Mass * (body[id].Com - pelvis.Com).sqrMagnitude;
            float ls = _perf.LastStandUntil > t ? 1.6f : 1f;
            float upHold = 1f - 0.50f * bAvg;
            Upright(thorax, upT, Iub * 0.40f * s * ls * upHold, dt, 135f);
            Upright(pelvis, Vector3.Slerp(Vector3.up, upT, 0.5f), Iub * 0.28f * s * ls * upHold, dt, 110f);
        }

        private static void Upright(Segment sg, Vector3 target, float gain, float dt, float maxTorque = 90f)
        {
            Vector3 err = Vector3.Cross(sg.U, target);
            Vector3 wH = sg.W - Vector3.Project(sg.W, Vector3.up);
            float iSeg = Mathf.Max(0.05f, (sg.InertiaDiag.x + sg.InertiaDiag.y + sg.InertiaDiag.z) / 3f);
            float kp = Mathf.Min(60f * gain, dt > 1e-4f ? 0.3f * iSeg / (dt * dt) : 60f * gain);
            float kd = Mathf.Min(14f * gain, dt > 1e-4f ? 0.9f * iSeg / dt : 14f * gain);
            Vector3 tq = Vector3.ClampMagnitude(kp * err - kd * wH, maxTorque);
            sg.Rb.AddTorque(tq, ForceMode.Force);
        }
    }
}
