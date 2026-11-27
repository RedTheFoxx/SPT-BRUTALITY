using Brutality.Anatomy;
using Brutality.Core;
using UnityEngine;
using static Brutality.Neural.Motor;

namespace Brutality.Neural
{
    /// <summary>
    /// Global posture (degrees): upper limbs, lower limbs, and spine. Internal rotations and pronation are
    /// expressed as positive values = internal rotation / pronation; the anatomical sign is applied per side.
    /// </summary>
    internal struct PostureShape
    {
        public float ShFlex, ShAbd, ShRot, Elbow, Pron;
        public float HipFlex, HipAbd, HipRot, Knee;
        public float Lumbar, LumbarSide, Neck, NeckTwist;

        public static PostureShape Lerp(in PostureShape a, in PostureShape b, float t)
        {
            return new PostureShape
            {
                ShFlex = Mathf.Lerp(a.ShFlex, b.ShFlex, t), ShAbd = Mathf.Lerp(a.ShAbd, b.ShAbd, t),
                ShRot = Mathf.Lerp(a.ShRot, b.ShRot, t), Elbow = Mathf.Lerp(a.Elbow, b.Elbow, t), Pron = Mathf.Lerp(a.Pron, b.Pron, t),
                HipFlex = Mathf.Lerp(a.HipFlex, b.HipFlex, t), HipAbd = Mathf.Lerp(a.HipAbd, b.HipAbd, t),
                HipRot = Mathf.Lerp(a.HipRot, b.HipRot, t), Knee = Mathf.Lerp(a.Knee, b.Knee, t),
                Lumbar = Mathf.Lerp(a.Lumbar, b.Lumbar, t), LumbarSide = Mathf.Lerp(a.LumbarSide, b.LumbarSide, t),
                Neck = Mathf.Lerp(a.Neck, b.Neck, t), NeckTwist = Mathf.Lerp(a.NeckTwist, b.NeckTwist, t),
            };
        }

        /// <summary>
        /// Decorticate rigidity (lesion above the red nucleus): elbows, wrists, and fingers flexed, arms
        /// adducted onto the chest; lower limbs extended and internally rotated.
        /// </summary>
        public static PostureShape Decorticate(System.Random rng)
        {
            return new PostureShape
            {
                ShFlex = BMath.Range(rng, 25f, 48f), ShAbd = BMath.Range(rng, 12f, 20f), ShRot = BMath.Range(rng, 25f, 45f),
                Elbow = BMath.Range(rng, 90f, 110f), Pron = BMath.Range(rng, 5f, 25f),
                HipFlex = BMath.Range(rng, -8f, 0f), HipAbd = BMath.Range(rng, -12f, -5f), HipRot = BMath.Range(rng, 12f, 25f),
                Knee = BMath.Range(rng, 0f, 6f),
                Lumbar = BMath.Range(rng, -8f, 2f), LumbarSide = 0f,
                Neck = BMath.Range(rng, -15f, 0f), NeckTwist = BMath.Range(rng, -10f, 10f),
            };
        }
    }

    /// <summary>Emits a <see cref="PostureShape"/> to the motor buffer (foreground or background).</summary>
    internal static class PostureLib
    {
        public static void Arm(NeuralState ns, in PostureShape p, int side, float u, float s, bool bg = false, float clonus = 0f)
        {
            float ir = InternalRotSign(side);
            if (bg)
            {
                PoseBg(ns, AnatomyData.Shoulder(side), p.ShFlex + clonus * 0.5f, p.ShAbd, p.ShRot * ir, u, s);
                PoseBg(ns, AnatomyData.Elbow(side), p.Elbow + clonus, NoChange, p.Pron * ir, u, s);
            }
            else
            {
                Pose(ns, AnatomyData.Shoulder(side), p.ShFlex + clonus * 0.5f, p.ShAbd, p.ShRot * ir, u, s);
                Pose(ns, AnatomyData.Elbow(side), p.Elbow + clonus, NoChange, p.Pron * ir, u, s);
            }
        }

        public static void Leg(NeuralState ns, in PostureShape p, int side, float u, float s, bool bg = false, float clonus = 0f)
        {
            float ir = InternalRotSign(side);
            if (bg)
            {
                PoseBg(ns, AnatomyData.Hip(side), p.HipFlex, p.HipAbd, p.HipRot * ir, u, s);
                PoseBg(ns, AnatomyData.Knee(side), p.Knee + Mathf.Abs(clonus), NoChange, NoChange, u, s);
            }
            else
            {
                Pose(ns, AnatomyData.Hip(side), p.HipFlex, p.HipAbd, p.HipRot * ir, u, s);
                Pose(ns, AnatomyData.Knee(side), p.Knee + Mathf.Abs(clonus), NoChange, NoChange, u, s);
            }
        }

        public static void Axial(NeuralState ns, in PostureShape p, float u, float s, bool bg = false)
        {
            if (bg)
            {
                PoseBg(ns, JointId.Lumbar, p.Lumbar, p.LumbarSide, NoChange, u, s);
                PoseBg(ns, JointId.Neck, p.Neck, NoChange, p.NeckTwist, u * 0.9f, s);
            }
            else
            {
                Pose(ns, JointId.Lumbar, p.Lumbar, p.LumbarSide, NoChange, u, s);
                Pose(ns, JointId.Neck, p.Neck, NoChange, p.NeckTwist, u * 0.9f, s);
            }
        }

        public static void Full(NeuralState ns, in PostureShape p, float u, float s, bool bg = false)
        {
            Axial(ns, p, u, s, bg);
            for (int side = -1; side <= 1; side += 2)
            {
                Arm(ns, p, side, u, s, bg);
                Leg(ns, p, side, u, s, bg);
            }
        }
    }

    /// <summary>
    /// Decorticate posturing during the fall: elbows flexed and brought onto the chest, lower limbs
    /// extended and internally rotated. Combined with leg extension, it produces the stiff "plank" fall.
    /// </summary>
    internal sealed class Posturing : MotorProgram
    {
        private readonly PostureShape _shape;
        private readonly float _u;

        public Posturing(float start, float duration, float u, System.Random rng)
        {
            Name = "Decortication";
            _shape = PostureShape.Decorticate(rng);
            Start = start; Duration = duration; Attack = 0.08f; Release = 0.5f;
            _u = u;
            Groups = MotorGroup.All;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            PostureLib.Full(ns, _shape, _u * k * ns.Posturing, 0.9f);
        }
    }

    /// <summary>
    /// Posturing episode on the ground: progressive buildup (a few hundred ms) toward decorticate posture,
    /// sustained with waves of intensity (paroxysms) and elbow/knee clonus, then release.
    /// </summary>
    internal sealed class PosturingEpisode : MotorProgram
    {
        private readonly PostureShape _decort;
        private readonly float _waves, _wavePhase;
        private readonly float _clonusF, _clonusAmp;
        private readonly float _u;

        public PosturingEpisode(float start, float duration, float u, System.Random rng)
        {
            Name = "Decorticate episode";
            _decort = PostureShape.Decorticate(rng);
            _waves = BMath.Range(rng, 1f, 3f);
            _wavePhase = BMath.Range(rng, 0f, BMath.TwoPi);
            _clonusF = BMath.Range(rng, 4f, 6f);
            _clonusAmp = BMath.Range(rng, 4f, 9f);
            Start = start; Duration = duration;
            Attack = BMath.Range(rng, 0.25f, 0.5f); Release = BMath.Range(rng, 0.45f, 0.8f);
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.All;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            float x = Mathf.Clamp01(local / Mathf.Max(0.1f, Duration));
            // Paroxysms: intensity undulates (never below 65%).
            float wave = 0.82f + 0.18f * Mathf.Cos(BMath.TwoPi * _waves * x + _wavePhase);
            float u = Mathf.Clamp01(_u * k * wave * ns.Posturing);
            ns.Rigidity = Mathf.Max(ns.Rigidity, u);
            float clonus = _clonusAmp * Shake(local, _clonusF, 0f) * BMath.SmoothStep(0.2f, 0.4f, x);

            PostureLib.Axial(ns, _decort, u, 0.9f);
            for (int side = -1; side <= 1; side += 2)
            {
                PostureLib.Arm(ns, _decort, side, u, 0.95f, clonus: clonus * (side < 0 ? 1f : 0.8f));
                PostureLib.Leg(ns, _decort, side, u, 0.95f, clonus: clonus * 0.6f);
            }
        }
    }

    /// <summary>
    /// Continuous postural rigidity (background): between gestures, the body returns toward its decorticate posture,
    /// proportional to agony intensity. Background layer: any overt gesture masks it.
    /// </summary>
    internal sealed class PosturalRigidity : MotorProgram
    {
        private readonly PostureShape _shape;
        private readonly float _u;
        private readonly float _seed;

        public PosturalRigidity(float start, float duration, float u, System.Random rng)
        {
            Name = "Decorticate rigidity";
            _shape = PostureShape.Decorticate(rng);
            _u = u;
            _seed = BMath.Range(rng, 0f, 100f);
            Start = start; Duration = duration; Attack = 0.8f; Release = 1.2f;
            Groups = MotorGroup.None;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float a = Mathf.Clamp01(ns.Agony * 1.4f);
            float u = _u * k * a * (0.8f + 0.2f * BMath.SmoothNoise(ns.Time * 0.6f, _seed)) * ns.Posturing;
            if (u < 0.02f) return;
            ns.Rigidity = Mathf.Max(ns.Rigidity, u);
            PostureLib.Full(ns, _shape, Mathf.Clamp01(u), 0.75f, bg: true);
        }
    }

    /// <summary>
    /// Lazarus sign (spinal reflex described in brain death, Ropper 1984): arms flex and rise slowly,
    /// hands cross over the chest or toward the neck, then lower back down.
    /// </summary>
    internal sealed class LazarusSign : MotorProgram
    {
        private readonly float _flex, _elbow, _rot, _neck;
        private readonly float _u;

        public LazarusSign(float start, float duration, float u, System.Random rng)
        {
            Name = "Lazarus sign";
            Start = start; Duration = duration;
            Attack = Mathf.Min(BMath.Range(rng, 1.1f, 1.7f), 0.45f * duration);
            Release = Mathf.Min(BMath.Range(rng, 0.8f, 1.3f), 0.35f * duration);
            _flex = BMath.Range(rng, 70f, 98f);
            _elbow = BMath.Range(rng, 100f, 128f);
            _rot = BMath.Range(rng, 30f, 55f);
            _neck = BMath.Range(rng, 8f, 22f);
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.Arms | MotorGroup.Neck;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = Mathf.Clamp01(_u * k * ns.Posturing);
            float tremor = 2.5f * Shake(ns.Time, 3.3f, 0f);
            for (int side = -1; side <= 1; side += 2)
            {
                float ir = InternalRotSign(side);
                Pose(ns, AnatomyData.Shoulder(side), _flex + tremor, -12f, _rot * ir, u, 0.6f);
                Pose(ns, AnatomyData.Elbow(side), _elbow - tremor, NoChange, 15f * ir, u, 0.6f);
            }
            Pose(ns, JointId.Neck, _neck, NoChange, NoChange, u * 0.6f, 0.5f);
            Pose(ns, JointId.Lumbar, 12f, NoChange, NoChange, u * 0.4f, 0.5f);
        }
    }
}
