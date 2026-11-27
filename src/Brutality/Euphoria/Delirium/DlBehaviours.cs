using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Euphoria
{
    /// <summary>
    /// Common base for Delirium port behaviors. A behavior never touches rigidbodies directly:
    /// it writes effectors (<see cref="DlCharacter"/>), like <c>nmrs::Character::eff</c> in Delirium.
    /// </summary>
    internal abstract class DlBehaviour
    {
        public readonly string Name;
        public bool Active = true;
        protected readonly DlBody B;
        protected readonly DlCharacter Ch;
        protected readonly NmCharacter Sensors;
        protected readonly NmPerformance Perf;
        protected readonly float Start;

        protected DlBehaviour(NmPerformance perf, string name, float t)
        {
            Perf = perf;
            B = perf.Dl;
            Ch = perf.Eff;
            Sensors = perf.Ch;
            Name = name;
            Start = t;
            perf.Log(t, name);
        }

        public virtual void Stop(float t) => Active = false;

        /// <summary>Arm inverse kinematics to a world point; returns false if the skeleton forbids it.</summary>
        protected bool ArmIK(int side, Vector3 target, out float flexDeg, out float abdDeg, out float elbowDeg)
        {
            var body = B.Body;
            var th = body[Seg.Thorax];
            var up = body[side < 0 ? Seg.LUpperArm : Seg.RUpperArm];
            var fo = body[side < 0 ? Seg.LForearm : Seg.RForearm];
            return JointKinematics.ArmIK(up.Pos, up.Length, fo.Length + 0.06f, target, side,
                th.R, th.U, th.F, out flexDeg, out abdDeg, out elbowDeg, out _);
        }

        /// <summary>Moves the hand to a target by blending the command (Delirium: limb_ik with weight w).</summary>
        protected void ArmTo(int side, Vector3 target, float w, float stiffness, float damping)
        {
            if (w <= 0.01f) return;
            if (!ArmIK(side, target, out float f, out float a, out float e)) return;
            var sh = AnatomyData.Shoulder(side);
            var el = AnatomyData.Elbow(side);
            Ch.SetStiffness(sh, stiffness, damping);
            Ch.SetStiffness(el, stiffness, damping);
            Ch.BlendDesired((int)sh, 0, f * BMath.Deg, w);
            Ch.BlendDesired((int)sh, 1, a * BMath.Deg, w);
            Ch.BlendDesired((int)el, 0, e * BMath.Deg, w);
        }

        /// <summary>Delirium (nmcf::clav_follow): the shoulder girdle follows the shoulder proportionally to range.</summary>
        protected void ClavicleFollow(int side)
        {
            // No clavicle joint on the EFT ragdoll: the shoulder plays both roles. Keep the
            // function so calling behaviors remain readable.
        }

        protected static Vector3 Horiz(Vector3 v) => DlBody.Horiz(v);
    }

    /// <summary>
    /// catchFall, ported from Delirium's <c>nmcf::CatchFall</c>: arms probe the ground and nearby surfaces
    /// in the fall direction, palms orient toward the normal via forearm pronation, stiffness drops
    /// on contact (armReduceSpeed) to cushion the impact, trunk curls, legs tuck,
    /// spine twist decays, and the neck looks at the landing point.
    /// </summary>
    internal sealed class DlCatchFall : DlBehaviour
    {
        private readonly float[] _leg = new float[2];
        private readonly float[] _armK = { 1f, 1f };
        private Vector3 _dir = Vector3.down;
        private float _forward, _k = 1f, _upright, _lean2;
        private bool _touched;
        public readonly bool[] ArmHitSurface = new bool[2];
        public Vector3 FallDir => _dir;
        public float ForwardDot => _forward;

        public DlCatchFall(NmPerformance perf, float t) : base(perf, "catchFall", t)
        {
            Ch.SetStiffness("us", DlCfg.CatchTorsoStiffness, 0.5f);
            Ch.SetStiffness("un", DlCfg.CatchTorsoStiffness, 0.5f);
            if (!Ch.Busy[0]) Ch.SetStiffness("ul", DlCfg.CatchArmsStiffness - 1f, 1f);
            if (!Ch.Busy[1]) Ch.SetStiffness("ur", DlCfg.CatchArmsStiffness - 1f, 1f);
            Ch.SetStiffness("lb", DlCfg.CatchLegsStiffness, 0.5f);
            // Limbs only: oppose_gravity on the spine would support the trunk and the body wouldn't fall.
            if (!Ch.Busy[0]) Ch.SetOpposeGravity("ul", 0.6f);
            if (!Ch.Busy[1]) Ch.SetOpposeGravity("ur", 0.6f);
            Ch.SetOpposeGravity("lb", 0.5f);
            for (int i = 0; i < 2; i++)
            {
                _leg[i] = 0.5f + 0.5f * (float)Perf.Rng.NextDouble();
                int sd = i == 0 ? -1 : 1;
                Ch.SetRelative((int)AnatomyData.Hip(sd), 0, _leg[i]);
                Ch.SetRelative((int)AnatomyData.Knee(sd), 0, 0.35f + 0.45f * _leg[i]);
                int hip = (int)AnatomyData.Hip(sd);
                Ch.SetDesired(hip, 1, Ch.Desired(hip).y + _leg[i] * 0.25f);
                Ch.SetStiffness(AnatomyData.Hip(sd), DlCfg.CatchLegsStiffness, 0.5f);
                Ch.SetStiffness(AnatomyData.Knee(sd), DlCfg.CatchLegsStiffness, 0.5f);
            }
            _lean2 = BMath.Range(perf.Rng, -0.3f, 0.6f);
        }

        public void Tick(float t, float dt, float weight)
        {
            if (!Active) return;
            float fl = Mathf.Max(0.5f, _armK[0]), fr = Mathf.Max(0.5f, _armK[1]);
            float ke = B.Kinetic;
            _k = Mathf.Clamp((((Mathf.Sqrt(2f * ke) * 2f - _k) * dt) * 0.5f + _k), 0.6f, 1f);
            float k = _k * Mathf.Clamp01(weight);
            if (k <= 0.02f) return;

            Ch.SetStiffness("us", DlCfg.CatchTorsoStiffness * k, 0.5f);
            Ch.SetStiffness("un", DlCfg.CatchTorsoStiffness * k, 0.5f);
            bool looseL = Perf.Shot != null && Perf.Shot.LooseArms[0];
            bool looseR = Perf.Shot != null && Perf.Shot.LooseArms[1];
            if (!Ch.Busy[0] && !looseL) Ch.SetStiffness("ul", fl * DlCfg.CatchArmsStiffness, 1f);
            if (!Ch.Busy[1] && !looseR) Ch.SetStiffness("ur", fr * DlCfg.CatchArmsStiffness, 1f);
            for (int i = 0; i < 2; i++)
            {
                if (Perf.Shot != null && Perf.Shot.CollapseLegs[i]) continue;
                int sd = i == 0 ? -1 : 1;
                float b = Perf.Balance != null ? Perf.Balance.LegBuckle[i] : 0f;
                float kneeK = Mathf.Lerp(DlCfg.CatchLegsStiffness, 2.2f, b) * k;
                Ch.SetStiffness(AnatomyData.Hip(sd), DlCfg.CatchLegsStiffness * k, 0.5f);
                Ch.SetStiffness(AnatomyData.Knee(sd), kneeK, 0.5f);
            }
            Ch.SetMuscleStiffness((int)JointId.Lumbar, 0.7f);

            // Fall direction (Delirium nmcf.rs:191-198): horizontal component of thorax velocity relative
            // to the arms + constant downward bias (-FALL_G * bias * up) to always probe toward the ground.
            Vector3 under = (_armVel(0) + _armVel(1)) * 0.5f;
            Vector3 s3 = B.Part(Seg.Thorax).V;
            Vector3 d = s3 - under;
            float du = Vector3.Dot(Vector3.up, d);
            // Delirium: (-FALL_G).fma(kv, du) - du = -FALL_G * kv (downward) added to horizontal velocity + descent.
            Vector3 downBias = Vector3.down * (1.25f + Mathf.Max(0f, -du) * 0.4f);
            _dir = BMath.SafeNormalize(DlBody.Horiz(d) + downBias, Vector3.down);
            // In EFT, Sensors.Fwd points forward (opposite of GTA IV's m.c): forward > 0 in a forward fall.
            _forward = Vector3.Dot(Sensors.Fwd, _dir);
            _upright = Mathf.Clamp01(Vector3.Dot(Vector3.up, B.Part(Seg.Thorax).U));

            float back = -Vector3.Dot(Sensors.Fwd, B.Vel - under);
            float curl = Mathf.Clamp01(Mathf.Clamp01(2f * Mathf.Max(0f, back)) * _upright - _forward * 0.5f);

            // Curled trunk, twist decays if no spineTwist is active.
            for (int j = 0; j < DlCharacter.NJ; j++)
            {
                if (!IsSpine(j)) continue;
                Ch.SetRelative(j, 0, curl * k * 0.6f);
                Ch.SetRelative(j, 1, _lean2);
                Ch.SetDesired(j, 2, Ch.Desired(j).z * (1f / (1f + 0.35f * dt)));
            }
            // Forward fall: head raised (extension); backward fall: chin tucked (flexion) to protect the occiput.
            Ch.SetRelative((int)JointId.Neck, 0, _forward > 0.35f ? -0.45f : _forward < -0.35f ? 0.6f : 0.2f);

            // Tucked legs, cushioning knees (positive knee = flexion in SPT convention).
            float onBack = Mathf.Clamp01(Vector3.Dot(Sensors.Fwd, Vector3.up));
            float bend = (onBack + 1f) * k - onBack;
            for (int i = 0; i < 2; i++)
            {
                int sd = i == 0 ? -1 : 1;
                int hip = (int)AnatomyData.Hip(sd), knee = (int)AnatomyData.Knee(sd);
                Ch.BlendToZero(hip, 1f);
                Ch.SetRelative(hip, 0, (0.15f + 0.3f * _leg[i] + 0.35f * curl) * k);
                Ch.SetRelative(knee, 0, Mathf.Clamp(0.35f + 0.45f * _leg[i] * bend, 0f, 1f));
            }

            // Arms: probe toward ground / obstacle in the fall direction, palm pronation facing the normal,
            // stiffness reduced on contact (armReduceSpeed). Skips an arm already gripping scenery (Ch.Busy).
            var thorax = B.Part(Seg.Thorax);
            Vector3 trunkAxis = thorax.U;
            for (int i = 0; i < 2; i++)
            {
                int sd = i == 0 ? -1 : 1;
                int kk = NmCharacter.Si(sd);
                ArmHitSurface[kk] = false;
                if (Ch.Busy[kk] || (Perf.Shot != null && Perf.Shot.LooseArms[kk])) continue;
                int shoulder = (int)AnatomyData.Shoulder(sd);
                Vector3 sh = B.JointPos((JointId)shoulder);
                _armK[kk] = Mathf.Min(1f, _armK[kk] + dt * 2f);
                // Delirium nmcf.rs:278-285: offset along trunk axis (out), probe along _dir.
                Vector3 outAxis = _forward > 0f
                    ? BMath.SafeNormalize(trunkAxis + Sensors.Fwd * (0.4f * _upright), trunkAxis)
                    : trunkAxis;
                float offset = Mathf.Clamp(_forward, DlCfg.CatchBackwardsMinArmOffset, DlCfg.CatchForwardMaxArmOffset);
                Vector3 from = sh + outAxis * offset;
                bool hit = DlPhys.Cast(from, _dir, DlCfg.ArmProbeLength, out Vector3 at, out Vector3 hitNormal);
                ArmHitSurface[kk] = hit;
                bool touching = Sensors.HandContact[kk];
                if (touching) _armK[kk] = Mathf.Max(0.18f, _armK[kk] - 2.5f * dt);
                Vector3 aim = touching && hit ? (at + Sensors.Hand[kk]) * 0.5f : at;
                Vector3 delta = aim - sh;
                float len = delta.magnitude;
                if (len > DlCfg.ArmReachLength) delta *= DlCfg.ArmReachLength / len;
                else if (len < 0.2f && len > 1e-4f) delta *= 0.2f / len;
                ArmTo(sd, sh + delta, 1f, DlCfg.CatchArmsStiffness * Mathf.Max(0.4f, _armK[kk]), 1f);
                // Forearm pronation (Shot::wrist nmcf.rs:336-340): turns palm to face the ground / wall.
                Vector3 palmNormal = hit ? hitNormal : Vector3.up;
                float pron = DlPhys.ForearmPronationForNormal(B.Body, sd, palmNormal);
                Ch.BlendDesired((int)AnatomyData.Elbow(sd), 2, pron, hit ? 0.85f : 0.5f);
                if (_forward >= 0f && touching)
                    Ch.BlendDesired((int)JointId.Lumbar, 0, -0.25f, 0.35f);
                ClavicleFollow(sd);
            }

            // Neck looks at the landing point (Delirium: headLook toward head.pos + dir; Atan2 already in radians!).
            Vector3 look = B.Part(Seg.Head).Com + _dir;
            Vector3 neckTarget = look - B.JointPos(JointId.Neck);
            float yaw = Mathf.Atan2(Vector3.Dot(neckTarget, Sensors.Right), Vector3.Dot(neckTarget, Sensors.Fwd));
            float pitchLook = Mathf.Atan2(Vector3.Dot(neckTarget, Vector3.up), Horiz(neckTarget).magnitude);
            Ch.BlendDesired((int)JointId.Neck, 0, Mathf.Clamp(-pitchLook, -0.7f, 0.7f), 0.6f);
            Ch.BlendDesired((int)JointId.Neck, 2, Mathf.Clamp(yaw, -0.8f, 0.8f), 0.6f);

            // On contact, the neck braces: head aligns with the body axis (Delirium: touched).
            if (Sensors.HandContact[0] || Sensors.HandContact[1]) _touched = true;
            if (_touched)
            {
                Vector3 v = -_dir;
                Ch.BlendDesired((int)JointId.Neck, 0, Mathf.Asin(Mathf.Clamp(Vector3.Dot(v, Sensors.Fwd), -1f, 1f)), 0.5f);
                Ch.BlendDesired((int)JointId.Neck, 1, -Mathf.Asin(Mathf.Clamp(Vector3.Dot(v, Sensors.Right), -1f, 1f)), 0.5f);
            }
        }

        private Vector3 _armVel(int i)
        {
            var fore = B.Body[i == 0 ? Seg.LForearm : Seg.RForearm];
            return fore.V;
        }

        private static bool IsSpine(int j) => j == (int)JointId.Lumbar;
    }

    /// <summary>
    /// bodyRollUp, ported from Delirium's <c>nmru::RollUp</c>: asymmetric curling, dynamic leg
    /// push modulated by torso orientation (<c>legPush</c>), and arms reaching toward the ground in the
    /// fall direction to cushion/slow the roll (<c>useArmToSlowDown</c>, <c>armReachAmount</c>).
    /// </summary>
    internal sealed class DlRollUp : DlBehaviour
    {
        public float Stiffness = DlCfg.RollUpStiffness;
        public float Damping = 0.5f;
        public float LegPush = 1f;
        public float ArmReachAmount = 1.4f;
        public float ArmReachLength = 0.55f;
        public float UseArmToSlowDown = -0.6f;
        public float AsymmetricalLegs = 0.45f;
        public string Mask = "fb";

        private float _t, _roll, _still, _asym, _rate, _phase, _shoulder;
        private readonly float[] _reach = new float[2];

        public DlRollUp(NmPerformance perf, float t, float stiffness = DlCfg.RollUpStiffness) : base(perf, "bodyRollUp", t)
        {
            Stiffness = stiffness;
            RollAsym();
            _rate = BMath.Range(perf.Rng, 3.2f, 5.5f);
            _phase = BMath.Range(perf.Rng, 0f, BMath.TwoPi);
            _shoulder = BMath.Range(perf.Rng, -0.6f, 0f);
            var keep = Ch.Lock(Mask);
            Ch.SetStiffness("fb", Stiffness, Damping, 0.7f);
            Ch.SetStiffness("ua", Stiffness + 2f, 1f);
            Ch.SetStiffness("uc", Stiffness + 2f, 1f);
            Ch.Unlock(Mask, keep);
        }

        private void RollAsym()
        {
            float x = BMath.Range(Perf.Rng, -1f, 1f);
            _asym = (x >= 0f ? 1f : -1f) * AsymmetricalLegs - AsymmetricalLegs * x * 0.3f;
        }

        public void Tick(float t, float dt, float weight)
        {
            if (!Active) return;
            float w = Mathf.Clamp01(weight);
            if (w <= 0.02f) return;
            _t += dt;
            var keep = Ch.Lock(Mask);

            var th = B.Part(Seg.Thorax);
            float spin = Mathf.Clamp(Vector3.Dot(th.U, B.AngVel) * 0.25f, -1f, 1f);
            float speed = B.AngVel.magnitude + B.Vel.magnitude;
            float sp = speed * 0.5f;
            float k = (_roll * 2f + Mathf.Clamp01(sp)) / 3f;
            _roll = k;
            if (sp < 0.3f) _still += dt; else _still = 0f;

            // Predicted torso orientation: when legs swing toward the ground, legPush extends them to sustain the roll.
            Quaternion dq = Quaternion.AngleAxis(B.AngVel.magnitude * 0.15f * Mathf.Rad2Deg,
                BMath.SafeNormalize(B.AngVel, Vector3.up));
            Vector3 fPred = dq * th.F;
            float uOrient = Vector3.Dot(Vector3.up, fPred);
            float push = uOrient * LegPush * -0.5f;
            if (uOrient > 0f && uOrient < 0.2f && B.AngVel.magnitude > 2.2f) RollAsym();

            float s = Mathf.Clamp(Stiffness + (1f - k) * 2f, 4f, 15f) * w;
            Ch.SetStiffness("fb", s, Damping);

            float rt = _rate * _t;
            float sl = Mathf.Sin(rt + _phase), sr = Mathf.Sin(rt * 1.15f + _phase + 1.1f);
            float slow = Mathf.Clamp(Mathf.Abs(UseArmToSlowDown) - Vector3.Dot(Vector3.up, -th.F) - 0.5f, 0f, 1f);
            float armL = UseArmToSlowDown * spin > 0f ? 1f - Mathf.Abs(spin) * slow : 1f;
            float armR = UseArmToSlowDown * spin <= 0f ? 1f - Mathf.Abs(spin) * slow : 1f;

            // Asymmetric curled legs + rhythmic push (nmru.rs:135-146).
            for (int i = 0; i < 2; i++)
            {
                int sd = i == 0 ? -1 : 1;
                float a = i == 0 ? _asym : -_asym;
                float osc = i == 0 ? sl : sr;
                int hip = (int)AnatomyData.Hip(sd), knee = (int)AnatomyData.Knee(sd);
                int sh = (int)AnatomyData.Shoulder(sd), el = (int)AnatomyData.Elbow(sd);
                Ch.SetRelative(hip, 0, Mathf.Clamp((0.8f - push) * k + a * 0.35f + 0.15f, -0.4f, 1.1f));
                Ch.SetRelative(hip, 1, 0.15f * sd + a * 0.2f);
                Ch.SetRelative(knee, 0, Mathf.Clamp((0.85f - push * 0.4f + osc * 0.15f) * k + 0.25f, 0.1f, 1.1f));
                if (!Ch.Busy[i])
                {
                    float armF = i == 0 ? armL : armR;
                    Ch.SetRelative(sh, 0, Mathf.Clamp(armF * 0.55f + _shoulder * 0.3f, -0.5f, 1f));
                    Ch.SetRelative(sh, 1, 0.25f);
                    Ch.SetRelative(sh, 2, -0.4f * Motor.InternalRotSign(sd));
                    Ch.SetRelative(el, 0, Mathf.Clamp(0.45f + 0.35f * k, 0.15f, 1f));
                    Ch.SetRelative(el, 2, 0.5f * Motor.InternalRotSign(sd));
                }
            }

            Ch.SetRelative((int)JointId.Lumbar, 0, (0.35f + 0.35f * k) * w);
            Ch.SetRelative((int)JointId.Lumbar, 2, _asym * 0.35f * w);
            Ch.SetRelative((int)JointId.Neck, 0, (0.35f + 0.25f * k) * w);
            Ch.SetOpposeGravity((int)JointId.Lumbar, 0f);

            // Cushioning arms (Delirium nmru.rs:167-202): reach toward the fall direction to protect the head.
            for (int i = 0; i < 2; i++)
            {
                if (Ch.Busy[i]) continue;
                int sd = i == 0 ? -1 : 1;
                Vector3 at = B.JointPos(AnatomyData.Shoulder(sd));
                float rise = Vector3.Dot(Vector3.up, th.V);
                float outW = rise > 0f ? Mathf.Clamp01(1f - 0.5f * rise) : 1f;
                Vector3 dir = BMath.SafeNormalize(th.V + Vector3.down * 1.5f + th.U + th.F, Vector3.down);
                Vector3 p = at + dir * 0.5f + Vector3.Cross(th.W, Vector3.up) * 0.15f;
                Vector3 aimDir = BMath.SafeNormalize(p - at, dir);
                Vector3 target = at + aimDir * ArmReachLength;
                Vector3 n = BMath.SafeNormalize(th.F * 2f + th.R * sd, th.F);
                float d = Vector3.Dot(n, aimDir);
                float reachW = Mathf.Clamp01((d - (0.6f - ArmReachAmount * 0.4f)) * 2f);
                _reach[i] = Mathf.Lerp(_reach[i], reachW, 1f - Mathf.Exp(-dt * 8f));
                float blend = outW * _reach[i] * w;
                if (blend > 0.08f)
                {
                    ArmTo(sd, target, blend * 0.75f, Mathf.Clamp(Stiffness + 2f * blend, 6f, 15f), 1f);
                    float pron = DlPhys.ForearmPronationForNormal(B.Body, sd, Vector3.up);
                    Ch.BlendDesired((int)AnatomyData.Elbow(sd), 2, pron, blend * 0.7f);
                }
            }

            Ch.Unlock(Mask, keep);
        }
    }

    /// <summary>
    /// rollDownStairs, ported from Delirium's <c>nmrds::RollDownStairs</c>: drives <see cref="DlRollUp"/> for the
    /// curled posture + cushioning arms, follows smoothed movement (or imposed) direction, applies the
    /// roll-assist torque in <see cref="Forces"/>, and reduces parasitic spin around the vertical axis.
    /// </summary>
    internal sealed class DlRollDownStairs : DlBehaviour
    {
        public bool SpinWhenInAir = false;
        public float ForceMag = 0.65f;
        public float ZAxisSpinReduction = 0.35f;

        private Vector3 _dir, _sum;
        private readonly bool _customDir;
        private readonly float _maxAge;
        private readonly DlRollUp _rollUp;
        private float _slope;

        /// <param name="maxAge">Roll lifetime: a ground roll stops sooner than a fall.</param>
        public DlRollDownStairs(NmPerformance perf, float t, Vector3 dir, float maxAge = 4f, bool customDir = true) : base(perf, "rollDownStairs", t)
        {
            _maxAge = maxAge;
            _customDir = customDir;
            perf.Log(t, maxAge < 4f ? "rollDownStairs (on ground)" : "rollDownStairs");
            _dir = BMath.SafeNormalize(dir, Sensors.Fwd);
            _sum = Vector3.down;
            _rollUp = new DlRollUp(perf, t, DlCfg.RollStiffness)
            {
                Damping = DlCfg.RollDamping,
                LegPush = 1f,
                ArmReachAmount = 1.4f,
                AsymmetricalLegs = BMath.Range(perf.Rng, 0.25f, 0.65f),
            };
            Ch.SetStiffness("fb", DlCfg.RollStiffness, DlCfg.RollDamping);
        }

        public void Tick(float t, float dt, float weight)
        {
            if (!Active) return;
            float w = Mathf.Clamp01(weight);
            if (w <= 0.02f) return;
            // The roll stops by itself when the body finishes spinning (Delirium stops the sub-behavior
            // based on velocity, not at the instant of contact).
            float speedNow = Horiz(B.Vel).magnitude;
            if (t - Start > _maxAge || (t - Start > 1.2f && speedNow < 0.45f && B.AngVel.magnitude < 1.2f))
            {
                Stop(t);
                return;
            }
            _sum = BMath.SafeNormalize(_sum + B.Vel, _dir) * 0.85f;
            if (!_customDir && _sum.sqrMagnitude > 1e-4f) _dir = _sum.normalized;
            _slope = Mathf.Asin(Mathf.Clamp(Vector3.Dot(_dir, Vector3.up), -1f, 1f));

            _rollUp.Tick(t, dt, w);
        }

        /// <summary>Roll torque faithful to <c>nmrds::RollDownStairs::roll_force</c> (applied in Forces).</summary>
        public void Forces(float t, float dt, float weight)
        {
            if (!Active) return;
            float w = Mathf.Clamp01(weight);
            if (w <= 0.02f) return;
            if (Sensors.Airborne && !SpinWhenInAir) return;
            Vector3 axis = Vector3.Cross(Vector3.up, _dir);
            if (axis.sqrMagnitude < 1e-4f) return;
            axis.Normalize();

            bool torsoLow = (B.Part(Seg.Pelvis).Com.y - Sensors.GroundY < 0.78f)
                          || (B.Part(Seg.Thorax).Com.y - Sensors.GroundY < 0.85f)
                          || Sensors.HandContact[0] || Sensors.HandContact[1];
            bool touching = torsoLow || Sensors.Contact[0] || Sensors.Contact[1];
            if (!touching && !SpinWhenInAir) return;

            float speed = Horiz(B.Vel).magnitude;
            float spin = B.AngVel.magnitude;
            float wNow = Vector3.Dot(B.AngVel, axis);
            bool upright = !torsoLow && Vector3.Dot(B.Part(Seg.Thorax).U, Vector3.up) > 0.65f;
            float groundScale = upright ? 0.85f : 1f;
            float targetW = Mathf.Clamp(speed / 0.50f, 2.6f, 5.2f);
            if (spin > targetW + 0.8f && wNow > 0f) return;
            float alphaMag = Mathf.Clamp((targetW - wNow) * 4.2f, -13.5f, 13.5f) * ForceMag * groundScale * w;
            DlPhys.BodyAngularAccel(B, axis * alphaMag);

            // Reduction of parasitic spin around the vertical axis (nmcf::spin_reduction).
            if (ZAxisSpinReduction > 0f && torsoLow)
            {
                float yawW = Vector3.Dot(B.AngVel, Vector3.up);
                Vector3 antiSpin = Vector3.up * (-yawW * B.Mass * ZAxisSpinReduction * 0.25f * w);
                B.Torque(Seg.Thorax, antiSpin * 0.6f);
                B.Torque(Seg.Pelvis, antiSpin * 0.4f);
            }
        }

        public override void Stop(float t)
        {
            Active = false;
            _rollUp.Stop(t);
        }
    }

    /// <summary>
    /// armsWindmill (Delirium: <c>nmaw::Windmill</c>): phase-offset sagittal circles between left and right arms,
    /// direction driven by trunk angular velocity, individual arm stop on contact (<c>disableOnImpact</c>),
    /// and counter-twist of spine and neck in phase with the windmilling.
    /// </summary>
    internal sealed class DlWindmill : DlBehaviour
    {
        public bool DisableOnImpact = true;
        public float PhaseOffset;
        private float _ph;
        private readonly bool[] _hit = new bool[2];
        private readonly bool[] _going = { true, true };

        public DlWindmill(NmPerformance perf, float t) : base(perf, "armsWindmill", t)
        {
            _ph = BMath.Range(perf.Rng, 0f, BMath.TwoPi);
            // Natural phase offset (in opposition, never robotic-synchronous).
            PhaseOffset = BMath.Range(perf.Rng, 2.2f, 4.0f);
        }

        public void Tick(float t, float dt, float weight)
        {
            if (!Active) return;
            float u = Mathf.Clamp01(weight);
            if (u <= 0.02f) return;

            for (int i = 0; i < 2; i++)
            {
                if (DisableOnImpact && !_hit[i] && Sensors.HandContact[i]) _hit[i] = true;
                if (DisableOnImpact && _hit[i]) _going[i] = false;
            }
            if (!_going[0] && !_going[1])
            {
                // Delirium nmaw.rs:68-73: when both arms have touched, stiffness drops.
                Ch.SetStiffness("ua", DlCfg.WindmillShoulderStiffness * 0.5f, 1f);
                return;
            }

            var th = B.Part(Seg.Thorax);
            float dirSign = Vector3.Dot(B.AngVel, Sensors.Right) >= 0f ? -1f : 1f;
            float omega = BMath.TwoPi * (1.05f + Mathf.Min(1.5f, th.W.magnitude * 0.3f)) * dirSign;
            _ph += omega * dt;
            for (int i = 0; i < 2; i++)
            {
                if (!_going[i] || Ch.Busy[i] || (Perf.Shot != null && Perf.Shot.LooseArms[i])) continue;
                int sd = i == 0 ? -1 : 1;
                float at = i == 0 ? _ph : _ph + PhaseOffset;
                Vector3 sh = B.JointPos(AnatomyData.Shoulder(sd));
                Vector3 c = sh + th.U * 0.12f + th.F * 0.16f + th.R * (sd * 0.16f);
                float r = 0.34f;
                Vector3 target = c + th.F * (r * Mathf.Cos(at)) + th.U * (r * Mathf.Sin(at));
                ArmTo(sd, target, u, DlCfg.WindmillShoulderStiffness, 1f);
                Ch.SetStiffness(AnatomyData.Elbow(sd), DlCfg.WindmillElbowStiffness, 1f);
                // Oscillating pronation-supination during windmilling.
                Ch.BlendDesired((int)AnatomyData.Elbow(sd), 2, Mathf.Cos(at) * 0.7f * Motor.InternalRotSign(sd), u * 0.7f);
            }
            // Spine and neck counter-twist (Delirium nmaw.rs:95-99).
            float s = Mathf.Sin(_ph);
            Ch.BlendDesired((int)JointId.Lumbar, 2, s * 0.22f, u * 0.7f);
            Ch.BlendDesired((int)JointId.Neck, 2, -s * 0.3f, u * 0.6f);
            Ch.SetStiffness("us", DlCfg.BalanceBodyStiffness, 0.5f);
        }
    }

    /// <summary>pedalLegs: ankles driven on desynchronized circles (Delirium: nmpd).</summary>
    internal sealed class DlPedal : DlBehaviour
    {
        private readonly float[] _ph = { 0f, Mathf.PI };
        private readonly float[] _asym = { 1f, 0.92f };

        public DlPedal(NmPerformance perf, float t) : base(perf, "pedalLegs", t)
        {
            for (int k = 0; k < 2; k++) _ph[k] += BMath.Range(perf.Rng, -0.5f, 0.5f);
        }

        public void Tick(float t, float dt, float weight)
        {
            if (!Active) return;
            float u = Mathf.Clamp01(weight);
            if (u <= 0.02f) return;
            var pv = B.Part(Seg.Pelvis);
            for (int sd = -1; sd <= 1; sd += 2)
            {
                int k = NmCharacter.Si(sd);
                _ph[k] += 3.5f * _asym[k] * dt;
                float r = 0.22f * (1f + 0.2f * Mathf.Sin(_ph[k] * 0.37f + k));
                Vector3 hip = B.JointPos(AnatomyData.Hip(sd));
                Vector3 c = hip - pv.U * 0.45f + pv.F * 0.15f;
                Vector3 target = c + pv.U * (Mathf.Sin(_ph[k]) * r) + pv.F * (Mathf.Cos(_ph[k]) * r);
                if (!Nm.LegIK(B.Body, sd, hip, target, pv.R, pv.U, pv.F, out float f, out float ab, out float kn)) continue;
                Ch.SetStiffness(AnatomyData.Hip(sd), 10f, 1f);
                Ch.SetStiffness(AnatomyData.Knee(sd), 10f, 1f);
                Ch.BlendDesired((int)AnatomyData.Hip(sd), 0, f * BMath.Deg, u);
                Ch.BlendDesired((int)AnatomyData.Hip(sd), 1, ab * BMath.Deg, u);
                Ch.BlendDesired((int)AnatomyData.Knee(sd), 0, Mathf.Max(8f, kn) * BMath.Deg, u);
            }
        }
    }

    /// <summary>
    /// bodyWrithe, ported from <c>nmbw::Writhe</c>: each joint drifts on smoothed noise (distinct
    /// periods for arms, back, and legs), with limb tucking and side-to-side rocking.
    /// </summary>
    internal sealed class DlWrithe : DlBehaviour
    {
        private readonly float _seed, _gain;
        private float _t, _noiseT, _rollT, _rollForce, _rollTimer, _lastU;
        public bool RollOver = true;

        public DlWrithe(NmPerformance perf, float t, float gain) : base(perf, "bodyWrithe", t)
        {
            _gain = gain;
            _seed = BMath.Range(perf.Rng, 0f, 100f);
            _noiseT = BMath.Range(perf.Rng, 0f, 100f);
            _rollT = BMath.Range(perf.Rng, 0f, 10f);
            _rollForce = BMath.Chance(perf.Rng, 0.5f) ? 1f : -1f;
            _rollTimer = 1.2f;
            for (int k = 0; k < DlCharacter.NJ; k++) Ch.SetMuscleStiffness(k, 0.6f);
            Ch.SetStiffness("ua", 5.5f, 0.85f, 0.45f);
            Ch.SetStiffness("uk", DlCfg.WritheBackStiffness, DlCfg.WritheDamping);
            Ch.SetStiffness("lb", DlCfg.WritheLegStiffness, DlCfg.WritheDamping);
        }

        public void Tick(float t, float dt, float intensity, float armsBlend)
        {
            if (!Active) return;
            float u = Mathf.Clamp01(intensity) * _gain * Mathf.Clamp01(Perf.U(t));
            _lastU = u;
            if (u <= 0.02f) return;
            float s = _t * 0.5f;
            float Ph(float n) => n * BMath.TwoPi - BMath.Pi;
            float p16 = Ph(DlPhys.Noise01(s, _noiseT, _seed));
            float p15 = Ph(DlPhys.Noise01(s, s, _seed));
            float p24 = Ph(DlPhys.Noise01(s, _noiseT, s));
            float p23 = Ph(DlPhys.Noise01(s, s, _noiseT));
            float p22 = Ph(DlPhys.Noise01(s, _seed, s));
            float a = _t * BMath.TwoPi;
            float amp = DlPhys.Noise01(_noiseT, a, a);

            for (int i = 0; i < 2; i++)
            {
                int sd = i == 0 ? -1 : 1;
                float[] x =
                {
                    Mathf.Clamp(Mathf.Sin(a + (i == 0 ? p16 : p24)) * amp * 0.5f, -1f, 1f),
                    Mathf.Clamp(Mathf.Cos(a + (i == 0 ? p15 : p23)) * amp * 2f, -1f, 1f),
                    Mathf.Clamp(-(i == 0 ? p24 : p22), -1f, 1f),
                };
                int sh = (int)AnatomyData.Shoulder(sd), el = (int)AnatomyData.Elbow(sd), hip = (int)AnatomyData.Hip(sd), knee = (int)AnatomyData.Knee(sd);
                if (!Ch.Busy[i])
                {
                    Ch.BlendToZero(sh, 0.35f);
                    Ch.BlendDesired(sh, 0, Mathf.Lerp(0f, 38f, 0.5f + 0.5f * x[0]) * BMath.Deg, u * armsBlend);
                    Ch.BlendDesired(sh, 1, Mathf.Lerp(16f, 52f, 0.5f + 0.5f * Mathf.Clamp(x[1] * 0.5f, -1f, 1f)) * BMath.Deg, u * armsBlend);
                    Ch.BlendDesired(sh, 2, Mathf.Clamp(x[2] * 0.35f, -0.35f, 0.35f), u * armsBlend);
                    float elbow = Mathf.Clamp((i == 0 ? Mathf.Sin(a + p22) : Mathf.Cos(a + p23)) * amp, -1f, 1f);
                    Ch.BlendDesired(el, 0, Mathf.Lerp(12f, 55f, 0.5f + 0.5f * elbow) * BMath.Deg, u * armsBlend);
                    // Agonic forearm pronation-supination.
                    Ch.BlendDesired(el, 2, LerpMid(el, 2, Mathf.Sin(a * 0.8f + (i == 0 ? p15 : p24)) * 0.8f), u * armsBlend);
                }

                // Left/right asymmetry faithful to nmbw.rs:131-138 (sin on left, cos on right).
                float g = _t * 2f;
                float la = amp;
                if (i == 0)
                {
                    Ch.BlendDesired(hip, 0, LerpMid(hip, 0, Mathf.Sin(g + p22 + 2f) * la * 0.5f), u);
                    Ch.BlendDesired(knee, 0, LerpMid(knee, 0, -0.5f + Mathf.Cos(g + p15) * 0.5f), u);
                    Ch.BlendDesired(hip, 1, LerpMid(hip, 1, -0.35f - Mathf.Sin(g * 0.5f) * la * 0.3f), u);
                    Ch.BlendDesired(hip, 2, LerpMid(hip, 2, Mathf.Sin(g * 1.3f + 1f) * la * 0.3f), u);
                }
                else
                {
                    Ch.BlendDesired(hip, 0, LerpMid(hip, 0, -Mathf.Cos(g + p23) * la * 0.5f), u);
                    Ch.BlendDesired(knee, 0, LerpMid(knee, 0, -0.5f - Mathf.Sin(g + p24) * 0.5f), u);
                    Ch.BlendDesired(hip, 1, LerpMid(hip, 1, -0.35f - Mathf.Cos(g * 0.5f) * la * 0.3f), u);
                    Ch.BlendDesired(hip, 2, LerpMid(hip, 2, Mathf.Sin(g * 1.4f - 1f) * la * 0.3f), u);
                }
            }
            float sn = Mathf.Sin(a + p16), cs = Mathf.Cos(a + p23);
            int neck = (int)JointId.Neck;
            Ch.BlendDesired(neck, 0, LerpMid(neck, 0, sn * 0.7f), u * 0.8f);
            Ch.BlendDesired(neck, 1, LerpMid(neck, 1, cs * 0.7f), u * 0.8f);
            Ch.BlendDesired(neck, 2, LerpMid(neck, 2, sn), u * 0.8f);
            float b = _t * BMath.TwoPi;
            int lu = (int)JointId.Lumbar;
            Ch.BlendDesired(lu, 0, LerpMid(lu, 0, amp * Mathf.Sin((b + p23) * 0.5f) * 0.2f), u);
            Ch.BlendDesired(lu, 1, LerpMid(lu, 1, p24 * 0.1f), u);
            Ch.BlendDesired(lu, 2, LerpMid(lu, 2, amp * Mathf.Sin(b + p16) * 0.3f), u);

            if (_rollTimer < 0f) { _rollForce = -_rollForce; _rollTimer = 0.5f + BMath.Range(Perf.Rng, 0f, 2f); }
            else _rollTimer -= dt;

            _noiseT = Mathf.Lerp(_noiseT, _seed, 1f - Mathf.Exp(-dt * 0.35f));
            _t += (DlPhys.Noise01(_noiseT, _seed, 0.7f) + 0.5f) * dt;
            _rollT += dt;
        }

        /// <summary>Side-to-side rocking on the ground (Delirium nmbw.rs:139-150), applied in Forces().</summary>
        public void Forces(float t, float dt, float weight)
        {
            if (!Active || !RollOver || _lastU <= 0.02f) return;
            if (B.AngVel.magnitude > 2.5f) return;
            Vector3 up = BMath.SafeNormalize(B.JointPos(JointId.Lumbar) - B.Part(Seg.Pelvis).Pos, Vector3.up);
            float k = Mathf.Clamp01((Mathf.Sin(_rollT * 1.4f) + 1f) * 0.5f - 0.35f);
            if (k <= 0.01f) return;
            Vector3 f = up * (k * _rollForce * 28f * _lastU * Mathf.Clamp01(weight));
            B.Torque(Seg.Thorax, f);
            B.Torque(Seg.Pelvis, f * 0.6f);
        }

        private static float LerpMid(int j, int dof, float rel)
        {
            DlCfg.Range(j, dof, out float lo, out float hi);
            return 0.5f * (lo + hi) + 0.5f * (hi - lo) * Mathf.Clamp(rel, -1f, 1f);
        }
    }

    /// <summary>bodyFoetal, ported from <c>nmbf::Foetal</c>: curling with stiffness asymmetry, back twist, and pronation.</summary>
    internal sealed class DlFoetal : DlBehaviour
    {
        private readonly float[] _asym = { 1f, 1f };
        private readonly float[] _jointS = new float[DlCharacter.NJ];
        private readonly float[] _jointD = new float[DlCharacter.NJ];
        private readonly float _twist;

        public DlFoetal(NmPerformance perf, float t) : base(perf, "bodyFoetal", t)
        {
            float asymAmt = BMath.Range(perf.Rng, 0.15f, 0.5f);
            float a = asymAmt * 0.35f;
            float s0 = (1f - asymAmt * 0.5f) * DlCfg.FoetalStiffness, s1 = (1f + a) * DlCfg.FoetalStiffness;
            float d0 = (1f - a) * DlCfg.FoetalDamping, d1 = (1f + a) * DlCfg.FoetalDamping;
            for (int j = 0; j < DlCharacter.NJ; j++)
            {
                _jointS[j] = BMath.Range(perf.Rng, s0, s1);
                _jointD[j] = BMath.Range(perf.Rng, d0, d1);
            }
            for (int k = 0; k < 2; k++) _asym[k] = 1f + 0.4f * BMath.Range(perf.Rng, -0.5f, 0.5f);
            _twist = BMath.Range(perf.Rng, -0.35f, 0.35f);
        }

        public void Tick(float t, float intensity, float weight)
        {
            if (!Active) return;
            float u = Mathf.Clamp01(intensity) * Mathf.Clamp01(weight);
            if (u <= 0.02f) return;
            for (int j = 0; j < DlCharacter.NJ; j++) Ch.SetStiffness((JointId)j, _jointS[j], _jointD[j]);
            Ch.SetStiffness("ua", 5.5f, 1.0f, 0.5f);
            Ch.BlendDesired((int)JointId.Lumbar, 0, LerpRel((int)JointId.Lumbar, 0, 0.6f), u);
            Ch.BlendDesired((int)JointId.Lumbar, 2, LerpRel((int)JointId.Lumbar, 2, _twist), u);
            Ch.BlendDesired((int)JointId.Neck, 0, LerpRel((int)JointId.Neck, 0, 0.5f), u);
            for (int i = 0; i < 2; i++)
            {
                int sd = i == 0 ? -1 : 1;
                float a = _asym[i];
                int hip = (int)AnatomyData.Hip(sd), knee = (int)AnatomyData.Knee(sd);
                int sh = (int)AnatomyData.Shoulder(sd), el = (int)AnatomyData.Elbow(sd);
                Ch.BlendDesired(hip, 0, LerpRel(hip, 0, 0.85f * a), u);
                Ch.BlendDesired(hip, 1, LerpRel(hip, 1, 0.1f), u);
                Ch.BlendDesired(knee, 0, LerpRel(knee, 0, 0.75f * a), u);
                if (!Ch.Busy[i])
                {
                    Ch.BlendDesired(sh, 0, LerpRel(sh, 0, -0.22f * a), u);
                    Ch.BlendDesired(sh, 1, 22f * BMath.Deg, u);
                    Ch.BlendDesired(sh, 2, 0.20f * Motor.InternalRotSign(sd), u);
                    Ch.BlendDesired(el, 0, LerpRel(el, 0, 0.08f * a), u);
                    Ch.BlendDesired(el, 2, 0.55f * Motor.InternalRotSign(sd), u);
                }
            }
        }

        private static float LerpRel(int j, int dof, float rel)
        {
            DlCfg.Range(j, dof, out float lo, out float hi);
            return 0.5f * (lo + hi) + 0.5f * (hi - lo) * Mathf.Clamp(rel, -1f, 1f);
        }
    }

    /// <summary>
    /// injuredOnGround, ported from <c>nmshot</c>: hands return to reachable wounds, a knee occasionally
    /// pulls up, head follows pain, and the body attempts to roll toward the injury.
    /// </summary>
    internal sealed class DlInjuredOnGround : DlBehaviour
    {
        private float _nextKnee, _kneeT;
        private int _knee;
        private float _rollT0 = -1f, _rollDur;
        private int _rollSide;

        public DlInjuredOnGround(NmPerformance perf, float t) : base(perf, "injuredOnGround", t)
        {
            _nextKnee = t + BMath.Range(perf.Rng, 0.3f, 1.2f);
            _kneeT = t + BMath.Range(perf.Rng, 0.8f, 2.2f);
        }

        public void Tick(float t, float intensity, float weight)
        {
            if (!Active) return;
            float u = Mathf.Clamp01(intensity) * Mathf.Clamp01(weight);
            if (u <= 0.02f) return;
            var th = B.Part(Seg.Thorax);
            float reachFade = 1f - BMath.SmoothStep(1.4f, 2.2f, t - Start);
            int n = 0;
            NmHit last = null;
            for (int i = Perf.Wounds.Count - 1; i >= 0 && n < 2; i--)
            {
                var h = Perf.Wounds[i];
                if (!h.Reachable) continue;
                n++;
                if (last == null) last = h;
                if (reachFade <= 0.02f) continue;
                int side = h.Side != 0 ? -h.Side : (n == 1 ? 1 : -1);
                if (h.ArmSide == side || Nm.ElbowCap(B.Body, side) < 0.25f) side = -side;
                if (Nm.ElbowCap(B.Body, side) < 0.25f || h.ArmSide == side || Ch.Busy[NmCharacter.Si(side)]) continue;
                Vector3 woundNormal = BMath.SafeNormalize(h.World - th.Com, th.F);
                Vector3 target = h.World + woundNormal * 0.05f;
                target = JointKinematics.ClampOutsideThorax(target, B.Part(Seg.Thorax).Pos, th.R, th.U, th.F,
                    B.Body.ThoraxGearChestZ, B.Body.ThoraxYBot, B.Body.ThoraxYTop, B.Body.ThoraxClearRx, BodyModel.ThoraxRyCap, B.Body.ThoraxClearRz, side, 0.025f);
                ArmTo(side, target, u * 0.85f * reachFade, 6.5f, 1f);
                Ch.BlendDesired((int)AnatomyData.Elbow(side), 2, DlPhys.ForearmPronationForNormal(B.Body, side, woundNormal), u * 0.8f * reachFade);
            }
            float supine = Vector3.Dot(th.F, Vector3.up);
            Ch.BlendDesired((int)JointId.Neck, 0, supine > 0.3f ? 0.45f : 0.2f, u * 0.7f);

            if (t > _kneeT)
            {
                _knee = _knee != 0 ? 0 : (BMath.Chance(Perf.Rng, 0.5f) ? -1 : 1);
                _kneeT = t + BMath.Range(Perf.Rng, 0.8f, 1.8f);
            }
            if (_knee != 0 && B.Body.Joints[(int)AnatomyData.Knee(_knee)].Capacity[0] > 0.25f)
            {
                Ch.BlendDesired((int)AnatomyData.Hip(_knee), 0, Ch.Desired((int)AnatomyData.Hip(_knee)).x + 1.0f * u, u * 0.7f);
                Ch.BlendDesired((int)AnatomyData.Knee(_knee), 0, Ch.Desired((int)AnatomyData.Knee(_knee)).x + 1.6f * u, u * 0.7f);
            }
            if (last != null && t > _nextKnee && _rollT0 < 0f)
            {
                _rollT0 = t;
                _rollSide = last.Side != 0 ? last.Side : (BMath.Chance(Perf.Rng, 0.5f) ? -1 : 1);
                _rollDur = BMath.Range(Perf.Rng, 0.6f, 1.1f);
                _nextKnee = t + BMath.Range(Perf.Rng, 1.6f, 3.2f);
            }
            if (_rollT0 >= 0f && t > _rollT0 + _rollDur) _rollT0 = -1f;
        }

        public void Forces(float t, float intensity, float weight)
        {
            if (!Active || _rollT0 < 0f) return;
            float x = Mathf.Clamp01((t - _rollT0) / _rollDur);
            float e = Mathf.Sin(Mathf.PI * x);
            DlPhys.BodyAngularAccel(B, B.Part(Seg.Thorax).U * (_rollSide * 5f * e * Mathf.Clamp01(intensity) * Mathf.Clamp01(weight)));
        }
    }
}
