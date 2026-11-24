using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Euphoria
{
    /// <summary>
    /// Wall brace, slump, and low-wall fall-over, ported from Delirium's <c>balancerCollisionsReaction</c>,
    /// <c>nmbr::Brace</c>, and <c>nmfow::FallOverWall</c>.
    ///
    /// When a staggering or falling character encounters a vertical wall, the nearest/rear hand(s) reach
    /// toward the surface with palm pronation, cushion impact velocity without rubbery bouncing, and slump
    /// smoothly down the wall by progressively reducing leg stiffness. If the obstacle is a low wall
    /// (clear air above hip/waist height), bounded over-edge torque tips the body cleanly over the wall.
    /// </summary>
    internal sealed class DlWallBrace : DlBehaviour
    {
        public bool BracingWall { get; private set; }
        public bool Slumping { get; private set; }
        public bool FallingOverWall { get; private set; }
        public Vector3 WallPoint { get; private set; }
        public Vector3 WallNormal { get; private set; }

        private readonly bool _isLowWall;
        private readonly bool[] _reaching = new bool[2];
        private float _nearWallTime;
        private float _age;
        private bool _impactReduced;

        public DlWallBrace(NmPerformance perf, float t, Vector3 point, Vector3 normal, bool isLowWall)
            : base(perf, isLowWall ? "fallOverWall" : "balancerCollisionsReaction", t)
        {
            WallPoint = point;
            WallNormal = BMath.SafeNormalize(Horiz(normal), -perf.Ch.Fwd);
            _isLowWall = isLowWall;
            if (isLowWall) FallingOverWall = true;
        }

        /// <summary>
        /// Probes around the pelvis and thorax for a vertical wall or low obstacle in the movement/fall direction.
        /// Ignores flat ground and Brutality-managed ragdoll colliders.
        /// </summary>
        public static bool ProbeWall(DlBody b, NmCharacter sensors, Vector3 moveHint, out Vector3 pt, out Vector3 nrm, out bool isLowWall)
        {
            pt = Vector3.zero;
            nrm = Vector3.zero;
            isLowWall = false;

            var pv = b.Part(Seg.Pelvis);
            var th = b.Part(Seg.Thorax);
            Vector3 moveDir = BMath.SafeNormalize(Horiz(moveHint), -sensors.Fwd);
            Vector3 right = Vector3.Cross(Vector3.up, moveDir);
            if (right.sqrMagnitude < 1e-4f) right = sensors.Right;
            right.Normalize();

            Vector3[] dirs =
            {
                moveDir,
                BMath.SafeNormalize(-sensors.Fwd, moveDir),
                BMath.SafeNormalize(moveDir + right * 0.55f, moveDir),
                BMath.SafeNormalize(moveDir - right * 0.55f, moveDir),
                right,
                -right,
            };

            float waistY = Mathf.Max(sensors.GroundY + 0.55f, pv.Com.y + 0.05f);
            float chestY = Mathf.Max(waistY + 0.25f, th.Com.y + 0.10f);
            float bestDist = float.MaxValue;
            bool found = false;

            for (int i = 0; i < dirs.Length; i++)
            {
                Vector3 d = dirs[i];
                for (int h = 0; h < 2; h++)
                {
                    Vector3 origin = h == 0
                        ? new Vector3(pv.Com.x, waistY, pv.Com.z)
                        : new Vector3(th.Com.x, chestY, th.Com.z);
                    if (!DlPhys.Cast(origin, d, DlCfg.BraceSearchDist, out Vector3 hitPt, out Vector3 hitNrm))
                        continue;
                    if (Mathf.Abs(Vector3.Dot(hitNrm, Vector3.up)) >= 0.45f) continue;

                    float dist = Vector3.Distance(origin, hitPt);
                    if (dist < 0.12f || dist > DlCfg.BraceSearchDist) continue;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        pt = hitPt;
                        nrm = BMath.SafeNormalize(Horiz(hitNrm), -d);
                        found = true;
                        // Low wall detection (nmfow): waist ray hit, check if space 0.50 m above hit is clear.
                        Vector3 overOrigin = new Vector3(origin.x, hitPt.y + 0.50f, origin.z);
                        bool upperHit = DlPhys.Cast(overOrigin, d, dist + 0.35f, out Vector3 upPt, out _);
                        isLowWall = h == 0 && (!upperHit || Vector3.Distance(overOrigin, upPt) > dist + 0.25f);
                    }
                }
            }
            return found;
        }

        public void Tick(float t, float dt, float weight)
        {
            if (!Active) return;
            float u = Mathf.Clamp01(weight);
            if (u <= 0.05f) { Stop(t); return; }
            _age += dt;
            if (_age > 3.2f) { Stop(t); return; }

            var pv = B.Part(Seg.Pelvis);
            var th = B.Part(Seg.Thorax);
            float distN = Vector3.Dot(th.Com - WallPoint, WallNormal);
            float pelDistN = Vector3.Dot(pv.Com - WallPoint, WallNormal);
            float minDist = Mathf.Min(distN, pelDistN);

            if (minDist > DlCfg.BraceSearchDist + 0.45f)
            {
                if (Perf.Balance != null) Perf.Balance.WallBracing = false;
                if (_age > 0.6f) Stop(t);
                return;
            }

            if (_isLowWall)
            {
                FallingOverWall = true;
                Ch.SetStiffness("us", DlCfg.BraceBodyStiffness * u, 0.8f);
                // Arch/lean backward/sideways over the low wall edge and loosen legs slightly so torso tips over.
                Ch.BlendDesired((int)JointId.Lumbar, 0, -0.25f, 0.55f * u);
                if (minDist < 0.65f)
                    Perf.Balance?.ReduceLegStiffness(NmCollisions.SlumpLegStiffRate * 0.7f * dt);
                return;
            }

            // Full wall brace & slump (balancerCollisionsReaction).
            bool nearWall = minDist < 0.70f;
            if (nearWall)
            {
                BracingWall = true;
                _nearWallTime += dt;
                if (Perf.Balance != null) Perf.Balance.WallBracing = true;
                if (!_impactReduced && minDist < 0.55f)
                {
                    _impactReduced = true;
                    Perf.Balance?.ReduceLegStiffness(NmCollisions.ImpactLegStiffReduction);
                    Perf.Log(t, "wall brace impact");
                }
            }

            // Reach toward the wall with arm(s) not already gripped by DlGrab (Ch.Busy) or limp.
            Vector3 tangent = Vector3.Cross(WallNormal, Vector3.up);
            if (tangent.sqrMagnitude < 1e-4f) tangent = Sensors.Right;
            tangent.Normalize();

            Ch.SetStiffness("us", DlCfg.BraceBodyStiffness * u, 0.85f);
            for (int i = 0; i < 2; i++)
            {
                _reaching[i] = false;
                int sd = i == 0 ? -1 : 1;
                if (Ch.Busy[i] || (Perf.Shot != null && Perf.Shot.LooseArms[i])) continue;
                if (Nm.ElbowCap(B.Body, sd) < 0.25f) continue;

                Vector3 sh = B.JointPos(AnatomyData.Shoulder(sd));
                Vector3 wallPlanePt = WallPoint + tangent * Vector3.Dot(sh - WallPoint, tangent);
                wallPlanePt.y = Mathf.Clamp(sh.y - 0.08f, Sensors.GroundY + 0.45f, sh.y + 0.18f);
                float shDist = Vector3.Distance(sh, wallPlanePt);
                if (shDist > 1.05f) continue;

                _reaching[i] = true;
                Vector3 aim = wallPlanePt + WallNormal * 0.03f;
                ArmTo(sd, aim, 0.85f * u, DlCfg.BraceArmStiffness, 1f);
                float pron = DlPhys.ForearmPronationForNormal(B.Body, sd, WallNormal);
                Ch.BlendDesired((int)AnatomyData.Elbow(sd), 2, pron, 0.85f * u);
                Ch.SetOpposeGravity((int)AnatomyData.Shoulder(sd), 0.9f);
                Ch.SetOpposeGravity((int)AnatomyData.Elbow(sd), 0.9f);
            }

            // Look slightly toward or along the wall and tuck chin softly.
            Ch.BlendDesired((int)JointId.Neck, 0, 0.18f, 0.45f * u);

            // Progressive slump down the wall (NmCollisions.SlumpLegStiffRate).
            if (_nearWallTime > 0.18f)
            {
                if (!Slumping)
                {
                    Slumping = true;
                    Perf.Log(t, "wall slump");
                }
                Perf.Balance?.ReduceLegStiffness(NmCollisions.SlumpLegStiffRate * dt);
                float slumpW = Mathf.Clamp01((_nearWallTime - 0.18f) / 0.8f) * u;
                for (int sd = -1; sd <= 1; sd += 2)
                {
                    int hip = (int)AnatomyData.Hip(sd), knee = (int)AnatomyData.Knee(sd);
                    Ch.BlendDesired(hip, 0, Mathf.Max(Ch.Desired(hip).x, 0.65f * slumpW), 0.55f * slumpW);
                    Ch.BlendDesired(knee, 0, Mathf.Max(Ch.Desired(knee).x, 1.05f * slumpW), 0.55f * slumpW);
                }
            }
        }

        public void Forces(float t, float dt, float weight)
        {
            if (!Active) return;
            float u = Mathf.Clamp01(weight);
            if (u <= 0.05f) return;

            var pv = B.Part(Seg.Pelvis);
            var th = B.Part(Seg.Thorax);
            float M = Mathf.Max(20f, B.Mass);
            float g = Mathf.Max(1f, -Physics.gravity.y);

            if (_isLowWall)
            {
                float distN = Vector3.Dot(pv.Com - WallPoint, WallNormal);
                float vToward = -Vector3.Dot(B.Vel, WallNormal);
                if (distN < 0.68f && (vToward > 0.1f || _age < 0.7f))
                {
                    // nmfow: pivot body cleanly over the top edge of the low wall.
                    Vector3 edgeAxis = BMath.SafeNormalize(Vector3.Cross(Vector3.up, -WallNormal), Sensors.Right);
                    float env = BMath.SmoothStep(0f, 0.12f, _age) * (1f - BMath.SmoothStep(0.65f, 1.15f, _age));
                    DlPhys.BodyAngularAccel(B, edgeAxis * (7.5f * env * u));
                    Vector3 overForce = (-WallNormal * 0.65f + Vector3.up * 0.45f) * (0.42f * M * g * env * u);
                    B.Force(Seg.Thorax, overForce);
                }
                return;
            }

            // Full wall cushion + slide friction (no bouncy rubber recoil off wall).
            float thDist = Vector3.Dot(th.Com - WallPoint, WallNormal);
            if (thDist < 0.58f)
            {
                float vInto = -Vector3.Dot(th.V, WallNormal);
                if (vInto > 0.1f)
                {
                    float cush = Mathf.Min(vInto * 65f, 0.55f * M * g) * u;
                    B.Force(Seg.Thorax, WallNormal * (cush * 0.65f));
                    B.Force(Seg.Pelvis, WallNormal * (cush * 0.35f));
                }
                // Gentle vertical wall friction while leaning/slumping.
                if (B.Vel.y < -0.1f)
                    B.Force(Seg.Thorax, Vector3.up * Mathf.Min(-B.Vel.y * 18f, 0.25f * M * g) * u);
            }
        }

        public override void Stop(float t)
        {
            if (Perf.Balance != null) Perf.Balance.WallBracing = false;
            Active = false;
        }
    }

    /// <summary>
    /// highFall, ported from Delirium's <c>nmhf::HighFall</c>: airborne orientation, out-of-phase arm flailing,
    /// leg pedaling, and time-to-impact landing brace for explosions (RPG) and high falls.
    /// </summary>
    internal sealed class DlHighFall : DlBehaviour
    {
        public bool PreparingToLand { get; private set; }
        private float _age;
        private readonly float _phase;

        public DlHighFall(NmPerformance perf, float t) : base(perf, "highFall", t)
        {
            _phase = BMath.Range(perf.Rng, 0f, BMath.TwoPi);
        }

        public void Tick(float t, float dt, float weight)
        {
            if (!Active) return;
            float u = Mathf.Clamp01(weight);
            if (u <= 0.03f) { Stop(t); return; }
            _age += dt;
            if (_age > 2.6f) { Stop(t); return; }

            var pv = B.Part(Seg.Pelvis);
            var th = B.Part(Seg.Thorax);

            float tti = 99f;
            if (B.Vel.y < -0.8f && DlPhys.Cast(pv.Com, Vector3.down, 4.5f, out Vector3 groundPt, out _))
                tti = Mathf.Max(0f, (pv.Com.y - groundPt.y) / Mathf.Max(0.8f, -B.Vel.y));

            PreparingToLand = B.Vel.y < -1.2f && tti < DlCfg.HighFallCatchTime;

            Ch.SetStiffness("ub", DlCfg.HighFallBodyStiffness * u, 0.85f);
            Ch.SetStiffness("lb", 9f * u, 0.85f);
            Ch.BlendDesired((int)JointId.Lumbar, 0, PreparingToLand ? 0.35f : 0.15f, 0.55f * u);
            Ch.BlendDesired((int)JointId.Neck, 0, PreparingToLand ? 0.35f : -0.15f, 0.55f * u);

            if (PreparingToLand)
            {
                // Landing anticipation: extend arms toward floor with pronated palms.
                for (int i = 0; i < 2; i++)
                {
                    if (Ch.Busy[i]) continue;
                    int sd = i == 0 ? -1 : 1;
                    if (Nm.ElbowCap(B.Body, sd) < 0.25f) continue;
                    Vector3 sh = B.JointPos(AnatomyData.Shoulder(sd));
                    Vector3 target = sh + (Vector3.down * 0.7f + th.F * 0.35f + th.R * (sd * 0.28f)).normalized * 0.65f;
                    ArmTo(sd, target, u, DlCfg.CatchArmsStiffness, 1f);
                    Ch.BlendDesired((int)AnatomyData.Elbow(sd), 2, DlPhys.ForearmPronationForNormal(B.Body, sd, Vector3.up), 0.85f * u);
                }
            }
            else
            {
                // Airborne arm flailing (overhead / lateral circles) and leg pedaling.
                float ph = _phase + _age * 7.5f;
                for (int i = 0; i < 2; i++)
                {
                    if (Ch.Busy[i]) continue;
                    int sd = i == 0 ? -1 : 1;
                    if (Nm.ElbowCap(B.Body, sd) < 0.25f) continue;
                    float a = i == 0 ? ph : ph + Mathf.PI * 0.85f;
                    Vector3 sh = B.JointPos(AnatomyData.Shoulder(sd));
                    Vector3 center = sh + th.U * 0.26f + th.R * (sd * 0.22f) + th.F * 0.12f;
                    Vector3 target = center + th.F * (0.28f * Mathf.Cos(a)) + th.U * (0.28f * Mathf.Sin(a));
                    ArmTo(sd, target, 0.85f * u, DlCfg.HighFallBodyStiffness, 0.85f);
                    Ch.BlendDesired((int)AnatomyData.Elbow(sd), 2, 0.4f * Mathf.Sin(a) * Motor.InternalRotSign(sd), 0.6f * u);
                    Ch.SetOpposeGravity((int)AnatomyData.Shoulder(sd), 0.7f);
                }
            }

            // Airborne leg pedaling.
            for (int sd = -1; sd <= 1; sd += 2)
            {
                int k = NmCharacter.Si(sd);
                float a = _phase + _age * 6.5f + (k == 0 ? 0f : Mathf.PI);
                Vector3 hip = B.JointPos(AnatomyData.Hip(sd));
                Vector3 c = hip - pv.U * 0.48f + pv.F * 0.12f;
                Vector3 tgt = c + pv.U * (0.20f * Mathf.Sin(a)) + pv.F * (0.20f * Mathf.Cos(a));
                if (!Nm.LegIK(B.Body, sd, hip, tgt, pv.R, pv.U, pv.F, out float f, out float ab, out float kn)) continue;
                Ch.BlendDesired((int)AnatomyData.Hip(sd), 0, f * BMath.Deg, 0.75f * u);
                Ch.BlendDesired((int)AnatomyData.Hip(sd), 1, Mathf.Clamp(ab, -15f, 25f) * BMath.Deg, 0.65f * u);
                Ch.BlendDesired((int)AnatomyData.Knee(sd), 0, Mathf.Max(12f, kn) * BMath.Deg, 0.75f * u);
            }
        }

        public void Forces(float t, float dt, float weight)
        {
            if (!Active) return;
            float u = Mathf.Clamp01(weight);
            if (u <= 0.05f || !Sensors.Airborne) return;

            // Gentle airborne righting / orientation toward flight direction (bounded, no spin explosion).
            var th = B.Part(Seg.Thorax);
            Vector3 wantUp = BMath.SafeNormalize(Vector3.up * 0.65f - Horiz(B.Vel) * 0.25f, Vector3.up);
            Vector3 err = Vector3.Cross(th.U, wantUp);
            Vector3 tq = Vector3.ClampMagnitude(err * 28f - th.W * 5f, 35f) * u;
            B.Torque(Seg.Thorax, tq);
        }
    }

    /// <summary>
    /// onFire, ported from Delirium's <c>onfire</c>: frantic staggering while swatting burning chest/shoulders
    /// and rolling side-to-side on the ground ("stop, drop, and roll") for Molotov / flame deaths.
    /// </summary>
    internal sealed class DlOnFire : DlBehaviour
    {
        private readonly float _seed;
        private float _age;
        private float _rollDir = 1f;

        public DlOnFire(NmPerformance perf, float t) : base(perf, "onFire", t)
        {
            _seed = BMath.Range(perf.Rng, 0f, 50f);
            _rollDir = BMath.Chance(perf.Rng, 0.5f) ? 1f : -1f;
        }

        public void Tick(float t, float dt, float weight)
        {
            if (!Active) return;
            float u = Mathf.Clamp01(weight);
            if (u <= 0.04f) { Stop(t); return; }
            _age += dt;

            var th = B.Part(Seg.Thorax);
            Ch.SetStiffness("ub", DlCfg.OnFireBodyStiffness * u, 0.85f);

            if (Perf.Phase != NmPhase.Ground)
            {
                // Panic stagger twist + ducking head away from chest heat.
                float sway = Mathf.Sin(_age * 6.5f + _seed);
                Ch.BlendDesired((int)JointId.Lumbar, 0, 0.24f + 0.14f * Mathf.Sin(_age * 5f), 0.7f * u);
                Ch.BlendDesired((int)JointId.Lumbar, 2, 0.35f * sway, 0.7f * u);
                Ch.BlendDesired((int)JointId.Neck, 0, -0.25f, 0.55f * u);

                // Alternating asynchronous swatting at chest/shoulders and panicked flailing.
                for (int i = 0; i < 2; i++)
                {
                    if (Ch.Busy[i]) continue;
                    int sd = i == 0 ? -1 : 1;
                    if (Nm.ElbowCap(B.Body, sd) < 0.25f) continue;
                    float ph = _age * 7.5f + i * 2.4f + _seed;
                    float swat = 0.5f + 0.5f * Mathf.Sin(ph);
                    Vector3 chestTarget = th.Com + th.U * (0.04f + 0.14f * Mathf.Cos(ph * 1.3f)) + th.F * 0.18f + th.R * (sd * 0.06f);
                    Vector3 flailTarget = th.Com + th.U * 0.25f + th.F * 0.22f + th.R * (sd * 0.36f);
                    Vector3 target = Vector3.Lerp(flailTarget, chestTarget, swat);
                    target = JointKinematics.ClampOutsideThorax(target, th.Pos, th.R, th.U, th.F,
                        B.Body.ThoraxChestZ, B.Body.ThoraxYBot, B.Body.ThoraxYTop, B.Body.ThoraxRx, BodyModel.ThoraxRyCap, B.Body.ThoraxRz, sd);
                    ArmTo(sd, target, 0.85f * u, DlCfg.OnFireBodyStiffness, 0.9f);
                    Ch.BlendDesired((int)AnatomyData.Elbow(sd), 2, DlPhys.ForearmPronationForNormal(B.Body, sd, th.F), 0.7f * u);
                    Ch.SetOpposeGravity((int)AnatomyData.Shoulder(sd), 0.85f);
                }
            }
            else
            {
                // Ground stop-drop-roll: tuck elbows/knees slightly while rolling side-to-side.
                if (((int)Mathf.Floor(_age * 0.9f)) % 2 == 1) _rollDir = -1f; else _rollDir = 1f;
                for (int i = 0; i < 2; i++)
                {
                    if (Ch.Busy[i]) continue;
                    int sd = i == 0 ? -1 : 1;
                    if (Nm.ElbowCap(B.Body, sd) < 0.25f) continue;
                    Ch.BlendDesired((int)AnatomyData.Shoulder(sd), 0, 0.65f, 0.6f * u);
                    Ch.BlendDesired((int)AnatomyData.Elbow(sd), 0, 1.35f, 0.6f * u);
                }
            }
        }

        public void Forces(float t, float dt, float weight)
        {
            if (!Active || Perf.Phase != NmPhase.Ground) return;
            float u = Mathf.Clamp01(weight);
            if (u <= 0.05f) return;

            // Stop-drop-roll torque around spine axis.
            float pulse = Mathf.Sin(_age * 3.8f);
            Vector3 ax = BMath.SafeNormalize(B.Part(Seg.Thorax).U, Sensors.Fwd);
            float tq = _rollDir * (0.4f + 0.6f * Mathf.Abs(pulse)) * 34f * u;
            B.Torque(Seg.Thorax, ax * tq);
            B.Torque(Seg.Pelvis, ax * (tq * 0.65f));
        }
    }

    /// <summary>
    /// Melee guard reflex, ported from Delirium's <c>meleetask</c>: defensive knee duck, lumbar hunch,
    /// chin tuck, and both arms raised overhead/in front of the brow with pronated forearms (bat / knife).
    /// </summary>
    internal sealed class DlMeleeGuard : DlBehaviour
    {
        private readonly float _duration;
        private readonly float _seed;
        private float _age;

        public DlMeleeGuard(NmPerformance perf, float t, float duration = 0.95f) : base(perf, "meleeGuard", t)
        {
            _duration = duration;
            _seed = BMath.Range(perf.Rng, 0f, 30f);
        }

        public void Tick(float t, float dt, float weight)
        {
            if (!Active) return;
            _age += dt;
            if (_age > _duration) { Stop(t); return; }
            float env = DlPhys.Envelope(_age, 0f, 0.08f, _duration * 0.55f, _duration * 0.35f) * Mathf.Clamp01(weight);
            if (env <= 0.03f) return;

            var th = B.Part(Seg.Thorax);
            Ch.SetStiffness("ub", DlCfg.MeleeGuardStiffness * env, 0.95f);

            // Defensive hunch & chin tuck under melee impact.
            Ch.BlendDesired((int)JointId.Lumbar, 0, 0.44f, 0.78f * env);
            Ch.BlendDesired((int)JointId.Neck, 0, 0.42f, 0.75f * env);
            if (Perf.Balance != null && Perf.Balance.Active && !Perf.Balance.Failed)
                Perf.Balance.Sink = Mathf.Min(0.28f, Perf.Balance.Sink + 0.35f * dt);

            // Both arms raised high in front of face/head with strong forearm pronation.
            for (int i = 0; i < 2; i++)
            {
                if (Ch.Busy[i] || (Perf.Shot != null && Perf.Shot.LooseArms[i])) continue;
                int sd = i == 0 ? -1 : 1;
                if (Nm.ElbowCap(B.Body, sd) < 0.25f) continue;
                float n = (DlPhys.Noise01(_age * 2.5f, _seed, i * 4.2f) - 0.5f) * 0.06f;
                Vector3 guard = th.Com + th.U * (0.34f + n) + th.F * 0.24f + th.R * (sd * 0.14f);
                ArmTo(sd, guard, 0.90f * env, DlCfg.MeleeGuardStiffness, 1f);
                Ch.BlendDesired((int)AnatomyData.Elbow(sd), 2, 0.90f * Motor.InternalRotSign(sd), 0.90f * env);
                Ch.SetOpposeGravity((int)AnatomyData.Shoulder(sd), 1f);
                Ch.SetOpposeGravity((int)AnatomyData.Elbow(sd), 1f);
            }
        }
    }
}
