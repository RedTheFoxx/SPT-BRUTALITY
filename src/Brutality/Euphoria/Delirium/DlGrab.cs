using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Euphoria
{
    /// <summary>Point d'accroche détecté dans le décor autour du mourant (Delirium : <c>grabhelper::GrabHelper</c>).</summary>
    internal struct DlGrabTarget
    {
        public Vector3 Point;
        public Vector3 Normal;
        public bool UseLeft;
        public bool UseRight;
        public bool IsLedge;
    }

    /// <summary>Prise physique temporaire d'une main sur un obstacle (Delirium : <c>nmgr::Grip</c>).</summary>
    internal sealed class DlGrip
    {
        public Vector3 AnchorWorld;
        public Vector3 SurfaceNormal;
        public float RestLength;
        public float HeldTime;
        public float LastForce;
        public bool Broken;
    }

    /// <summary>
    /// Comportement <c>grab</c> + <c>GrabHelper</c>, portés depuis <c>nmgr::Grab</c> et <c>grabhelper.rs</c> de Delirium.
    ///
    /// Quand un mur, une caisse, une table, une rambarde ou un rebord se trouve à portée pendant le déséquilibre ou la
    /// chute, le personnage cherche un point d'appui par raycasts multiples, tend la ou les mains adéquates (selon
    /// l'angle par rapport au thorax : <c>reachAngle</c> / <c>oneSideReachAngle</c>), oriente la paume contre la surface
    /// par pronation de l'avant-bras, attire la main (impulsion <c>pull</c>), verrouille une prise physique réelle à
    /// force de rupture (<c>GrabStrength</c>) qui retient et fait pivoter le corps, puis lâche dès que la charge arrache
    /// la prise, que <c>GrabHoldMaxTime</c> expire ou que la conscience s'éteint.
    /// </summary>
    internal sealed class DlGrab : DlBehaviour
    {
        public readonly DlGrip[] Grips = new DlGrip[2];
        public bool Grabbing { get; private set; }
        public bool EverGripped { get; private set; }
        public int SnapCount { get; private set; }
        public Vector3 TargetPoint => _target.Point;
        public Vector3 TargetNormal => _target.Normal;

        private DlGrabTarget _target;
        private readonly float[] _held = new float[2];
        private readonly bool[] _pulling = new bool[2];
        private readonly Vector3[] _aim = new Vector3[2];
        private readonly float _holdMax;
        private readonly float _strength;
        private float _searchAge;

        public DlGrab(NmPerformance perf, float t, DlGrabTarget target, float strength = DlCfg.GrabStrength, float holdMax = DlCfg.GrabHoldMaxTime)
            : base(perf, "grab", t)
        {
            _target = target;
            _strength = strength;
            _holdMax = holdMax;
            _aim[0] = target.Point;
            _aim[1] = target.Point;
        }

        /// <summary>
        /// Sonde le décor autour du buste (murs, caisses, piliers, rebords) pour trouver une surface saisissable
        /// (équivalent de <c>grabhelper::GrabHelper::find_surface</c>, étendu à un balayage directionnel complet).
        /// </summary>
        public static bool ProbeEnvironment(DlBody b, NmCharacter sensors, Vector3 fallHint, out DlGrabTarget found)
        {
            found = default;
            var th = b.Part(Seg.Thorax);
            Vector3 origin = th.Com;
            Vector3 fwd = BMath.SafeNormalize(DlBody.Horiz(fallHint), sensors.Fwd);
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            if (right.sqrMagnitude < 1e-4f) right = sensors.Right;
            right.Normalize();

            // 8 directions autour du mourant (priorité au sens de chute, devant et côtés) + sonde rebord en contrebas.
            Vector3[] dirs =
            {
                fwd,
                BMath.SafeNormalize(fwd + right * 0.65f, fwd),
                BMath.SafeNormalize(fwd - right * 0.65f, fwd),
                right,
                -right,
                BMath.SafeNormalize(fwd * 0.85f + Vector3.down * 0.45f, fwd),
                BMath.SafeNormalize(right * 0.8f + Vector3.down * 0.45f, right),
                BMath.SafeNormalize(-right * 0.8f + Vector3.down * 0.45f, -right),
            };

            float bestScore = float.MaxValue;
            bool any = false;
            float minY = sensors.GroundY + 0.35f; // ignore le sol plat : c'est le rôle de catchFall

            for (int i = 0; i < dirs.Length; i++)
            {
                if (!DlPhys.Cast(origin, dirs[i], DlCfg.GrabSearchDist, out Vector3 pt, out Vector3 nrm)) continue;
                // Surface utilisable : mur (normale quasi horizontale) ou rebord/caisse/table surélevé (> 35 cm du sol).
                bool isWall = Mathf.Abs(Vector3.Dot(nrm, Vector3.up)) < 0.65f;
                bool isLedge = !isWall && pt.y >= minY && pt.y <= origin.y + 0.45f;
                if (!isWall && !isLedge) continue;
                float dist = Vector3.Distance(origin, pt);
                if (dist < 0.16f || dist > DlCfg.GrabSearchDist) continue;
                // Priorise les surfaces dans la direction de chute et proches.
                float align = Vector3.Dot(BMath.SafeNormalize(pt - origin, fwd), fwd);
                float score = dist - 0.25f * align - (isLedge ? 0.12f : 0f);
                if (score < bestScore)
                {
                    bestScore = score;
                    any = true;
                    found = new DlGrabTarget
                    {
                        Point = pt,
                        Normal = nrm,
                        IsLedge = isLedge,
                        UseLeft = true,
                        UseRight = true,
                    };
                }
            }
            return any;
        }

        public void Tick(float t, float dt, float weight)
        {
            if (!Active) return;
            _pulling[0] = _pulling[1] = false;
            float u = Mathf.Clamp01(weight);
            if (u <= 0.08f)
            {
                ReleaseAll();
                Stop(t);
                return;
            }
            _searchAge += dt;

            // Décision gauche / droite selon la distance, l'approche et le gisement par rapport au thorax (nmgr.rs:307-353).
            Decide(out bool left, out bool right);
            if (!Grabbing || (!left && !right))
            {
                ReleaseAll();
                if (EverGripped || _searchAge > 0.75f) Stop(t);
                return;
            }

            // Sépare légèrement les deux mains si elles visent le même point (Delirium nmgr.rs:233-240 : apart()).
            Vector3 tangent = Vector3.Cross(_target.Normal, Vector3.up);
            if (tangent.sqrMagnitude < 1e-3f) tangent = Sensors.Right;
            tangent.Normalize();
            Vector3 ptL = left && right ? _target.Point - tangent * 0.14f : _target.Point;
            Vector3 ptR = left && right ? _target.Point + tangent * 0.14f : _target.Point;
            _aim[0] = ptL;
            _aim[1] = ptR;

            bool anyActive = false;
            for (int i = 0; i < 2; i++)
            {
                bool on = i == 0 ? left : right;
                int sd = i == 0 ? -1 : 1;
                if (!on || (Grips[i] != null && Grips[i].Broken))
                {
                    ReleaseArm(i);
                    continue;
                }
                if (Nm.ElbowCap(B.Body, sd) < 0.25f)
                {
                    ReleaseArm(i);
                    continue;
                }
                anyActive = true;
                Ch.Busy[i] = true;
                StepArm(i, sd, _aim[i], _target.Normal, u, dt);
            }
            if (!anyActive && EverGripped)
            {
                Stop(t);
                return;
            }

            // SpineTwist + HeadLook vers le point saisi (Delirium nmgr.rs:255-268).
            Vector3 mid = left && right ? 0.5f * (ptL + ptR) : (left ? ptL : ptR);
            Vector3 rel = mid - B.Part(Seg.Thorax).Com;
            float yaw = Mathf.Atan2(Vector3.Dot(rel, Sensors.Right), Vector3.Dot(rel, Sensors.Fwd));
            float pitch = Mathf.Atan2(Vector3.Dot(rel, Vector3.up), Horiz(rel).magnitude);
            Ch.SetStiffness(JointId.Lumbar, DlCfg.GrabBodyStiffness * u, 1f);
            Ch.BlendDesired((int)JointId.Lumbar, 2, Mathf.Clamp(yaw * 0.6f, -0.6f, 0.6f), 0.65f * u);
            Ch.SetStiffness(JointId.Neck, DlCfg.GrabBodyStiffness * u, 0.8f);
            Ch.BlendDesired((int)JointId.Neck, 0, Mathf.Clamp(-pitch, -0.6f, 0.6f), 0.65f * u);
            Ch.BlendDesired((int)JointId.Neck, 2, Mathf.Clamp(yaw, -0.9f, 0.9f), 0.65f * u);
        }

        private void Decide(out bool left, out bool right)
        {
            var th = B.Part(Seg.Thorax);
            Vector3 d = _target.Point - th.Com;
            float len = d.magnitude;
            float closing = Mathf.Clamp(Vector3.Dot(B.Vel, BMath.SafeNormalize(d, Sensors.Fwd)) * 0.25f, 0f, len);
            float dist = DlCfg.GrabSearchDist;
            Grabbing = (len - closing) < (Grabbing ? dist + 0.25f : dist);

            // Gisement horizontal dans le repère du thorax (nmgr.rs:331-339).
            float x = Vector3.Dot(d, th.R);
            float z = Vector3.Dot(d, th.F);
            float ang = Mathf.Atan2(x, z);
            if (Mathf.Abs(ang) < DlCfg.GrabReachAngle)
            {
                right = _target.UseRight && ang >= -(DlCfg.GrabReachAngle - DlCfg.GrabOneSideReachAngle);
                left = _target.UseLeft && ang <= (DlCfg.GrabReachAngle - DlCfg.GrabOneSideReachAngle);
            }
            else
            {
                Grabbing = false;
                left = false;
                right = false;
            }

            if (Grips[0] != null && !Grips[0].Broken) { Grabbing = true; left = true; }
            if (Grips[1] != null && !Grips[1].Broken) { Grabbing = true; right = true; }
            if (Grips[0] != null && Grips[0].Broken) left = false;
            if (Grips[1] != null && Grips[1].Broken) right = false;

            if (_holdMax > 0f)
            {
                if (_held[0] > _holdMax) left = false;
                if (_held[1] > _holdMax) right = false;
            }
        }

        private void StepArm(int i, int sd, Vector3 worldPt, Vector3 normal, float u, float dt)
        {
            var grip = Grips[i];
            int sh = (int)AnatomyData.Shoulder(sd);
            int el = (int)AnatomyData.Elbow(sd);
            Vector3 shPos = B.JointPos((JointId)sh);
            Vector3 handPos = B.HandPos(sd);
            float distToTarget = Vector3.Distance(handPos, worldPt);

            // Pronation de l'avant-bras : la paume se tourne face à la surface à saisir (nmgr.rs:402, 473-480).
            float pron = DlPhys.ForearmPronationForNormal(B.Body, sd, normal);

            if (grip == null || grip.Broken)
            {
                // Phase d'approche : IK + attraction pull() vers le point cible.
                Vector3 delta = worldPt - shPos;
                float len = delta.magnitude;
                Vector3 aim = len > DlCfg.GrabMaxReach ? shPos + delta * (DlCfg.GrabMaxReach / len) : worldPt;
                ArmTo(sd, aim, u, DlCfg.GrabArmStiffness, 1f);
                Ch.BlendDesired(el, 2, pron, 0.9f * u);
                Ch.SetOpposeGravity(sh, 1f);
                Ch.SetOpposeGravity(el, 1f);
                if (distToTarget < 3f * DlCfg.GrabDist) _pulling[i] = true;

                // Verrouillage de la prise dès que la main touche la zone (Delirium nmgr.rs:461-466).
                if (distToTarget < DlCfg.GrabDist && (grip == null))
                {
                    Grips[i] = new DlGrip
                    {
                        AnchorWorld = worldPt,
                        SurfaceNormal = normal,
                        RestLength = Mathf.Min(distToTarget, 0.08f),
                    };
                    EverGripped = true;
                    Perf.Log(Perf.Time, $"grab : prise main {(sd < 0 ? "G" : "D")} ({(_target.IsLedge ? "rebord" : "surface")})");
                }
                return;
            }

            // Phase de maintien : prise physique active, traction réflexe du coude (pullUp), raideur de retenue.
            _held[i] += dt;
            grip.HeldTime = _held[i];
            grip.RestLength = Mathf.Clamp(grip.RestLength - dt * 0.25f, 0f, distToTarget);
            float pullRamp = Mathf.Clamp01(_held[i] / Mathf.Max(0.1f, DlCfg.GrabPullUpTime));
            float armK = Mathf.Lerp(DlCfg.GrabArmStiffness, DlCfg.GrabHoldStiffness, pullRamp) * u;
            Ch.SetStiffness(AnatomyData.Shoulder(sd), armK, 0.7f, 1.1f);
            Ch.SetStiffness(AnatomyData.Elbow(sd), armK, 0.7f, 1.1f);
            // Légère flexion réflexe du coude (pullUpStrength) pour se retenir avant de céder.
            Ch.BlendDesired(el, 0, Mathf.Lerp(Ch.Actual(el, B.Body).x, 1.35f, 0.5f * pullRamp), 0.7f * u);
            Ch.BlendDesired(el, 2, pron, 0.95f * u);
            Ch.SetOpposeGravity(sh, 1f);
            Ch.SetOpposeGravity(el, 1f);
            _pulling[i] = true;
        }

        /// <summary>
        /// Forces physiques de <c>grab</c> :
        ///  - attraction <c>pull</c> en approche (Delirium nmgr.rs:453-460) ;
        ///  - contrainte élastique amortie de prise <c>DlGrip</c> avec force de rupture <c>GrabStrength</c>
        ///    (Delirium ragdoll.rs:3604 + nmgr.rs:181-187 <c>snapped</c>).
        /// </summary>
        public void Forces(float t, float dt, float weight)
        {
            if (!Active) return;
            float u = Mathf.Clamp01(weight);
            if (u <= 0.05f) return;

            for (int i = 0; i < 2; i++)
            {
                int sd = i == 0 ? -1 : 1;
                Seg foreSeg = sd < 0 ? Seg.LForearm : Seg.RForearm;
                Vector3 handPos = B.HandPos(sd);
                Vector3 handVel = B.HandVel(sd);
                var grip = Grips[i];

                if (grip != null && !grip.Broken)
                {
                    Vector3 diff = grip.AnchorWorld - handPos;
                    float d = diff.magnitude;
                    float stretch = Mathf.Max(0f, d - grip.RestLength);
                    Vector3 dir = d > 1e-4f ? diff / d : Vector3.zero;
                    // Ressort amorti de maintien sur la prise : retient la main et fait pivoter le corps.
                    float springF = stretch * 950f;
                    float dampF = -Vector3.Dot(handVel, dir) * 42f;
                    Vector3 f = dir * Mathf.Max(0f, springF + dampF) - (handVel - dir * Vector3.Dot(handVel, dir)) * 18f;
                    float maxF = _strength * u;
                    float mag = f.magnitude;
                    grip.LastForce = mag;
                    // Rupture de la prise si la charge arrache la main ou que la distance dérape (nmgr.rs:181).
                    if (mag > maxF * 1.45f || d > 0.36f)
                    {
                        grip.Broken = true;
                        SnapCount++;
                        ReleaseArm(i);
                        Perf.Log(t, $"grab : prise main {(sd < 0 ? "G" : "D")} arrachée ({mag:0} N)");
                        continue;
                    }
                    if (mag > maxF) f *= maxF / mag;
                    B.ForceAt(foreSeg, handPos, f);
                }
                else if (_pulling[i])
                {
                    Vector3 diff = _aim[i] - handPos;
                    float d = diff.magnitude;
                    if (d < 3f * DlCfg.GrabDist && d > 1e-4f)
                    {
                        float k = Mathf.Clamp01((3f * DlCfg.GrabDist - d) / (2f * DlCfg.GrabDist)) * u;
                        Vector3 f = Vector3.ClampMagnitude(diff * (90f * k) - handVel * (12f * k), 55f);
                        B.ForceAt(foreSeg, handPos, f);
                    }
                }
            }
        }

        private void ReleaseArm(int i)
        {
            Ch.Busy[i] = false;
            if (Grips[i] != null) Grips[i].Broken = true;
        }

        private void ReleaseAll()
        {
            ReleaseArm(0);
            ReleaseArm(1);
            Grabbing = false;
        }

        public override void Stop(float t)
        {
            ReleaseAll();
            Active = false;
        }
    }

    /// <summary>
    /// upperBodyFlinch, porté depuis <c>nmfl::Flinch</c> de Delirium : réflexe protecteur du haut du corps face à une
    /// menace/tir frontal ou rafale — avant-bras levés devant le visage/torse en pronation, dos légèrement cambré ou
    /// rentré, puis relâchement progressif.
    /// </summary>
    internal sealed class DlFlinch : DlBehaviour
    {
        private readonly Vector3 _threat;
        private readonly float _duration;
        private readonly float _seed;
        private float _age;

        public DlFlinch(NmPerformance perf, float t, Vector3 threatPos, float duration = 0.65f) : base(perf, "upperBodyFlinch", t)
        {
            _threat = threatPos;
            _duration = duration;
            _seed = BMath.Range(perf.Rng, 0f, 50f);
        }

        public void Tick(float t, float dt, float weight)
        {
            if (!Active) return;
            _age += dt;
            if (_age > _duration) { Stop(t); return; }
            float env = DlPhys.Envelope(_age, 0f, 0.08f, _duration * 0.45f, _duration * 0.45f) * Mathf.Clamp01(weight);
            if (env <= 0.02f) return;

            var th = B.Part(Seg.Thorax);
            Vector3 toThreat = BMath.SafeNormalize(_threat - th.Com, th.F);
            float bearing = Mathf.Atan2(Vector3.Dot(toThreat, th.R), Vector3.Dot(toThreat, th.F));
            bool fromBehind = Mathf.Abs(bearing) > 1.7f;

            Ch.SetStiffness("ub", DlCfg.FlinchBodyStiffness * env, DlCfg.FlinchDamping);
            // Légère flexion/torsion défensive du tronc.
            Ch.BlendDesired((int)JointId.Lumbar, 0, fromBehind ? -0.15f : 0.28f, 0.65f * env);
            Ch.BlendDesired((int)JointId.Lumbar, 2, Mathf.Clamp(-bearing * 0.35f, -0.45f, 0.45f), 0.6f * env);
            Ch.BlendDesired((int)JointId.Neck, 0, 0.25f, 0.55f * env);

            // Avant-bras levés devant le visage / haut de poitrine en pronation (paumes vers l'extérieur/menace).
            for (int i = 0; i < 2; i++)
            {
                if (Ch.Busy[i]) continue;
                int sd = i == 0 ? -1 : 1;
                if (Nm.ElbowCap(B.Body, sd) < 0.25f) continue;
                float n = (DlPhys.Noise01(_age * 2f, _seed, i * 3.1f) - 0.5f) * 0.08f;
                Vector3 guard = th.Com + th.U * (0.20f + n) + th.F * 0.30f + th.R * (sd * 0.14f);
                ArmTo(sd, guard, 0.85f * env, DlCfg.FlinchBodyStiffness, 1f);
                // Pronation franche pour présenter l'avant-bras / dos du poignet face au tir.
                Ch.BlendDesired((int)AnatomyData.Elbow(sd), 2, 0.85f * Motor.InternalRotSign(sd), 0.85f * env);
                Ch.SetOpposeGravity((int)AnatomyData.Shoulder(sd), 1f);
                Ch.SetOpposeGravity((int)AnatomyData.Elbow(sd), 1f);
            }
        }
    }
}
