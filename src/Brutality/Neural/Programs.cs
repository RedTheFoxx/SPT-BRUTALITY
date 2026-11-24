using System.Collections.Generic;
using Brutality.Anatomy;
using Brutality.Core;
using UnityEngine;
using static Brutality.Neural.Motor;

namespace Brutality.Neural
{
    [System.Flags]
    internal enum MotorGroup
    {
        None = 0,
        Legs = 1,
        LArm = 2,
        RArm = 4,
        Trunk = 8,
        Neck = 16,
        Arms = LArm | RArm,
        All = Legs | Arms | Trunk | Neck,
    }

    /// <summary>Programme moteur involontaire : produit des commandes (cible, excitation, raideur) dans le temps.</summary>
    internal abstract class MotorProgram
    {
        public string Name;
        public float Start, Duration;
        public float Attack = 0.05f, Release = 0.15f;
        public float Gain = 1f;
        public MotorGroup Groups;
        public bool IsAgonyEvent;

        public bool Done(float t) => t >= Start + Duration;

        public void Run(NeuralState ns)
        {
            float t = ns.Time;
            if (t < Start || Done(t)) return;
            float env = BMath.Envelope(t, Start, Duration, Attack, Release);
            if (env <= 0f) return;
            Emit(ns, env * Gain);
        }

        protected abstract void Emit(NeuralState ns, float k);

        protected static JointId[] AllJoints =
        {
            JointId.Lumbar, JointId.Neck, JointId.LShoulder, JointId.LElbow, JointId.RShoulder, JointId.RElbow,
            JointId.LHip, JointId.LKnee, JointId.RHip, JointId.RKnee,
        };
    }

    // ===================================================================== tonus et réflexes

    /// <summary>
    /// Tonus postural résiduel : à l'instant de la mort, les muscles maintiennent encore la posture,
    /// puis le tonus s'éteint par groupes (membres inférieurs, tronc, cou, membres supérieurs) avec
    /// une latence et une constante de temps propres au mécanisme lésionnel.
    /// </summary>
    internal sealed class PosturalTone : MotorProgram
    {
        private readonly float _u0;
        private readonly float[] _delay = new float[4];
        private readonly float[] _tau = new float[4];
        private float _fallenAt = -1f;
        // Bras qui retombent : la pose de tir (épaules levées, coudes fléchis sur l'arme) s'affaisse vers des bras
        // pendants à mesure que le tonus s'éteint, au lieu de rester figée pendant la chute.
        private readonly bool _armSag;
        private readonly float _sagStart, _sagDur;
        private readonly Vector3 _hangShoulder, _hangElbow;

        public PosturalTone(float u0, float delay, Vector2[] taus, System.Random rng, bool armSag = false)
        {
            Name = "Tonus postural";
            _u0 = u0;
            _armSag = armSag;
            _sagStart = BMath.Range(rng, 0.03f, 0.18f);
            _sagDur = BMath.Range(rng, 0.3f, 0.65f);
            _hangShoulder = new Vector3(BMath.Range(rng, 2f, 18f), BMath.Range(rng, 6f, 16f), 0f) * BMath.Deg;
            _hangElbow = new Vector3(BMath.Range(rng, 10f, 30f), 0f, 0f) * BMath.Deg;
            Start = 0f;
            Attack = 0f;
            Release = 0f;
            float maxEnd = 0f;
            for (int g = 0; g < 4; g++)
            {
                _delay[g] = delay * BMath.Range(rng, 0.7f, 1.3f);
                _tau[g] = Mathf.Max(0.005f, BMath.Range(rng, taus[g]));
                maxEnd = Mathf.Max(maxEnd, _delay[g] + 5f * _tau[g]);
            }
            Duration = maxEnd + 0.05f;
        }

        private static int GroupOf(JointId j)
        {
            switch (j)
            {
                case JointId.LHip: case JointId.RHip: case JointId.LKnee: case JointId.RKnee: return 0;
                case JointId.Lumbar: return 1;
                case JointId.Neck: return 2;
                default: return 3;
            }
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float t = ns.Time;
            // Le tonus postural maintient la pose debout/course (Phi0) pendant l'effondrement initial ;
            // une fois le buste couché au sol, il s'éteint rapidement pour ne pas figer les bras en pose de tir.
            if (_fallenAt < 0f && Vector3.Dot(ns.Body[Seg.Thorax].U, Vector3.up) < 0.5f)
                _fallenAt = t;
            float fallFade = _fallenAt >= 0f ? Mathf.Exp(-(t - _fallenAt) / 0.25f) : 1f;
            if (fallFade < 0.015f) return;
            foreach (var j in AllJoints)
            {
                int g = GroupOf(j);
                float x = t < _delay[g] ? 1f : Mathf.Exp(-(t - _delay[g]) / _tau[g]);
                float u = _u0 * x * fallFade;
                if (u < 0.01f) continue;
                Vector3 target = Phi0(ns, j);
                if (_armSag && g == 3)
                {
                    float sag = BMath.SmoothStep(_sagStart, _sagStart + _sagDur, t);
                    bool shoulder = j == JointId.LShoulder || j == JointId.RShoulder;
                    Vector3 hang = shoulder ? _hangShoulder : _hangElbow;
                    target = new Vector3(Mathf.Lerp(target.x, hang.x, sag), shoulder ? Mathf.Lerp(target.y, hang.y, sag) : target.y, target.z);
                }
                HoldBackground(ns, j, target, u, 0.55f);
            }
        }
    }

    /// <summary>Gel musculaire localisé : une articulation reste verrouillée dans sa position (co-contraction tonique).</summary>
    internal sealed class JointLock : MotorProgram
    {
        private readonly JointId _joint;
        private readonly float _u;
        private bool _captured;
        private Vector3 _angles;

        public JointLock(JointId joint, float start, float duration, float u)
        {
            Name = "Verrouillage " + joint;
            _joint = joint;
            Start = start; Duration = duration; Attack = 0.03f; Release = 0.25f;
            _u = u;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            if (!_captured)
            {
                _angles = Phi(ns, _joint);
                _captured = true;
            }
            Hold(ns, _joint, _angles, _u * k, 1f);
        }
    }

    /// <summary>
    /// Réponse d'escrime (fencing response, Hosseini &amp; Lifshitz 2009) : après traumatisme crânien, un bras
    /// s'étend vers l'avant pendant que l'autre se fléchit, la tête tournée vers le bras tendu.
    /// </summary>
    internal sealed class Fencing : MotorProgram
    {
        private readonly int _ext;
        private readonly float _u;

        public Fencing(int extendedSide, float start, float duration, float u)
        {
            Name = "Réponse d'escrime";
            _ext = extendedSide;
            Start = start; Duration = duration; Attack = 0.1f; Release = 0.6f;
            _u = u;
            Groups = MotorGroup.Arms | MotorGroup.Neck;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            Pose(ns, AnatomyData.Shoulder(_ext), 90f, 10f, NoChange, u, 0.75f);
            Pose(ns, AnatomyData.Elbow(_ext), 5f, NoChange, NoChange, u, 0.75f);
            Pose(ns, AnatomyData.Shoulder(-_ext), 20f, 25f, NoChange, u, 0.75f);
            Pose(ns, AnatomyData.Elbow(-_ext), 120f, NoChange, NoChange, u, 0.75f);
            Pose(ns, JointId.Neck, NoChange, NoChange, 35f * _ext, u * 0.8f, 0.6f);
        }
    }

    // ===================================================================== agonie

    /// <summary>Porte la main vers la plaie (cinématique inverse), tête fléchie vers elle.</summary>
    internal sealed class ClutchWound : MotorProgram
    {
        private readonly WoundRecord _wound;
        private readonly Seg _fallbackSeg;
        private readonly int[] _sides;
        private readonly float _u;
        private readonly bool _abdominal;

        public ClutchWound(WoundRecord wound, Seg fallback, int[] sides, float start, float duration, float u, bool abdominal)
        {
            Name = "Agrippe la plaie";
            _wound = wound; _fallbackSeg = fallback; _sides = sides;
            Start = start; Duration = duration; Attack = 0.25f; Release = 0.4f;
            _u = u; _abdominal = abdominal;
            IsAgonyEvent = true;
            foreach (int s in sides) Groups |= s < 0 ? MotorGroup.LArm : MotorGroup.RArm;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            var thorax = ns.Body[Seg.Thorax];
            Vector3 target;
            var seg = _wound != null ? ns.Body.SegmentForBone(_wound.Bone) : null;
            if (_wound != null && _wound.Bone != null && seg != null)
                target = _wound.CurrentWorldPoint;
            else
                target = ns.Body[_fallbackSeg].Com;
            // La main se pose sur la surface extérieure du thorax, jamais à l'intérieur de la cage thoracique.
            var body = ns.Body;
            target += BMath.SafeNormalize(target - thorax.Com, thorax.F) * 0.05f;
            float u = _u * k;
            foreach (int side in _sides)
            {
                Vector3 safeTarget = JointKinematics.ClampOutsideThorax(
                    target, thorax.Pos, thorax.R, thorax.U, thorax.F,
                    body.ThoraxChestZ, body.ThoraxYBot, body.ThoraxYTop,
                    body.ThoraxRx + 0.035f, BodyModel.ThoraxRyCap, body.ThoraxRz + 0.035f, side, 0.02f);
                if (ArmIK(ns, side, safeTarget, out float f, out float a, out float e))
                {
                    a = Mathf.Max(a, JointKinematics.MinShoulderAbductionRad(f * BMath.Deg) / BMath.Deg);
                    Pose(ns, AnatomyData.Shoulder(side), f, a, NoChange, u, 0.55f);
                    Pose(ns, AnatomyData.Elbow(side), e, NoChange, NoChange, u, 0.55f);
                }
            }
            Offset(ns, JointId.Neck, 0, Phi(ns, JointId.Neck).x, 10f, u * 0.4f, 0.4f);
            if (_abdominal)
            {
                Offset(ns, JointId.Lumbar, 0, Phi0(ns, JointId.Lumbar).x, 25f, u * 0.6f, 0.5f);
                for (int side = -1; side <= 1; side += 2)
                {
                    Pose(ns, AnatomyData.Hip(side), 55f, NoChange, NoChange, u * 0.5f, 0.45f);
                    Pose(ns, AnatomyData.Knee(side), 75f, NoChange, NoChange, u * 0.5f, 0.45f);
                }
            }
        }
    }

    /// <summary>Repli en position fœtale.</summary>
    internal sealed class Curl : MotorProgram
    {
        private readonly float _u;

        public Curl(float start, float duration, float u)
        {
            Name = "Repli fœtal";
            Start = start; Duration = duration; Attack = 0.35f; Release = 0.5f;
            _u = u; IsAgonyEvent = true;
            Groups = MotorGroup.All;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            Pose(ns, JointId.Lumbar, 45f, NoChange, NoChange, u, 0.75f);
            Pose(ns, JointId.Neck, 35f, NoChange, NoChange, u * 0.8f, 0.75f);
            for (int s = -1; s <= 1; s += 2)
            {
                Pose(ns, AnatomyData.Hip(s), 95f, 8f, NoChange, u, 0.75f);
                Pose(ns, AnatomyData.Knee(s), 115f, NoChange, NoChange, u, 0.75f);
                Pose(ns, AnatomyData.Shoulder(s), 70f, -15f, NoChange, u * 0.8f, 0.75f);
                Pose(ns, AnatomyData.Elbow(s), 120f, NoChange, NoChange, u * 0.8f, 0.75f);
            }
        }
    }

    /// <summary>Extension tonique incontrôlée (opisthotonos) : dos cambré, tête rejetée en arrière, jambes tendues.</summary>
    internal sealed class Opisthotonus : MotorProgram
    {
        private readonly float _u;

        public Opisthotonus(float start, float duration, float u)
        {
            Name = "Extension tonique";
            Start = start; Duration = duration; Attack = 0.12f; Release = 0.4f;
            _u = u; IsAgonyEvent = true;
            Groups = MotorGroup.All;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            Pose(ns, JointId.Lumbar, -25f, NoChange, NoChange, u, 0.85f);
            Pose(ns, JointId.Neck, -45f, NoChange, NoChange, u, 0.85f);
            for (int s = -1; s <= 1; s += 2)
            {
                Pose(ns, AnatomyData.Hip(s), -15f, NoChange, NoChange, u, 0.85f);
                Pose(ns, AnatomyData.Knee(s), 5f, NoChange, NoChange, u, 0.85f);
                Pose(ns, AnatomyData.Shoulder(s), -20f, 20f, NoChange, u * 0.7f, 0.8f);
                Pose(ns, AnatomyData.Elbow(s), 15f, NoChange, NoChange, u * 0.7f, 0.8f);
            }
        }
    }

    /// <summary>Salve de myoclonies (contractions brèves, souvent bilatérales) : crise anoxique, syncope convulsive.</summary>
    internal sealed class ClonicBurst : MotorProgram
    {
        private readonly float _freq;
        private readonly float _duty;
        private readonly float _u;
        private readonly MotorGroup[] _masks;

        public ClonicBurst(float start, int jerks, float freq, float u, System.Random rng)
        {
            Name = "Myoclonies";
            _freq = freq;
            _duty = BMath.Range(rng, 0.25f, 0.4f);
            Start = start; Duration = jerks / freq; Attack = 0f; Release = 0f;
            _u = u; IsAgonyEvent = true;
            _masks = new MotorGroup[jerks];
            for (int i = 0; i < jerks; i++)
            {
                MotorGroup m = MotorGroup.None;
                if (BMath.Chance(rng, 0.7f)) m |= MotorGroup.Arms;
                else m |= BMath.Chance(rng, 0.5f) ? MotorGroup.LArm : MotorGroup.RArm;
                if (BMath.Chance(rng, 0.6f)) m |= MotorGroup.Legs;
                if (BMath.Chance(rng, 0.5f)) m |= MotorGroup.Trunk;
                if (BMath.Chance(rng, 0.5f)) m |= MotorGroup.Neck;
                _masks[i] = m;
                Groups |= m;
            }
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float phase = (ns.Time - Start) * _freq;
            int idx = Mathf.Clamp((int)phase, 0, _masks.Length - 1);
            if (phase - Mathf.Floor(phase) > _duty) return;
            float u = _u * k * (1f - 0.5f * idx / Mathf.Max(1, _masks.Length));
            var m = _masks[idx];
            if ((m & MotorGroup.Trunk) != 0) Pose(ns, JointId.Lumbar, 30f, NoChange, NoChange, u, 0.95f);
            if ((m & MotorGroup.Neck) != 0) Pose(ns, JointId.Neck, 25f, NoChange, NoChange, u, 0.95f);
            for (int s = -1; s <= 1; s += 2)
            {
                if ((m & (s < 0 ? MotorGroup.LArm : MotorGroup.RArm)) != 0)
                {
                    Pose(ns, AnatomyData.Shoulder(s), 65f, NoChange, NoChange, u, 0.95f);
                    Pose(ns, AnatomyData.Elbow(s), 115f, NoChange, NoChange, u, 0.95f);
                }
                if ((m & MotorGroup.Legs) != 0)
                {
                    Pose(ns, AnatomyData.Hip(s), 70f, NoChange, NoChange, u, 0.95f);
                    Pose(ns, AnatomyData.Knee(s), 100f, NoChange, NoChange, u, 0.95f);
                }
            }
        }
    }

    /// <summary>Pédalage agonique : flexion-extension alternée des membres inférieurs.</summary>
    internal sealed class Pedal : MotorProgram
    {
        private readonly float _omega;
        private readonly float _u;
        private readonly int _onlySide;

        public Pedal(float start, float duration, float freq, float u, int onlySide)
        {
            Name = "Pédalage";
            Start = start; Duration = duration; Attack = 0.2f; Release = 0.3f;
            _omega = BMath.TwoPi * freq; _u = u; _onlySide = onlySide;
            IsAgonyEvent = true;
            Groups = MotorGroup.Legs;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            float t = ns.Time - Start;
            for (int s = -1; s <= 1; s += 2)
            {
                if (_onlySide != 0 && s != _onlySide) continue;
                float ph = _omega * t + (s < 0 ? 0f : Mathf.PI);
                float hip = 15f + 45f * (0.5f + 0.5f * Mathf.Sin(ph));
                float knee = 20f + 70f * (0.5f + 0.5f * Mathf.Sin(ph + 0.5f * Mathf.PI));
                Pose(ns, AnatomyData.Hip(s), hip, NoChange, NoChange, u, 0.85f);
                Pose(ns, AnatomyData.Knee(s), knee, NoChange, NoChange, u, 0.85f);
            }
        }
    }

    /// <summary>Ruade : flexion puis extension explosive d'un membre inférieur.</summary>
    internal sealed class Kick : MotorProgram
    {
        private readonly int _side;
        private readonly float _u;

        public Kick(int side, float start, float duration, float u)
        {
            Name = "Ruade";
            _side = side;
            Start = start; Duration = duration; Attack = 0.05f; Release = 0.12f;
            _u = u; IsAgonyEvent = true;
            Groups = MotorGroup.Legs;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float x = (ns.Time - Start) / Duration;
            float u = _u * k;
            if (x < 0.4f)
            {
                Pose(ns, AnatomyData.Hip(_side), 70f, NoChange, NoChange, u * 0.8f, 0.7f);
                Pose(ns, AnatomyData.Knee(_side), 110f, NoChange, NoChange, u * 0.8f, 0.7f);
            }
            else
            {
                Pose(ns, AnatomyData.Hip(_side), -5f, NoChange, NoChange, u, 0.95f);
                Pose(ns, AnatomyData.Knee(_side), 0f, NoChange, NoChange, u, 0.95f);
            }
        }
    }

    /// <summary>Mouvement de bras incontrôlé / tentative d'agripper (le bras se lève puis retombe).</summary>
    internal sealed class ArmFlail : MotorProgram
    {
        private readonly int[] _sides;
        private readonly Vector3[] _targetA, _targetB;
        private readonly float _u;

        public ArmFlail(int[] sides, float start, float duration, float u, System.Random rng)
        {
            Name = "Bras incontrôlé";
            _sides = sides;
            Start = start; Duration = duration; Attack = 0.15f; Release = 0.35f;
            _u = u; IsAgonyEvent = true;
            _targetA = new Vector3[sides.Length];
            _targetB = new Vector3[sides.Length];
            for (int i = 0; i < sides.Length; i++)
            {
                _targetA[i] = new Vector3(BMath.Range(rng, 60f, 160f), BMath.Range(rng, -10f, 70f), BMath.Range(rng, 5f, 70f));
                _targetB[i] = new Vector3(BMath.Range(rng, 30f, 130f), BMath.Range(rng, -20f, 50f), BMath.Range(rng, 20f, 110f));
                Groups |= sides[i] < 0 ? MotorGroup.LArm : MotorGroup.RArm;
            }
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float x = Mathf.Clamp01((ns.Time - Start) / Duration);
            float u = _u * k;
            for (int i = 0; i < _sides.Length; i++)
            {
                Vector3 tgt = Vector3.Lerp(_targetA[i], _targetB[i], BMath.SmoothStep(0.35f, 0.75f, x));
                Pose(ns, AnatomyData.Shoulder(_sides[i]), tgt.x, tgt.y, NoChange, u, 0.85f);
                Pose(ns, AnatomyData.Elbow(_sides[i]), tgt.z, NoChange, NoChange, u, 0.85f);
            }
        }
    }

    /// <summary>
    /// Tentative de retournement vers un côté : la jambe et le bras opposés croisent la ligne médiane, le tronc
    /// et la tête tournent vers le côté de la roulade. Combiné au contact du sol, le corps roule.
    /// </summary>
    internal sealed class Roll : MotorProgram
    {
        private readonly int _toward;
        private readonly float _u;

        public Roll(int toward, float start, float duration, float u)
        {
            Name = "Roulade";
            _toward = toward;
            Start = start; Duration = duration; Attack = 0.25f; Release = 0.35f;
            _u = u; IsAgonyEvent = true;
            Groups = MotorGroup.All;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            int o = -_toward;
            Pose(ns, AnatomyData.Hip(o), 65f, -20f, NoChange, u, 0.85f);
            Pose(ns, AnatomyData.Knee(o), 80f, NoChange, NoChange, u, 0.8f);
            Pose(ns, AnatomyData.Hip(_toward), 25f, NoChange, NoChange, u * 0.5f, 0.7f);
            Pose(ns, AnatomyData.Knee(_toward), 40f, NoChange, NoChange, u * 0.5f, 0.7f);
            Pose(ns, AnatomyData.Shoulder(o), 95f, -22f, NoChange, u, 0.85f);
            Pose(ns, AnatomyData.Elbow(o), 25f, NoChange, NoChange, u, 0.8f);
            Pose(ns, JointId.Lumbar, 15f, NoChange, 30f * _toward, u, 0.85f);
            Pose(ns, JointId.Neck, 15f, NoChange, 40f * _toward, u * 0.8f, 0.8f);
        }
    }

    /// <summary>Rotation de la tête (mouvement de recherche, de douleur).</summary>
    internal sealed class HeadTurn : MotorProgram
    {
        private readonly float _twist, _lat, _flex;
        private readonly float _u;

        public HeadTurn(float start, float duration, float u, System.Random rng)
        {
            Name = "Rotation de tête";
            Start = start; Duration = duration; Attack = 0.2f; Release = 0.3f;
            _u = u; IsAgonyEvent = true;
            _twist = BMath.Range(rng, -55f, 55f);
            _lat = BMath.Range(rng, -20f, 20f);
            _flex = BMath.Range(rng, -30f, 30f);
            Groups = MotorGroup.Neck;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            Pose(ns, JointId.Neck, _flex, _lat, _twist, _u * k, 0.5f);
        }
    }

    /// <summary>
    /// Bras qui se tendent brusquement (extension réflexe, « grasping » agonique) : montée en ~150 ms vers une
    /// extension franche, maintien, relâchement. Répété 1 à 3 fois avec des cibles différentes.
    /// </summary>
    internal sealed class ArmThrust : MotorProgram
    {
        private readonly int[] _sides;
        private readonly float _period;
        private readonly Vector3[,] _targets; // [rep, côté] : (flexion, abduction, coude)
        private readonly float _u;

        public ArmThrust(int[] sides, int reps, float start, float period, float u, System.Random rng)
        {
            Name = "Bras tendus";
            _sides = sides;
            _period = period;
            Start = start; Duration = reps * period; Attack = 0f; Release = 0f;
            _u = u; IsAgonyEvent = true;
            _targets = new Vector3[reps, sides.Length];
            for (int r = 0; r < reps; r++)
                for (int i = 0; i < sides.Length; i++)
                    _targets[r, i] = new Vector3(BMath.Range(rng, 35f, 100f), BMath.Range(rng, 10f, 55f), BMath.Range(rng, 5f, 25f));
            foreach (int s in sides) Groups |= s < 0 ? MotorGroup.LArm : MotorGroup.RArm;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            int rep = Mathf.Clamp((int)(local / _period), 0, _targets.GetLength(0) - 1);
            float x = (local - rep * _period) / _period;
            // Contraction pendant 35 % du cycle, puis relâchement progressif (le bras retombe sans claquer).
            float env = x < 0.35f ? 1f : Mathf.Clamp01(1f - (x - 0.35f) / 0.3f);
            if (env <= 0f) return;
            float u = _u * k * env;
            for (int i = 0; i < _sides.Length; i++)
            {
                Vector3 t = _targets[rep, i];
                Pose(ns, AnatomyData.Shoulder(_sides[i]), t.x, t.y, NoChange, u, 0.95f);
                Pose(ns, AnatomyData.Elbow(_sides[i]), t.z, NoChange, NoChange, u, 0.95f);
            }
        }
    }

    /// <summary>Genoux qui se replient par saccades (réflexe de retrait en flexion), parfois en détente.</summary>
    internal sealed class LegJerk : MotorProgram
    {
        private readonly int[] _sides;
        private readonly float _freq;
        private readonly bool[] _extension;
        private readonly float _u;

        public LegJerk(int[] sides, int reps, float start, float freq, float u, System.Random rng)
        {
            Name = "Saccades des jambes";
            _sides = sides;
            _freq = freq;
            Start = start; Duration = reps / freq; Attack = 0f; Release = 0f;
            _u = u; IsAgonyEvent = true;
            _extension = new bool[reps];
            for (int r = 0; r < reps; r++) _extension[r] = BMath.Chance(rng, 0.25f);
            Groups = MotorGroup.Legs;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float phase = (ns.Time - Start) * _freq;
            int rep = Mathf.Clamp((int)phase, 0, _extension.Length - 1);
            if (phase - Mathf.Floor(phase) > 0.45f) return;
            float u = _u * k * (1f - 0.35f * rep / Mathf.Max(1, _extension.Length));
            foreach (int s in _sides)
            {
                if (_extension[rep])
                {
                    Pose(ns, AnatomyData.Hip(s), -5f, NoChange, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Knee(s), 0f, NoChange, NoChange, u, 1f);
                }
                else
                {
                    Pose(ns, AnatomyData.Hip(s), 75f, NoChange, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Knee(s), 120f, NoChange, NoChange, u, 1f);
                }
            }
        }
    }

    /// <summary>Secousse myoclonique globale : tout le corps se contracte d'un coup, en flexion ou en extension (bras projetés).</summary>
    internal sealed class Spasm : MotorProgram
    {
        private readonly bool _extensor;
        private readonly float _u;

        public Spasm(bool extensor, float start, float duration, float u)
        {
            Name = extensor ? "Spasme en extension" : "Spasme en flexion";
            _extensor = extensor;
            Start = start; Duration = duration; Attack = 0.03f; Release = 0.12f;
            _u = u; IsAgonyEvent = true;
            Groups = MotorGroup.All;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            if (_extensor)
            {
                Pose(ns, JointId.Lumbar, -25f, NoChange, NoChange, u, 1f);
                Pose(ns, JointId.Neck, -40f, NoChange, NoChange, u, 1f);
                for (int s = -1; s <= 1; s += 2)
                {
                    Pose(ns, AnatomyData.Shoulder(s), 70f, 40f, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Elbow(s), 5f, NoChange, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Hip(s), -10f, 10f, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Knee(s), 0f, NoChange, NoChange, u, 1f);
                }
            }
            else
            {
                Pose(ns, JointId.Lumbar, 35f, NoChange, NoChange, u, 1f);
                Pose(ns, JointId.Neck, 30f, NoChange, NoChange, u, 1f);
                for (int s = -1; s <= 1; s += 2)
                {
                    Pose(ns, AnatomyData.Shoulder(s), 60f, 20f, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Elbow(s), 115f, NoChange, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Hip(s), 70f, NoChange, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Knee(s), 95f, NoChange, NoChange, u, 1f);
                }
            }
        }
    }

    // ===================================================================== couches continues

    /// <summary>
    /// Contorsions continues : chaque articulation dérive lentement et au hasard autour de sa posture courante.
    /// C'est ce fond de mouvement qui donne l'impression d'un corps encore « habité » entre les gestes francs.
    /// </summary>
    internal sealed class Writhing : MotorProgram
    {
        private readonly float _amp;
        private readonly float[] _seed = new float[AnatomyData.JointCount * 2];
        private readonly float[] _speed = new float[AnatomyData.JointCount];
        private readonly Vector3[] _baseline = new Vector3[AnatomyData.JointCount];
        private bool _init;

        // Amplitude (degrés) par articulation : flexion, puis abduction / inclinaison / torsion.
        private static readonly Vector2[] Amp =
        {
            new Vector2(10f, 8f),  // rachis : flexion, torsion
            new Vector2(18f, 22f), // cou : flexion, torsion
            new Vector2(25f, 15f), // épaule G : flexion, abduction
            new Vector2(30f, 0f),  // coude G
            new Vector2(25f, 15f), // épaule D
            new Vector2(30f, 0f),  // coude D
            new Vector2(20f, 8f),  // hanche G : flexion, abduction
            new Vector2(30f, 0f),  // genou G
            new Vector2(20f, 8f),  // hanche D
            new Vector2(30f, 0f),  // genou D
        };

        public Writhing(float start, float duration, float amplitude, System.Random rng)
        {
            Name = "Contorsions";
            Start = start; Duration = duration; Attack = 0.4f; Release = 0.8f;
            _amp = amplitude;
            for (int i = 0; i < _seed.Length; i++) _seed[i] = BMath.Range(rng, 0f, 100f);
            for (int j = 0; j < _speed.Length; j++) _speed[j] = BMath.Range(rng, 0.5f, 1.1f);
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float a = Mathf.Clamp01(ns.Agony);
            if (a < 0.03f || _amp <= 0f) return;
            float t = ns.Time;
            float lp = 1f - Mathf.Exp(-ns.Dt / 0.8f);
            // Une rigidité de posture en cours (décortication) bride les contorsions.
            float u = Mathf.Clamp01(0.35f * a * k * (1f - 0.7f * Mathf.Clamp01(Mathf.Max(ns.Rigidity, ns.RigidityPrev))));
            for (int j = 0; j < AnatomyData.JointCount; j++)
            {
                Vector3 phi = ns.Body.Joints[j].Phi;
                _baseline[j] = _init ? Vector3.Lerp(_baseline[j], phi, lp) : phi;
                float n0 = BMath.SmoothNoise(t * _speed[j], _seed[2 * j]);
                float n1 = BMath.SmoothNoise(t * _speed[j] * 0.8f, _seed[2 * j + 1]);
                float a0 = Amp[j].x * _amp * BMath.Deg;
                float a1 = Amp[j].y * _amp * BMath.Deg;
                ns.Buffer.Add(j, 0, _baseline[j].x + a0 * n0, u, 0.7f);
                if (a1 > 0f)
                {
                    var id = (JointId)j;
                    int dof = id == JointId.Lumbar || id == JointId.Neck ? 2 : 1;
                    ns.Buffer.Add(j, dof, _baseline[j][dof] + a1 * n1, u * 0.8f, 0.6f);
                }
            }
            _init = true;
        }
    }

    /// <summary>Tremblements / fasciculations (6-10 Hz) autour de la posture courante, filtrés par l'inertie.</summary>
    internal sealed class Tremor : MotorProgram
    {
        private readonly float _amp;
        private readonly float[] _freq = new float[AnatomyData.JointCount];
        private readonly float[] _phase = new float[AnatomyData.JointCount];
        private readonly Vector3[] _baseline = new Vector3[AnatomyData.JointCount];
        private bool _init;

        public Tremor(float start, float duration, float amplitudeDeg, System.Random rng)
        {
            Name = "Tremblements";
            Start = start; Duration = duration; Attack = 0.3f; Release = 0.6f;
            _amp = amplitudeDeg;
            for (int j = 0; j < _freq.Length; j++)
            {
                _freq[j] = BMath.Range(rng, 6f, 10f);
                _phase[j] = BMath.Range(rng, 0f, BMath.TwoPi);
            }
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float a = ns.Agony;
            if (a < 0.02f) return;
            float t = ns.Time;
            float lp = 1f - Mathf.Exp(-ns.Dt / 0.4f);
            for (int j = 0; j < _freq.Length; j++)
            {
                Vector3 phi = ns.Body.Joints[j].Phi;
                if (!_init) _baseline[j] = phi;
                else _baseline[j] = Vector3.Lerp(_baseline[j], phi, lp);
                var id = (JointId)j;
                bool distal = id == JointId.LElbow || id == JointId.RElbow || id == JointId.LKnee || id == JointId.RKnee || id == JointId.Neck;
                float amp = _amp * (distal ? 1f : 0.5f) * BMath.Deg;
                float osc = Mathf.Sin(BMath.TwoPi * _freq[j] * t + _phase[j]);
                ns.Buffer.Add(j, 0, _baseline[j].x + amp * osc, 0.35f * a * k, 0.95f);
            }
            _init = true;
        }
    }

    /// <summary>Gasps agoniques : brèves extensions rythmiques du tronc et du cou (respiration agonique).</summary>
    internal sealed class AgonalGasp : MotorProgram
    {
        private readonly float _period;
        private readonly float _burst;
        private readonly float _offset;

        public AgonalGasp(float start, float duration, System.Random rng)
        {
            Name = "Gasps";
            Start = start; Duration = duration; Attack = 0.2f; Release = 0.5f;
            _period = BMath.Range(rng, 2.5f, 5f);
            _burst = BMath.Range(rng, 0.3f, 0.5f);
            _offset = BMath.Range(rng, 0f, _period);
            Groups = MotorGroup.Trunk | MotorGroup.Neck;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float x = Mathf.Repeat(ns.Time - Start + _offset, _period);
            if (x > _burst) return;
            float env = Mathf.Sin(Mathf.PI * x / _burst);
            float u = 0.7f * Mathf.Sqrt(Mathf.Max(0f, ns.Agony)) * env * k;
            Offset(ns, JointId.Lumbar, 0, Phi(ns, JointId.Lumbar).x, -12f, u, 0.75f);
            Offset(ns, JointId.Neck, 0, Phi(ns, JointId.Neck).x, -18f, u, 0.75f);
            for (int s = -1; s <= 1; s += 2)
                Offset(ns, AnatomyData.Shoulder(s), 1, Phi(ns, AnatomyData.Shoulder(s)).y, 6f, u * 0.5f, 0.5f);
        }
    }

    /// <summary>Ballottement de la tête : dérive lente, faiblement activée.</summary>
    internal sealed class HeadLoll : MotorProgram
    {
        private readonly float _seed;

        public HeadLoll(float start, float duration, System.Random rng)
        {
            Name = "Tête ballante";
            Start = start; Duration = duration; Attack = 0.5f; Release = 0.8f;
            _seed = BMath.Range(rng, 0f, 100f);
            Groups = MotorGroup.Neck;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float t = ns.Time;
            float u = 0.15f * Mathf.Clamp01(ns.Agony) * k;
            if (u < 0.01f) return;
            Pose(ns, JointId.Neck, 10f + 15f * BMath.SmoothNoise(t * 0.7f, _seed), 12f * BMath.SmoothNoise(t * 0.5f, _seed + 3f),
                25f * BMath.SmoothNoise(t * 0.4f, _seed + 7f), u, 0.3f);
        }
    }

    // ===================================================================== gestes transversaux

    // Ces gestes ne dépendent pas d'une lésion précise : ils apparaissent pour tous les mécanismes, mais avec un
    // poids propre à chacun (MechanismProfile), une palette propre à chaque mort (VarietyDirector) et les capacités
    // articulaires du moment (NeuralController.PickEvent). Ils multiplient les silhouettes d'agonie au sol.

    /// <summary>Pontage : le bassin se soulève en arc, appui sur les pieds et les épaules, puis retombe.</summary>
    internal sealed class HipBridge : MotorProgram
    {
        private readonly int _reps;
        private readonly float _period, _u;

        public HipBridge(float start, float duration, int reps, float u)
        {
            Name = "Pontage";
            _reps = Mathf.Max(1, reps);
            _period = duration / _reps;
            _u = u;
            Start = start; Duration = duration; Attack = 0f; Release = 0f;
            IsAgonyEvent = true;
            Groups = MotorGroup.Legs | MotorGroup.Trunk;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            int rep = Mathf.Clamp((int)(local / _period), 0, _reps - 1);
            float x = (local - rep * _period) / _period;
            float env = Mathf.Sin(Mathf.PI * x);
            if (env <= 0.01f) return;
            float u = _u * k * env;
            Pose(ns, JointId.Lumbar, -30f, NoChange, NoChange, u, 0.9f);
            Pose(ns, JointId.Neck, -14f, NoChange, NoChange, u * 0.45f, 0.6f);
            for (int s = -1; s <= 1; s += 2)
            {
                Pose(ns, AnatomyData.Hip(s), -10f, 8f, NoChange, u, 0.85f);
                Pose(ns, AnatomyData.Knee(s), 95f, NoChange, NoChange, u, 0.85f);
                Pose(ns, AnatomyData.Shoulder(s), 28f, 30f, NoChange, u * 0.4f, 0.55f);
                Pose(ns, AnatomyData.Elbow(s), 30f, NoChange, NoChange, u * 0.4f, 0.55f);
            }
        }
    }

    /// <summary>
    /// Reptation sur les coudes (sur le ventre) : un coude se plante pendant que l'autre tire, le tronc se visse
    /// légèrement vers le coude d'appui, la tête suit. Donne au corps couché une progression visible, différente du
    /// ramper sur les mains.
    /// </summary>
    internal sealed class ElbowDrag : MotorProgram
    {
        private readonly int _reps, _lead;
        private readonly float _period, _u;

        public ElbowDrag(float start, float duration, int reps, float u, System.Random rng)
        {
            Name = "Reptation sur les coudes";
            _reps = Mathf.Max(1, reps);
            _period = duration / _reps;
            _lead = BMath.Chance(rng, 0.5f) ? -1 : 1;
            _u = u;
            Start = start; Duration = duration; Attack = 0f; Release = 0f;
            IsAgonyEvent = true;
            Groups = MotorGroup.Arms | MotorGroup.Trunk;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            int rep = Mathf.Clamp((int)(local / _period), 0, _reps - 1);
            float x = (local - rep * _period) / _period;
            float env = Mathf.Sin(Mathf.PI * x);
            if (env <= 0.01f) return;
            float u = _u * k * env;
            for (int s = -1; s <= 1; s += 2)
            {
                bool lead = s == _lead;
                Pose(ns, AnatomyData.Shoulder(s), lead ? 95f : 32f, lead ? 18f : 32f, NoChange, u, 0.85f);
                Pose(ns, AnatomyData.Elbow(s), lead ? 42f : 112f, NoChange, NoChange, u, 0.85f);
            }
            Pose(ns, JointId.Lumbar, 14f, NoChange, 10f * _lead, u * 0.6f, 0.7f);
            Pose(ns, JointId.Neck, -10f, NoChange, 12f * _lead, u * 0.5f, 0.5f);
        }
    }

    /// <summary>La tête se soulève lentement puis frappe le sol, plusieurs fois (secousse de la nuque au sol).</summary>
    internal sealed class HeadBang : MotorProgram
    {
        private readonly int _reps;
        private readonly float _period, _u;

        public HeadBang(float start, float duration, float period, float u, System.Random rng)
        {
            Name = "Tête qui frappe le sol";
            _period = Mathf.Max(0.35f, period);
            _reps = Mathf.Max(1, Mathf.RoundToInt(duration / _period));
            _u = u;
            Start = start; Duration = _reps * _period; Attack = 0f; Release = 0f;
            IsAgonyEvent = true;
            Groups = MotorGroup.Neck;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            int rep = Mathf.Clamp((int)(local / _period), 0, _reps - 1);
            float x = (local - rep * _period) / _period;
            // +1 en extension (tête soulevée), -1 en flexion (front contre le sol).
            float swing = Mathf.Sin(BMath.TwoPi * x);
            float u = _u * k * Mathf.Min(1f, Mathf.Sin(Mathf.PI * x) * 3f);
            if (u <= 0.01f) return;
            float dir = ns.Body.SupineScore > 0f ? 1f : -1f;
            Pose(ns, JointId.Neck, dir * (8f + 26f * swing), NoChange, NoChange, u, 0.75f);
            Pose(ns, JointId.Lumbar, dir * 4f * swing, NoChange, NoChange, u * 0.35f, 0.5f);
        }
    }

    /// <summary>Une jambe se croise par-dessus l'autre, le bassin tourne vers elle, puis elle retombe.</summary>
    internal sealed class LegCross : MotorProgram
    {
        private readonly int _cross;
        private readonly int _reps;
        private readonly float _period, _u;

        public LegCross(int cross, float start, float duration, float u, System.Random rng)
        {
            Name = "Jambes croisées";
            _cross = cross == 0 ? 1 : cross;
            _reps = 1 + rng.Next(2);
            _period = duration / _reps;
            _u = u;
            Start = start; Duration = duration; Attack = 0f; Release = 0f;
            IsAgonyEvent = true;
            Groups = MotorGroup.Legs | MotorGroup.Trunk;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            int rep = Mathf.Clamp((int)(local / _period), 0, _reps - 1);
            float x = (local - rep * _period) / _period;
            float env = Mathf.Sin(Mathf.PI * x);
            if (env <= 0.01f) return;
            float u = _u * k * env;
            int o = -_cross;
            Pose(ns, AnatomyData.Hip(_cross), 26f, -30f, NoChange, u, 0.8f);
            Pose(ns, AnatomyData.Knee(_cross), 62f, NoChange, NoChange, u, 0.8f);
            Pose(ns, AnatomyData.Hip(o), 6f, 10f, NoChange, u * 0.5f, 0.6f);
            Pose(ns, AnatomyData.Knee(o), 22f, NoChange, NoChange, u * 0.5f, 0.6f);
            Pose(ns, JointId.Lumbar, 6f, -7f * _cross, 12f * _cross, u * 0.6f, 0.7f);
            Pose(ns, JointId.Neck, NoChange, NoChange, -8f * _cross, u * 0.4f, 0.5f);
        }
    }

    /// <summary>Les deux bras se tendent au-dessus de la tête, tremblants, puis retombent.</summary>
    internal sealed class ArmsOverhead : MotorProgram
    {
        private readonly float _u, _amp, _freq, _phase, _asym;

        public ArmsOverhead(float start, float duration, float u, System.Random rng)
        {
            Name = "Bras au-dessus de la tête";
            _u = u;
            _amp = BMath.Range(rng, 2f, 5f);
            _freq = BMath.Range(rng, 3f, 5f);
            _phase = BMath.Range(rng, 0f, BMath.TwoPi);
            _asym = BMath.Range(rng, -1f, 1f);
            Start = start; Duration = duration;
            Attack = BMath.Range(rng, 0.3f, 0.7f); Release = BMath.Range(rng, 0.5f, 0.9f);
            IsAgonyEvent = true;
            Groups = MotorGroup.Arms;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            float tremor = _amp * Shake(ns.Time, _freq, _phase);
            for (int s = -1; s <= 1; s += 2)
            {
                float tilt = 1f + (s < 0 ? _asym : -_asym) * 0.15f;
                Pose(ns, AnatomyData.Shoulder(s), 156f * tilt + tremor, 8f, NoChange, u, 0.7f);
                Pose(ns, AnatomyData.Elbow(s), 18f - tremor, NoChange, NoChange, u, 0.7f);
            }
            Pose(ns, JointId.Lumbar, -8f, NoChange, NoChange, u * 0.35f, 0.55f);
            Pose(ns, JointId.Neck, -12f, NoChange, NoChange, u * 0.4f, 0.5f);
        }
    }

    /// <summary>Les mains pressent la poitrine par instants, comme une tentative de respiration bloquée.</summary>
    internal sealed class ChestClutch : MotorProgram
    {
        private readonly int[] _sides;
        private readonly float _u, _pressFreq;

        public ChestClutch(int[] sides, float start, float duration, float u, System.Random rng)
        {
            Name = "Mains sur la poitrine";
            _sides = sides;
            _u = u;
            _pressFreq = BMath.Range(rng, 0.6f, 1.2f);
            Start = start; Duration = duration; Attack = 0.3f; Release = 0.5f;
            IsAgonyEvent = true;
            Groups = MotorGroup.Arms | MotorGroup.Trunk;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            var th = ns.Body[Seg.Thorax];
            float press = 0.02f * Mathf.Sin(BMath.TwoPi * _pressFreq * (ns.Time - Start));
            Vector3 centre = th.Com + th.U * 0.08f + th.F * (0.06f + press);
            foreach (int side in _sides)
            {
                Vector3 target = JointKinematics.ClampOutsideThorax(centre + th.R * (side * 0.05f), th.Pos, th.R, th.U, th.F,
                    ns.Body.ThoraxChestZ, ns.Body.ThoraxYBot, ns.Body.ThoraxYTop,
                    ns.Body.ThoraxRx + 0.02f, BodyModel.ThoraxRyCap, ns.Body.ThoraxRz + 0.02f, side);
                if (!ArmIK(ns, side, target, out float f, out float a, out float e)) continue;
                Pose(ns, AnatomyData.Shoulder(side), f, a, NoChange, u, 0.55f);
                Pose(ns, AnatomyData.Elbow(side), e, NoChange, NoChange, u, 0.55f);
            }
            Pose(ns, JointId.Neck, 22f, NoChange, NoChange, u * 0.5f, 0.5f);
            Pose(ns, JointId.Lumbar, 16f, NoChange, NoChange, u * 0.4f, 0.5f);
        }
    }

    /// <summary>Torsion axiale continue : le tronc se visse lentement d'un côté à l'autre, hanches en contretorsion.</summary>
    internal sealed class TwistArch : MotorProgram
    {
        private readonly float _amp, _rate, _phase, _u, _bend;

        public TwistArch(float start, float duration, float u, System.Random rng)
        {
            Name = "Torsion du tronc";
            _amp = BMath.Range(rng, 22f, 42f);
            _rate = BMath.Range(rng, 0.22f, 0.5f);
            _phase = BMath.Range(rng, 0f, BMath.TwoPi);
            _bend = BMath.Range(rng, 4f, 14f);
            _u = u;
            Start = start; Duration = duration; Attack = 0.4f; Release = 0.6f;
            IsAgonyEvent = true;
            Groups = MotorGroup.Trunk | MotorGroup.Neck;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            float a = Mathf.Sin(BMath.TwoPi * _rate * (ns.Time - Start) + _phase);
            Pose(ns, JointId.Lumbar, -10f + 6f * a, _bend * a, _amp * a, u, 0.85f);
            Pose(ns, JointId.Neck, -6f, 5f * a, -0.55f * _amp * a, u * 0.7f, 0.7f);
            for (int s = -1; s <= 1; s += 2)
            {
                Pose(ns, AnatomyData.Hip(s), 8f, NoChange, -4f * a, u * 0.35f, 0.55f);
                Pose(ns, AnatomyData.Shoulder(s), 35f, 25f, NoChange, u * 0.35f, 0.55f);
            }
        }
    }

    /// <summary>Un talon glisse vers la fesse puis retombe (glissement du talon, réflexe de retrait lent).</summary>
    internal sealed class HeelSlide : MotorProgram
    {
        private readonly int _side, _reps;
        private readonly float _period, _u;

        public HeelSlide(int side, float start, float duration, float u, System.Random rng)
        {
            Name = "Talon qui glisse";
            _side = side == 0 ? 1 : side;
            _reps = 1 + rng.Next(3);
            _period = duration / _reps;
            _u = u;
            Start = start; Duration = duration; Attack = 0f; Release = 0f;
            IsAgonyEvent = true;
            Groups = MotorGroup.Legs;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            int rep = Mathf.Clamp((int)(local / _period), 0, _reps - 1);
            float x = (local - rep * _period) / _period;
            float env = Mathf.Sin(Mathf.PI * x);
            if (env <= 0.01f) return;
            float u = _u * k * env;
            Pose(ns, AnatomyData.Hip(_side), 28f + 34f * env, 6f, NoChange, u, 0.8f);
            Pose(ns, AnatomyData.Knee(_side), 22f + 104f * env, NoChange, NoChange, u, 0.8f);
            Pose(ns, AnatomyData.Hip(-_side), 4f, NoChange, NoChange, u * 0.3f, 0.45f);
        }
    }

    /// <summary>Les épaules montent vers les oreilles à chaque gaspe (muscles accessoires), puis retombent.</summary>
    internal sealed class ShoulderHunch : MotorProgram
    {
        private readonly float _period, _phase, _u, _amp;

        public ShoulderHunch(float start, float duration, float period, float u, System.Random rng)
        {
            Name = "Épaules haussées";
            _period = Mathf.Max(0.5f, period);
            _phase = BMath.Range(rng, 0f, 1f);
            _amp = BMath.Range(rng, 0.7f, 1.2f);
            _u = u;
            Start = start; Duration = duration; Attack = 0.4f; Release = 0.7f;
            IsAgonyEvent = true;
            Groups = MotorGroup.Arms | MotorGroup.Neck | MotorGroup.Trunk;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            float p = Mathf.Repeat((ns.Time - Start) / _period + _phase, 1f);
            float e = 0.5f - 0.5f * Mathf.Cos(BMath.TwoPi * p);
            for (int s = -1; s <= 1; s += 2)
            {
                Pose(ns, AnatomyData.Shoulder(s), 16f + 10f * e * _amp, 6f + 14f * e * _amp, NoChange, u, 0.5f);
                Pose(ns, AnatomyData.Elbow(s), 18f + 12f * e, NoChange, NoChange, u * 0.6f, 0.45f);
            }
            Pose(ns, JointId.Neck, 6f + 9f * e, NoChange, NoChange, u * 0.6f, 0.5f);
            Pose(ns, JointId.Lumbar, -4f * e, NoChange, NoChange, u * 0.3f, 0.4f);
        }
    }

    /// <summary>La nuque se cambre en extension forcée, tremble, puis la tête retombe.</summary>
    internal sealed class NeckArch : MotorProgram
    {
        private readonly float _u, _amp, _freq, _phase;

        public NeckArch(float start, float duration, float u, System.Random rng)
        {
            Name = "Nuque cambrée";
            _u = u;
            _amp = BMath.Range(rng, 32f, 48f);
            _freq = BMath.Range(rng, 4f, 7f);
            _phase = BMath.Range(rng, 0f, BMath.TwoPi);
            Start = start; Duration = duration;
            Attack = BMath.Range(rng, 0.25f, 0.5f); Release = BMath.Range(rng, 0.4f, 0.7f);
            IsAgonyEvent = true;
            Groups = MotorGroup.Neck | MotorGroup.Trunk;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            float tremor = 3f * Shake(ns.Time, _freq, _phase);
            Pose(ns, JointId.Neck, -_amp + tremor, NoChange, NoChange, u, 0.8f);
            Pose(ns, JointId.Lumbar, -10f + tremor * 0.4f, NoChange, NoChange, u * 0.45f, 0.6f);
        }
    }

    /// <summary>Frisson de tout le corps : bouffée de secousses rapides et fines, puis silence.</summary>
    internal sealed class Shudder : MotorProgram
    {
        private readonly float _freq, _amp, _u;
        private readonly float[] _phase = new float[AnatomyData.JointCount];

        public Shudder(float start, float duration, float freq, float u, System.Random rng)
        {
            Name = "Frisson";
            _freq = freq;
            _amp = BMath.Range(rng, 2f, 5f);
            _u = u;
            for (int j = 0; j < _phase.Length; j++) _phase[j] = BMath.Range(rng, 0f, BMath.TwoPi);
            Start = start; Duration = duration; Attack = 0.05f; Release = 0.25f;
            IsAgonyEvent = true;
            Groups = MotorGroup.All;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            float env = k * Mathf.Sin(Mathf.PI * Mathf.Clamp01(local / Mathf.Max(0.05f, Duration)));
            float u = _u * env;
            if (u <= 0.01f) return;
            for (int j = 0; j < AnatomyData.JointCount; j++)
            {
                Vector3 phi = ns.Body.Joints[j].Phi;
                float osc = Mathf.Sin(BMath.TwoPi * _freq * local + _phase[j]);
                ns.Buffer.Add(j, 0, phi.x + _amp * u * osc * BMath.Deg, u, 0.95f);
            }
        }
    }
}
