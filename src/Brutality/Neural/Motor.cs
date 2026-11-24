using Brutality.Anatomy;
using Brutality.Core;
using UnityEngine;

namespace Brutality.Neural
{
    /// <summary>Commande motrice résolue pour un degré de liberté.</summary>
    internal struct MotorCommand
    {
        public float Target;            // angle cible (rad)
        public float U;                 // excitation 0..1
        public float Omega;             // pulsation propre du muscle (rad/s) = « stiffness » NaturalMotion
        public float Zeta;              // rapport d'amortissement (1 = critique) = « damping » NaturalMotion
        public float MuscleStiffness;   // 1,01 − looseness (NaturalMotion) ; multiplie la raideur, pas l'amortissement
        public float RefOmega;          // pulsation du filtre de la trajectoire de référence (rad/s)
        public float Stiffness01;       // raideur ramenée à 0..1 (ancienne échelle Brutality)
    }

    /// <summary>
    /// Tampon de commandes motrices : chaque programme y dépose (cible, excitation, raideur) par degré de liberté.
    /// Deux couches : premier plan (chorégraphies, réflexes, agonie, comportements Euphoria) et arrière-plan (tonus
    /// postural résiduel). Sur un DDL commandé activement au premier plan, le tonus de fond s'efface proportionnellement
    /// à (1 - u_fg) (inhibition réciproque) afin que la pose figée à la mort ne dilue pas les gestes.
    /// Chaque commande porte une raideur ω, un amortissement ζ, une muscleStiffness et la pulsation du filtre de
    /// référence : les programmes Brutality passent une raideur 0..1 (ω = 10..34 rad/s, ζ = 0,95) ; les comportements
    /// Euphoria passent directement les unités NaturalMotion (stiffness, damping, muscleStiffness).
    /// </summary>
    internal sealed class MotorCommandBuffer
    {
        public const float LegacyOmegaMin = 10f, LegacyOmegaMax = 34f, LegacyZeta = 0.95f;
        public const float RefOmegaMin = 6f, RefOmegaMax = 20f;
        private const int N = AnatomyData.JointCount * 3;

        private sealed class Layer
        {
            public readonly float[] UT = new float[N], U = new float[N], NotU = new float[N];
            public readonly float[] UK = new float[N], UZ = new float[N], UM = new float[N], UW = new float[N];

            public void Reset()
            {
                for (int i = 0; i < N; i++) { UT[i] = 0f; U[i] = 0f; NotU[i] = 1f; UK[i] = 0f; UZ[i] = 0f; UM[i] = 0f; UW[i] = 0f; }
            }

            public void Add(int i, float target, float u, float k, float z, float ms, float wr)
            {
                UT[i] += u * target; U[i] += u; NotU[i] *= 1f - u;
                UK[i] += u * k; UZ[i] += u * z; UM[i] += u * ms; UW[i] += u * wr;
            }
        }

        private readonly Layer _fg = new Layer();
        private readonly Layer _bg = new Layer();

        public void Reset()
        {
            _fg.Reset();
            _bg.Reset();
        }

        public void Add(int joint, int dof, float targetRad, float u, float stiffness)
        {
            if (!(u > 1e-4f) || float.IsNaN(targetRad)) return;
            float s = Mathf.Clamp01(stiffness);
            _fg.Add(joint * 3 + dof, targetRad, Mathf.Clamp01(u), Mathf.Lerp(LegacyOmegaMin, LegacyOmegaMax, s), LegacyZeta, 1f, Mathf.Lerp(RefOmegaMin, RefOmegaMax, s));
        }

        public void AddBackground(int joint, int dof, float targetRad, float u, float stiffness)
        {
            if (!(u > 1e-4f) || float.IsNaN(targetRad)) return;
            float s = Mathf.Clamp01(stiffness);
            _bg.Add(joint * 3 + dof, targetRad, Mathf.Clamp01(u), Mathf.Lerp(LegacyOmegaMin, LegacyOmegaMax, s), LegacyZeta, 1f, Mathf.Lerp(RefOmegaMin, RefOmegaMax, s));
        }

        /// <summary>Effecteur NaturalMotion : stiffness ω (rad/s), damping ζ, muscleStiffness (1,01 − looseness).</summary>
        public void AddNm(int joint, int dof, float targetRad, float weight, float stiffness, float damping, float muscleStiffness)
        {
            if (!(weight > 1e-4f) || float.IsNaN(targetRad)) return;
            float k = Mathf.Clamp(stiffness, 1f, 30f);
            // Pas de lissage de consigne marqué : la souplesse NaturalMotion vient de la raideur elle-même.
            _fg.Add(joint * 3 + dof, targetRad, Mathf.Clamp01(weight), k, Mathf.Clamp(damping, 0.05f, 4f), Mathf.Clamp(muscleStiffness, 0f, 1.5f), Mathf.Clamp(2.5f * k, 10f, 40f));
        }

        /// <summary>
        /// Effecteur Delirium : les raideurs sont déjà des unités NaturalMotion (ω, ζ), avec un plancher plus bas que
        /// les comportements car Delirium distingue un membre « mou » (strength faible) d'un membre éteint.
        /// </summary>
        public void AddEffector(int joint, int dof, float targetRad, float weight, float stiffness, float damping, float muscleStiffness)
        {
            if (!(weight > 1e-4f) || float.IsNaN(targetRad)) return;
            float k = Mathf.Clamp(stiffness, 0.5f, 30f);
            _fg.Add(joint * 3 + dof, targetRad, Mathf.Clamp01(weight), k,
                Mathf.Clamp(damping, 0.05f, 4f), Mathf.Clamp(muscleStiffness, 0f, 2f), Mathf.Clamp(2.5f * k, 6f, 40f));
        }

        public bool Resolve(int joint, int dof, out MotorCommand c)
        {
            int i = joint * 3 + dof;
            float su = _fg.U[i];
            float bgu = _bg.U[i];
            c = new MotorCommand { Omega = LegacyOmegaMin, Zeta = LegacyZeta, MuscleStiffness = 1f, RefOmega = RefOmegaMin };
            if (su < 1e-5f && bgu < 1e-5f) return false;
            float uFg = 1f - _fg.NotU[i];
            float bgScale = 1f - uFg;
            float wTot = su + bgu * bgScale;
            if (wTot < 1e-5f) return false;
            float inv = 1f / wTot;
            float uBg = (1f - _bg.NotU[i]) * bgScale;
            c.Target = (_fg.UT[i] + _bg.UT[i] * bgScale) * inv;
            c.U = 1f - (1f - uFg) * (1f - uBg);
            c.Omega = (_fg.UK[i] + _bg.UK[i] * bgScale) * inv;
            c.Zeta = (_fg.UZ[i] + _bg.UZ[i] * bgScale) * inv;
            c.MuscleStiffness = (_fg.UM[i] + _bg.UM[i] * bgScale) * inv;
            c.RefOmega = (_fg.UW[i] + _bg.UW[i] * bgScale) * inv;
            c.Stiffness01 = Mathf.Clamp01((c.Omega - LegacyOmegaMin) / (LegacyOmegaMax - LegacyOmegaMin));
            return true;
        }
    }

    /// <summary>Ce que voient les programmes moteurs à chaque pas.</summary>
    internal sealed class NeuralState
    {
        public BodyModel Body;
        public DeathContext Ctx;
        public System.Random Rng;
        public float Time;          // secondes depuis la mort
        public float Dt;
        public float Agony;         // intensité instantanée de l'agonie (0..1+)
        public float Reflex;        // échelle des réflexes (config)
        public float Posturing;     // échelle du posturing cérébral (config)
        public float Rigidity;      // rigidité de posture en cours (0..1), remise à 0 à chaque pas
        public float RigidityPrev;  // rigidité du pas précédent (lue par les couches exécutées avant les crises)
        public MotorCommandBuffer Buffer;
    }

    internal static class Motor
    {
        public const float NoChange = float.NaN;

        /// <summary>Cible absolue en degrés (NaN = ne pas commander ce DDL).</summary>
        public static void Pose(NeuralState ns, JointId j, float flexDeg, float sideDeg, float twistDeg, float u, float s)
        {
            int ji = (int)j;
            if (!float.IsNaN(flexDeg)) ns.Buffer.Add(ji, 0, flexDeg * BMath.Deg, u, s);
            if (!float.IsNaN(sideDeg)) ns.Buffer.Add(ji, 1, sideDeg * BMath.Deg, u, s);
            if (!float.IsNaN(twistDeg)) ns.Buffer.Add(ji, 2, twistDeg * BMath.Deg, u, s);
        }

        /// <summary>Cible absolue en degrés sur la couche de fond (effacée par toute commande de premier plan).</summary>
        public static void PoseBg(NeuralState ns, JointId j, float flexDeg, float sideDeg, float twistDeg, float u, float s)
        {
            int ji = (int)j;
            if (!float.IsNaN(flexDeg)) ns.Buffer.AddBackground(ji, 0, flexDeg * BMath.Deg, u, s);
            if (!float.IsNaN(sideDeg)) ns.Buffer.AddBackground(ji, 1, sideDeg * BMath.Deg, u, s);
            if (!float.IsNaN(twistDeg)) ns.Buffer.AddBackground(ji, 2, twistDeg * BMath.Deg, u, s);
        }

        /// <summary>
        /// Signe de la rotation interne (épaule, hanche) et de la pronation (avant-bras) dans la convention des
        /// angles articulaires : torsion négative à droite, positive à gauche (vérifié par le banc de test).
        /// La torsion des membres est relative à la pose de la mort.
        /// </summary>
        public static float InternalRotSign(int side) => side < 0 ? 1f : -1f;

        /// <summary>Petite oscillation (tremblement d'effort, clonus) en degrés, déterministe.</summary>
        public static float Shake(float t, float freq, float phase) => Mathf.Sin(BMath.TwoPi * freq * t + phase);

        /// <summary>Cible relative à un angle de référence (rad) + décalage en degrés.</summary>
        public static void Offset(NeuralState ns, JointId j, int dof, float baseRad, float offsetDeg, float u, float s)
        {
            ns.Buffer.Add((int)j, dof, baseRad + offsetDeg * BMath.Deg, u, s);
        }

        public static void Hold(NeuralState ns, JointId j, Vector3 anglesRad, float u, float s)
        {
            int ji = (int)j;
            ns.Buffer.Add(ji, 0, anglesRad.x, u, s);
            ns.Buffer.Add(ji, 1, anglesRad.y, u, s);
            ns.Buffer.Add(ji, 2, anglesRad.z, u, s);
        }

        public static void HoldBackground(NeuralState ns, JointId j, Vector3 anglesRad, float u, float s)
        {
            int ji = (int)j;
            ns.Buffer.AddBackground(ji, 0, anglesRad.x, u, s);
            ns.Buffer.AddBackground(ji, 1, anglesRad.y, u, s);
            ns.Buffer.AddBackground(ji, 2, anglesRad.z, u, s);
        }

        public static Vector3 Phi(NeuralState ns, JointId j) => ns.Body.Joints[(int)j].Phi;
        public static Vector3 Phi0(NeuralState ns, JointId j) => ns.Body.Joints[(int)j].Phi0;

        /// <summary>
        /// Cinématique inverse analytique à deux segments (bras + avant-bras/main) : angles d'épaule (flexion,
        /// abduction, mêmes définitions que BodyModel.Evaluate) et flexion du coude pour amener la main sur une cible.
        /// </summary>
        public static bool ArmIK(NeuralState ns, int side, Vector3 target, out float flexDeg, out float abdDeg, out float elbowDeg)
        {
            var body = ns.Body;
            var thorax = body[Seg.Thorax];
            var upper = body[side < 0 ? Seg.LUpperArm : Seg.RUpperArm];
            var fore = body[side < 0 ? Seg.LForearm : Seg.RForearm];
            return JointKinematics.ArmIK(upper.Pos, upper.Length, fore.Length + 0.06f, target, side,
                thorax.R, thorax.U, thorax.F, out flexDeg, out abdDeg, out elbowDeg, out _);
        }
    }
}
