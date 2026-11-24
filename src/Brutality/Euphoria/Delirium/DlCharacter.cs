using System;
using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Euphoria
{
    /// <summary>
    /// Constantes du portage Delirium adaptées au squelette SPT.
    ///
    /// Delirium (PVCPC/delirium-physics, GPL-3.0) lit ses nombres dans le binaire de GTA IV (macro `kv!`), qui
    /// n'est pas publiable : on ne peut donc pas reprendre ses valeurs, seulement ses lois. Les raideurs ci-dessous
    /// sont en revanche les valeurs publiées par NaturalMotion dans le behaviours.xml de GTA V (celles que Brutality
    /// utilisait déjà) : elles décrivent les mêmes comportements et sont exprimées dans la même unité que Delirium
    /// (stiffness = pulsation propre ω, damping = rapport d'amortissement ζ). Les endroits où Delirium choisit une
    /// valeur propre sont signalés par un commentaire « Delirium : ».
    /// </summary>
    internal static class DlCfg
    {
        // Raideur/amortissement de repos d'une articulation (Delirium : JointDef.strength/damping du squelette).
        public const float RestStiffness = 12f;
        public const float RestDamping = 1f;
        public const float MinDamping = 0.05f;

        // bodyBalance (behaviours.xml : bodyStiffness 11, damping 1, elbow 2, shoulder 1).
        public const float BalanceBodyStiffness = 11f;
        public const float BalanceDamping = 1f;
        public const float BalanceElbow = 2f;
        public const float BalanceShoulder = 1f;
        public const float BalanceHeadLookTimeout = 10f;
        public const float BalanceBodyTurnTimeout = 1f;
        public const float BalanceLegStiffness = 12f;

        // catchFall (behaviours.xml : legsStiffness 6, torsoStiffness 9, armsStiffness 15).
        public const float CatchLegsStiffness = 6f;
        public const float CatchTorsoStiffness = 9f;
        public const float CatchArmsStiffness = 15f;
        public const float CatchForwardMaxArmOffset = 0.35f;
        public const float CatchBackwardsMinArmOffset = -0.25f;

        // shot (behaviours.xml : bodyStiffness 11, armStiffness 10, neck 14).
        public const float ShotBodyStiffness = 11f;
        public const float ShotArmStiffness = 10f;
        public const float ShotNeckStiffness = 14f;
        public const float ShotSpineDamping = 1f;
        public const float ShotGrabHoldTime = 2f;
        public const float ShotTimeBeforeReach = 0.2f;
        public const float ShotLooseness = 1f;
        public const float ShotArmLooseness = 0.1f;
        public const float ShotSpinePainTime = 1f;
        public const float ShotTimeBeforeCollapseLeg = 0.3f;

        // Chute (armsWindmill, rollDownStairs, bodyWrithe, bodyFoetal, bodyRollUp).
        public const float WindmillShoulderStiffness = 12f;
        public const float WindmillElbowStiffness = 12f;
        public const float RollStiffness = 11f;
        public const float RollDamping = 1.4f;
        public const float WritheArmStiffness = 13f;
        public const float WritheBackStiffness = 13f;
        public const float WritheLegStiffness = 13f;
        public const float WritheDamping = 0.5f;
        public const float FoetalStiffness = 9f;
        public const float FoetalDamping = 1.4f;
        public const float RollUpStiffness = 8f;

        // grab (behaviours.xml : grabStrength, armStiffness 12, bodyStiffness 11, holdStiffness 14).
        public const float GrabArmStiffness = 12f;
        public const float GrabBodyStiffness = 11f;
        public const float GrabHoldStiffness = 14f;
        public const float GrabStrength = 160f;
        public const float GrabDist = 0.24f;
        public const float GrabMaxReach = 0.85f;
        public const float GrabSearchDist = 1.15f;
        public const float GrabReachAngle = 1.65f;
        public const float GrabOneSideReachAngle = 0.85f;
        public const float GrabHoldMaxTime = 1.6f;
        public const float GrabPullUpTime = 0.7f;

        // upperBodyFlinch (behaviours.xml : bodyStiffness 11, bodyDamping 1).
        public const float FlinchBodyStiffness = 11f;
        public const float FlinchDamping = 1f;

        // Physique commune.
        public const float CaptureHeightLead = 0.1f;   // horizon du point de capture (Pratt 2006)
        public const float StepHeight = 0.1f;
        public const float StepClamp = 1f;
        public const float GiveUpHeight = 0.5f;
        public const float BalanceAbortThreshold = 0.6f;
        public const float ArmReachLength = 0.9f;
        public const float ArmProbeLength = 1.25f;
        public const float OpposeGravityScale = 0.35f;

        public static float Half(int joint, int dof)
        {
            var d = AnatomyData.SpecOf(joint).Dofs[dof];
            return 0.5f * (d.MaxDeg - d.MinDeg) * BMath.Deg;
        }

        public static void Range(int joint, int dof, out float min, out float max)
        {
            var d = AnatomyData.SpecOf(joint).Dofs[dof];
            min = d.MinDeg * BMath.Deg;
            max = d.MaxDeg * BMath.Deg;
        }

        /// <summary>Rayon du cône de consigne : Delirium (rs::targets) resserre la cible quand le muscle est raide.</summary>
        public static float ConeRadius(float strength) => 1f + 25f / (strength + 256f);
    }

    /// <summary>
    /// Effecteurs NaturalMotion, portés depuis <c>nmrs::Effector</c> / <c>nmrs::Character</c> de Delirium.
    ///
    /// Un effecteur porte un angle désiré absolu (rad, convention anatomique de Brutality), une raideur (ω) et un
    /// amortissement (ζ) au sens de <c>set_stiffness(s, d)</c> de Delirium — strength = s², damping = 2·s·d, ce qui
    /// redonne exactement ω = s et ζ = d après la conversion en Muscle (Delirium : stiffness = √strength,
    /// damping ratio = damping / (2·stiffness)) —, une raideur musculaire passive et un facteur <c>oppose_gravity</c>.
    ///
    /// Delirium ne fait jamais bouger un segment directement : les comportements écrivent ces effecteurs et
    /// <see cref="Flush"/> les transmet aux drives PhysX de Brutality.
    /// </summary>
    internal sealed class DlCharacter
    {
        public const int NJ = AnatomyData.JointCount;

        private readonly Vector3[] _desired = new Vector3[NJ];
        private readonly Vector3[] _zero = new Vector3[NJ];
        private readonly Vector3[] _deathPose = new Vector3[NJ];
        private readonly float[] _strength = new float[NJ];
        private readonly float[] _damping = new float[NJ];
        private readonly float[] _muscleStiffness = new float[NJ];
        private readonly float[] _opposeGravity = new float[NJ];
        public readonly float[] BaseStrength = new float[NJ];
        public readonly float[] BaseDamping = new float[NJ];
        /// <summary>Bras occupé par une prise ou un agrippement (Delirium : Character::busy[2]).</summary>
        public readonly bool[] Busy = new bool[2];

        public DlCharacter(BodyModel body)
        {
            for (int j = 0; j < NJ; j++)
            {
                BaseStrength[j] = DlCfg.RestStiffness;
                BaseDamping[j] = DlCfg.RestDamping;
                _deathPose[j] = body.Joints[j].Phi0;
                // Pose de référence anatomique neutre (comme la zero-pose debout de Delirium, et non la pose
                // figée tenant le fusil au moment de la mort qui verrouillait les bras lors des blend_to_zero).
                var spec = AnatomyData.SpecOf(j);
                _zero[j] = new Vector3(
                    spec.Dofs[0].NeutralDeg * BMath.Deg,
                    spec.Dofs[1].NeutralDeg * BMath.Deg,
                    spec.Dofs[2].NeutralDeg * BMath.Deg);
                Reset(j);
            }
        }

        public Vector3 ZeroPose(int j) => _zero[j];
        public Vector3 DeathPose(int j) => _deathPose[j];

        // ---------------------------------------------------------------- configuration

        public void Reset(int j)
        {
            _strength[j] = BaseStrength[j] * BaseStrength[j];
            _damping[j] = 2f * BaseStrength[j] * BaseDamping[j];
            _muscleStiffness[j] = 1f;
            _opposeGravity[j] = 0f;
            _desired[j] = _zero[j];
        }

        public void ResetAll()
        {
            for (int j = 0; j < NJ; j++) Reset(j);
        }

        /// <summary>Sauvegarde/restaure les effecteurs hors du masque (Delirium : Character::lock / unlock).</summary>
        public struct EffectorSnap
        {
            public Vector3 Desired;
            public float Strength, Damping, MuscleStiffness, OpposeGravity;
        }

        public EffectorSnap[] Lock(string mask)
        {
            if (mask == "fb") return null;
            var snap = new EffectorSnap[NJ];
            for (int j = 0; j < NJ; j++)
                snap[j] = new EffectorSnap
                {
                    Desired = _desired[j], Strength = _strength[j], Damping = _damping[j],
                    MuscleStiffness = _muscleStiffness[j], OpposeGravity = _opposeGravity[j],
                };
            return snap;
        }

        public void Unlock(string mask, EffectorSnap[] keep)
        {
            if (keep == null) return;
            var open = Mask(mask);
            for (int j = 0; j < NJ; j++)
            {
                bool isOpen = false;
                for (int k = 0; k < open.Length; k++) if ((int)open[k] == j) { isOpen = true; break; }
                if (isOpen) continue;
                _desired[j] = keep[j].Desired;
                _strength[j] = keep[j].Strength;
                _damping[j] = keep[j].Damping;
                _muscleStiffness[j] = keep[j].MuscleStiffness;
                _opposeGravity[j] = keep[j].OpposeGravity;
            }
        }

        /// <summary>set_stiffness(s, d) : Delirium stocke strength = s² et damping = 2·s·d.</summary>
        public void SetStiffness(float s, float d, float? ms = null)
        {
            for (int j = 0; j < NJ; j++) SetStiffness((JointId)j, s, d, ms);
        }

        public void SetStiffness(JointId j, float s, float d, float? ms = null)
        {
            int i = (int)j;
            s = Mathf.Max(0f, s);
            _strength[i] = s * s;
            _damping[i] = 2f * s * Mathf.Max(0f, d);
            if (ms.HasValue) _muscleStiffness[i] = ms.Value;
        }

        /// <summary>set_relaxation : strength = base·x², damping = base·x (un membre qui s'éteint progressivement).</summary>
        public void SetRelaxation(int j, float x)
        {
            x = Mathf.Clamp01(x);
            _strength[j] = BaseStrength[j] * BaseStrength[j] * x * x;
            _damping[j] = 2f * BaseStrength[j] * BaseDamping[j] * x;
        }

        public void SetDampingRelaxation(int j, float x)
        {
            _damping[j] = 2f * BaseStrength[j] * BaseDamping[j] * Mathf.Clamp01(x);
        }

        public void SetMuscleStiffness(int j, float ms) => _muscleStiffness[j] = ms;
        public void SetOpposeGravity(int j, float og) => _opposeGravity[j] = og;

        public float Strength(int j) => _strength[j];
        public float Damping(int j) => _damping[j];
        public float MuscleStiffness(int j) => _muscleStiffness[j];
        public float OpposeGravity(int j) => _opposeGravity[j];

        /// <summary>Angle absolu courant (rad) d'une articulation, tel que le voit le comportement.</summary>
        public Vector3 Actual(int j, BodyModel b) => b.Joints[j].Phi;

        // ---------------------------------------------------------------- cibles

        /// <summary>
        /// Angle désiré, en unités relatives au milieu de course (0 = centre, ±1 = butée).
        /// Fidèle à Delirium (nmrs::Character::set_relative) : aucun pré-clamp brutal, le cône de
        /// <see cref="Targets"/> borne ensuite la cible selon la raideur.
        /// </summary>
        public void SetRelative(int j, int dof, float x)
        {
            DlCfg.Range(j, dof, out float lo, out float hi);
            float v = 0.5f * (lo + hi) + 0.5f * (hi - lo) * x;
            var d = _desired[j];
            if (dof == 0) d.x = v; else if (dof == 1) d.y = v; else d.z = v;
            _desired[j] = d;
        }

        public void SetRelative(int j, Vector3 x)
        {
            SetRelative(j, 0, x.x);
            SetRelative(j, 1, x.y);
            SetRelative(j, 2, x.z);
        }

        public void SetDesired(int j, int dof, float rad)
        {
            var d = _desired[j];
            if (dof == 0) d.x = rad; else if (dof == 1) d.y = rad; else d.z = rad;
            _desired[j] = d;
        }

        /// <summary>Mélange une cible sur l'angle courant (les comportements Delirium écrivent en pondération).</summary>
        public void BlendDesired(int j, int dof, float rad, float w)
        {
            var d = _desired[j];
            float cur = dof == 0 ? d.x : dof == 1 ? d.y : d.z;
            SetDesired(j, dof, Mathf.Lerp(cur, rad, Mathf.Clamp01(w)));
        }
        public Vector3 Desired(int j) => _desired[j];

        /// <summary>blend_to_zero : ramène l'angle désiré vers la pose de la mort (Delirium : Effector::blend_to_zero).</summary>
        public void BlendToZero(int j, float k)
        {
            k = Mathf.Clamp01(k);
            _desired[j] = Vector3.LerpUnclamped(_desired[j], _zero[j], k);
        }

        public void BlendToZero(string mask, float k)
        {
            foreach (var j in Mask(mask)) BlendToZero((int)j, k);
        }

        /// <summary>
        /// targets() de Delirium : la consigne d'inclinaison est ramenée dans un cône dont le rayon vaut
        /// k = 1 + 25/(strength+256) fois l'amplitude, et la torsion dans le même intervalle. Un muscle faible
        /// laisse donc la consigne déborder (membre mou), un muscle raide la contient dans l'anatomie.
        /// </summary>
        public Vector3 Targets(int j)
        {
            float k = DlCfg.ConeRadius(_strength[j]);
            DlCfg.Range(j, 0, out float lo0, out float hi0);
            DlCfg.Range(j, 1, out float lo1, out float hi1);
            DlCfg.Range(j, 2, out float lo2, out float hi2);
            float mid0 = 0.5f * (lo0 + hi0), h0 = 0.5f * (hi0 - lo0);
            float mid1 = 0.5f * (lo1 + hi1), h1 = 0.5f * (hi1 - lo1);
            float mid2 = 0.5f * (lo2 + hi2), h2 = 0.5f * (hi2 - lo2);
            float d0 = _desired[j].x, d1 = _desired[j].y, d2 = _desired[j].z;
            if (h0 > 1e-5f && h1 > 1e-5f)
            {
                float u = (d0 - mid0) / h0, v = (d1 - mid1) / h1;
                float r2 = u * u + v * v;
                if (r2 > k * k && r2 > 1e-8f)
                {
                    float s = k / Mathf.Sqrt(r2);
                    d0 = mid0 + s * u * h0;
                    d1 = mid1 + s * v * h1;
                }
            }
            if (h2 > 1e-5f) d2 = Mathf.Clamp(d2, mid2 - k * h2, mid2 + k * h2);
            return new Vector3(d0, d1, d2);
        }

        // ---------------------------------------------------------------- écriture des drives

        /// <summary>
        /// Transmet les effecteurs au tampon de commandes. <paramref name="weight"/> est la vivacité résiduelle du
        /// mourant (setCharacterHealth) : à 0, plus aucune commande n'est écrite et les membres retombent.
        /// </summary>
        public void Flush(NeuralState ns, BodyModel body, float weight)
        {
            if (weight <= 0.01f) return;
            for (int j = 0; j < NJ; j++)
            {
                float s = Mathf.Sqrt(Mathf.Max(0f, _strength[j]));
                if (s <= 0.05f) continue;
                float damp = Mathf.Max(_damping[j], DlCfg.MinDamping);
                float z = damp / (2f * Mathf.Max(1e-4f, s));
                z = Mathf.Clamp(z, DlCfg.MinDamping, 3f);
                var target = Targets(j);
                var kind = body.Joints[j].Kind;
                ns.Buffer.AddEffector(j, 0, target.x, weight, s, z, _muscleStiffness[j]);
                if (kind == JointKind.Elbow)
                {
                    // La prono-supination de l'avant-bras (DOF 2 du coude) est pilotée par les comportements
                    // Delirium (orientation de la paume sur une plaie, au sol, contre un mur, ou en posturing).
                    ns.Buffer.AddEffector(j, 2, target.z, weight, s, z, _muscleStiffness[j]);
                }
                else if (kind != JointKind.Knee)
                {
                    ns.Buffer.AddEffector(j, 1, target.y, weight, s, z, _muscleStiffness[j]);
                    ns.Buffer.AddEffector(j, 2, target.z, weight, s, z, _muscleStiffness[j]);
                }
            }
        }

        // ---------------------------------------------------------------- masques

        private static readonly JointId[] All =
        {
            JointId.Lumbar, JointId.Neck, JointId.LShoulder, JointId.LElbow, JointId.RShoulder, JointId.RElbow,
            JointId.LHip, JointId.LKnee, JointId.RHip, JointId.RKnee,
        };
        private static readonly JointId[] UpperBody = { JointId.Lumbar, JointId.Neck, JointId.LShoulder, JointId.LElbow, JointId.RShoulder, JointId.RElbow };
        private static readonly JointId[] UpperTorso = { JointId.Lumbar, JointId.LShoulder, JointId.LElbow, JointId.RShoulder, JointId.RElbow };
        private static readonly JointId[] UpperArms = { JointId.LShoulder, JointId.LElbow, JointId.RShoulder, JointId.RElbow };
        private static readonly JointId[] LeftArm = { JointId.LShoulder, JointId.LElbow };
        private static readonly JointId[] RightArm = { JointId.RShoulder, JointId.RElbow };
        private static readonly JointId[] Clavicles = { JointId.LShoulder, JointId.RShoulder };
        private static readonly JointId[] Spine = { JointId.Lumbar };
        private static readonly JointId[] SpineNeck = { JointId.Lumbar, JointId.Neck };
        private static readonly JointId[] Neck = { JointId.Neck };
        private static readonly JointId[] LeftLeg = { JointId.LHip, JointId.LKnee };
        private static readonly JointId[] RightLeg = { JointId.RHip, JointId.RKnee };
        private static readonly JointId[] BothLegs = { JointId.LHip, JointId.LKnee, JointId.RHip, JointId.RKnee };
        private static readonly JointId[] NoWrist = Array.Empty<JointId>();   // main fusionnée avec l'avant-bras dans EFT

        /// <summary>
        /// Masques de message Euphoria (Delirium : Character::mask). « uc » (clavicules) vise les épaules :
        /// le ragdoll EFT n'a pas d'articulation de ceinture scapulaire. « uw » (poignets) est vide : la main est
        /// solidaire de l'avant-bras. « ut » couvre tronc + bras (sans cou).
        /// </summary>
        public static JointId[] Mask(string mask)
        {
            if (string.IsNullOrEmpty(mask)) return All;
            char p = mask[0];
            char s = mask.Length > 1 ? mask[1] : 'b';
            if (p == 'f' && s == 'b') return All;
            if (p == 'u')
            {
                switch (s)
                {
                    case 'b': return UpperBody;
                    case 't': return UpperTorso;
                    case 'a': return UpperArms;
                    case 'l': return LeftArm;
                    case 'r': return RightArm;
                    case 'c': return Clavicles;
                    case 's': return Spine;
                    case 'n': return Neck;
                    case 'k': return SpineNeck;
                    case 'w': return NoWrist;
                }
            }
            if (p == 'l')
            {
                if (s == 'l') return LeftLeg;
                if (s == 'r') return RightLeg;
                if (s == 'b') return BothLegs;
            }
            return All;
        }

        public void SetStiffness(string mask, float s, float d, float? ms = null)
        {
            foreach (var j in Mask(mask)) SetStiffness(j, s, d, ms);
        }

        public void SetMuscleStiffness(string mask, float ms)
        {
            foreach (var j in Mask(mask)) SetMuscleStiffness((int)j, ms);
        }

        public void SetOpposeGravity(string mask, float og)
        {
            foreach (var j in Mask(mask)) SetOpposeGravity((int)j, og);
        }
    }

    /// <summary>
    /// Capteurs et actions du corps, équivalents de <c>nmdbm::Body</c> et <c>nmshot::Ctx</c> de Delirium :
    /// centre de masse, vitesse, moment cinétique, appuis, points articulaires, poussées et couples par segment.
    /// </summary>
    internal sealed class DlBody
    {
        public readonly BodyModel Body;
        public readonly NmCharacter Ch;

        public DlBody(BodyModel body, NmCharacter ch) { Body = body; Ch = ch; }

        public Vector3 Up => Vector3.up;
        public Vector3 Com => Ch.Com;
        public Vector3 Vel => Ch.Vel;
        public Vector3 AngVel => Body[Seg.Thorax].W;
        public Vector3 Fwd => Ch.Fwd;
        public Vector3 Right => Ch.Right;
        public float Mass => Body.TotalMass;
        public float Height => Ch.H;

        public Segment Part(Seg s) => Body[s];

        /// <summary>Position du pivot articulaire (origine du segment enfant).</summary>
        public Vector3 JointPos(JointId j)
        {
            var jm = Body.Joints[(int)j];
            var child = Body.Segs[jm.C];
            var dj = jm.Driven;
            return dj != null ? child.Pos + child.Q * dj.Anchor : child.Pos;
        }

        public Vector3 Point(Seg s) => Body[s].Pos;

        /// <summary>Horizontale d'un vecteur (shz).</summary>
        public static Vector3 Horiz(Vector3 v) => new Vector3(v.x, 0f, v.z);

        /// <summary>sat(v) : place v à la hauteur h le long de l'axe monde vertical.</summary>
        public static Vector3 AtHeight(Vector3 v, float h) => new Vector3(v.x, h, v.z);

        public float Kinetic => 0.5f * Mass * Vel.sqrMagnitude + 0.5f * Vector3.Dot(AngVel, Ch.L);

        /// <summary>Position de la main (extrémité distale de l'avant-bras).</summary>
        public Vector3 HandPos(int side) => Ch.Hand[NmCharacter.Si(side)];

        /// <summary>Vitesse linéaire au niveau de la main (v + ω × r).</summary>
        public Vector3 HandVel(int side)
        {
            var fore = Body[side < 0 ? Seg.LForearm : Seg.RForearm];
            return fore.V + Vector3.Cross(fore.W, HandPos(side) - fore.Com);
        }

        /// <summary>Poussée sur un segment (Delirium : Out::force).</summary>
        public void Force(Seg s, Vector3 f, ForceMode mode = ForceMode.Force)
        {
            var rb = Body[s].Rb;
            if (rb != null && f.sqrMagnitude > 1e-10f) rb.AddForce(f, mode);
        }

        /// <summary>Poussée en un point monde d'un segment (force + moment au CdM).</summary>
        public void ForceAt(Seg s, Vector3 worldPos, Vector3 f, ForceMode mode = ForceMode.Force)
        {
            var rb = Body[s].Rb;
            if (rb == null || f.sqrMagnitude <= 1e-10f) return;
            rb.AddForce(f, mode);
            rb.AddTorque(Vector3.Cross(worldPos - Body[s].Com, f), mode);
        }

        /// <summary>Impulsion en un point monde (Delirium : Out::impulse pour pull / grip).</summary>
        public void ImpulseAt(Seg s, Vector3 worldPos, Vector3 impulse)
        {
            ForceAt(s, worldPos, impulse, ForceMode.Impulse);
        }

        /// <summary>Couple sur un segment (Delirium : Out::torque).</summary>
        public void Torque(Seg s, Vector3 t, ForceMode mode = ForceMode.Force)
        {
            var rb = Body[s].Rb;
            if (rb != null && t.sqrMagnitude > 1e-10f) rb.AddTorque(t, mode);
        }
    }

    /// <summary>Physique partagée du portage Delirium.</summary>
    internal static class DlPhys
    {
        /// <summary>
        /// oppose_gravity de Delirium (nmdbm::oppose_gravity) : l'effecteur compense le couple de gravité du
        /// sous-arbre autour du pivot, borné par max = og · strength · SCALE. Sur les charnières (genou, coude),
        /// seul l'axe de flexion reçoit le couple ; la réaction opposée est appliquée au parent.
        /// </summary>
        public static void OpposeGravity(DlCharacter ch, BodyModel body, float weight)
        {
            if (weight <= 0.01f) return;
            Vector3 g = Physics.gravity * weight;
            for (int j = 0; j < DlCharacter.NJ; j++)
            {
                float og = ch.OpposeGravity(j);
                if (og <= 0.01f) continue;
                float maxTq = og * ch.Strength(j) * DlCfg.OpposeGravityScale;
                if (maxTq <= 1e-4f) continue;
                var jm = body.Joints[j];
                var dj = jm.Driven;
                if (dj == null) continue;
                var child = body.Segs[jm.C];
                var parent = body.Segs[jm.P];
                if (child.Rb == null) continue;
                Vector3 pivot = child.Pos + child.Q * dj.Anchor;
                Vector3 mSum = Vector3.zero;
                foreach (var s in body.Segs)
                {
                    if (!jm.InSubtree[s.Index] || s.Rb == null) continue;
                    mSum += Vector3.Cross(s.Com - pivot, g) * s.Mass;
                }
                Vector3 tq;
                bool isHinge = jm.Kind == JointKind.Knee || jm.Kind == JointKind.Elbow;
                if (isHinge)
                {
                    Vector3 a0 = child.Q * dj.Axis;
                    float proj = Mathf.Clamp(-Vector3.Dot(mSum, a0), -maxTq, maxTq);
                    tq = a0 * proj;
                }
                else
                {
                    tq = Vector3.ClampMagnitude(-mSum, maxTq);
                }
                if (tq.sqrMagnitude < 1e-8f) continue;
                child.Rb.AddTorque(tq, ForceMode.Force);
                if (parent.Rb != null) parent.Rb.AddTorque(-tq, ForceMode.Force);
            }
        }

        /// <summary>
        /// Angle de prono-supination de l'avant-bras (DOF 2 du coude, rad) qui oriente la paume face à la normale
        /// d'une surface ou d'une plaie (équivalent EFT sans poignet de <c>nmshot::Shot::wrist</c>).
        /// </summary>
        public static float ForearmPronationForNormal(BodyModel body, int side, Vector3 surfaceNormal)
        {
            var fore = body[side < 0 ? Seg.LForearm : Seg.RForearm];
            Vector3 n = BMath.SafeNormalize(surfaceNormal, Vector3.up);
            // Au neutre (phi.z = 0), la paume fait face vers la ligne médiane (-side * R).
            Vector3 palmNow = -side * fore.R;
            Vector3 wantPalm = -n;
            float dTwist = BMath.SignedAngleOnPlane(palmNow, wantPalm, fore.U);
            float cur = body.Joints[(int)AnatomyData.Elbow(side)].Phi.z;
            return Mathf.Clamp(cur + dTwist, -75f * BMath.Deg, 75f * BMath.Deg);
        }

        /// <summary>Accélération angulaire de corps entier (m·α×r sur chaque segment, plus α), comme Delirium.</summary>
        public static void BodyAngularAccel(DlBody b, Vector3 alpha) => NmPhysics.BodyAngularAccel(b.Body, alpha);

        /// <summary>Raycast sur le décor, en ignorant les corps gérés par Brutality.</summary>
        public static bool Cast(Vector3 from, Vector3 dir, float len, out Vector3 point, out Vector3 normal)
        {
            point = from + dir * len;
            normal = Vector3.up;
            if (!Physics.Raycast(from, dir, out RaycastHit hit, len, NmCharacter.EnvironmentMask, QueryTriggerInteraction.Ignore)) return false;
            if (BrutalityRagdoll.OwnerOf(hit.rigidbody) != null) return false;
            point = hit.point;
            normal = hit.normal;
            return true;
        }

        /// <summary>Bruit lissé 0..1, équivalent de <c>nmfl::noise01</c>.</summary>
        public static float Noise01(float a, float b, float c)
        {
            return 0.5f + 0.5f * BMath.SmoothNoise(a * 0.7f + b * 1.3f, c * 2.1f);
        }

        /// <summary>Enveloppe d'un geste : montée, maintien, retombée.</summary>
        public static float Envelope(float t, float t0, float attack, float hold, float release)
        {
            if (t < t0) return 0f;
            float x = t - t0;
            if (attack > 1e-4f && x < attack) return x / attack;
            if (x < attack + hold) return 1f;
            if (release <= 1e-4f) return 0f;
            return Mathf.Clamp01(1f - (x - attack - hold) / release);
        }
    }
}
