using Brutality.Anatomy;
using Brutality.Core;
using UnityEngine;

namespace Brutality.Neural
{
    /// <summary>Resolved motor command for a degree of freedom.</summary>
    internal struct MotorCommand
    {
        public float Target;            // target angle (rad)
        public float U;                 // excitation 0..1
        public float Omega;             // muscle natural frequency (rad/s) = NaturalMotion "stiffness"
        public float Zeta;              // damping ratio (1 = critical) = NaturalMotion "damping"
        public float MuscleStiffness;   // 1.01 - looseness (NaturalMotion); scales stiffness, not damping
        public float RefOmega;          // reference trajectory filter frequency (rad/s)
        public float Stiffness01;       // stiffness normalized to 0..1 (legacy Brutality scale)
    }

    /// <summary>
    /// Motor command buffer: each program submits (target, excitation, stiffness) per degree of freedom.
    /// Two layers: foreground (choreographies, reflexes, agony, Euphoria behaviors) and background (residual
    /// postural tone). On a DOF actively driven in the foreground, background tone fades proportionally to
    /// (1 - u_fg) (reciprocal inhibition) so the frozen death pose does not dilute gestures.
    /// Each command carries stiffness ω, damping ζ, muscleStiffness, and reference filter frequency:
    /// Brutality programs pass a 0..1 stiffness (ω = 10..34 rad/s, ζ = 0.95); Euphoria behaviors
    /// pass NaturalMotion units directly (stiffness, damping, muscleStiffness).
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

        /// <summary>NaturalMotion effector: stiffness ω (rad/s), damping ζ, muscleStiffness (1.01 - looseness).</summary>
        public void AddNm(int joint, int dof, float targetRad, float weight, float stiffness, float damping, float muscleStiffness)
        {
            if (!(weight > 1e-4f) || float.IsNaN(targetRad)) return;
            float k = Mathf.Clamp(stiffness, 1f, 30f);
            // No heavy target smoothing: NaturalMotion compliance comes from the stiffness itself.
            _fg.Add(joint * 3 + dof, targetRad, Mathf.Clamp01(weight), k, Mathf.Clamp(damping, 0.05f, 4f), Mathf.Clamp(muscleStiffness, 0f, 1.5f), Mathf.Clamp(2.5f * k, 10f, 40f));
        }

        /// <summary>
        /// Delirium effector: stiffnesses are already in NaturalMotion units (ω, ζ), with a lower floor than
        /// behaviors because Delirium distinguishes a "soft" limb (low strength) from a powered-off limb.
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

    /// <summary>What motor programs see at each step.</summary>
    internal sealed class NeuralState
    {
        public BodyModel Body;
        public DeathContext Ctx;
        public System.Random Rng;
        public float Time;          // seconds since death
        public float Dt;
        public float Agony;         // instantaneous agony intensity (0..1+)
        public float Reflex;        // reflex scale (config)
        public float Posturing;     // cerebral posturing scale (config)
        public float Rigidity;      // active postural rigidity (0..1), reset to 0 each step
        public float RigidityPrev;  // previous step rigidity (read by layers executed before seizures)
        public MotorCommandBuffer Buffer;
    }

    internal static class Motor
    {
        public const float NoChange = float.NaN;

        /// <summary>Absolute target in degrees (NaN = do not drive this DOF).</summary>
        public static void Pose(NeuralState ns, JointId j, float flexDeg, float sideDeg, float twistDeg, float u, float s)
        {
            int ji = (int)j;
            if (!float.IsNaN(flexDeg)) ns.Buffer.Add(ji, 0, flexDeg * BMath.Deg, u, s);
            if (!float.IsNaN(sideDeg)) ns.Buffer.Add(ji, 1, sideDeg * BMath.Deg, u, s);
            if (!float.IsNaN(twistDeg)) ns.Buffer.Add(ji, 2, twistDeg * BMath.Deg, u, s);
        }

        /// <summary>Absolute target in degrees on the background layer (masked by any foreground command).</summary>
        public static void PoseBg(NeuralState ns, JointId j, float flexDeg, float sideDeg, float twistDeg, float u, float s)
        {
            int ji = (int)j;
            if (!float.IsNaN(flexDeg)) ns.Buffer.AddBackground(ji, 0, flexDeg * BMath.Deg, u, s);
            if (!float.IsNaN(sideDeg)) ns.Buffer.AddBackground(ji, 1, sideDeg * BMath.Deg, u, s);
            if (!float.IsNaN(twistDeg)) ns.Buffer.AddBackground(ji, 2, twistDeg * BMath.Deg, u, s);
        }

        /// <summary>
        /// Sign of internal rotation (shoulder, hip) and pronation (forearm) in the joint angle
        /// convention: negative twist on the right, positive on the left (verified by the test rig).
        /// Limb twist is relative to the death pose.
        /// </summary>
        public static float InternalRotSign(int side) => side < 0 ? 1f : -1f;

        /// <summary>Small deterministic oscillation (effort tremor, clonus) in degrees.</summary>
        public static float Shake(float t, float freq, float phase) => Mathf.Sin(BMath.TwoPi * freq * t + phase);

        /// <summary>Target relative to a reference angle (rad) + offset in degrees.</summary>
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
        /// Analytical two-segment inverse kinematics (upper arm + forearm/hand): shoulder angles (flexion,
        /// abduction, matching BodyModel.Evaluate definitions) and elbow flexion to bring the hand to a target.
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
