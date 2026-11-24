using Brutality.Anatomy;
using Brutality.Core;
using EFT;
using UnityEngine;
using static Brutality.Neural.Motor;

namespace Brutality.Neural
{
    /// <summary>
    /// Labored breathing: chest rises, head tilts slightly back, and shoulders lift on each inhalation
    /// (brief inhale, slow exhale). Fast and deep during agony, it slows and fades as agony subsides.
    /// Background layer, masked by overt gestures.
    /// </summary>
    internal sealed class LaboredBreathing : MotorProgram
    {
        private readonly float _fast, _slow, _amp;
        private float _phase;
        private readonly Vector3[] _base = new Vector3[AnatomyData.JointCount];
        private bool _init;

        private static readonly JointId[] Breath = { JointId.Lumbar, JointId.Neck, JointId.LShoulder, JointId.RShoulder };

        public LaboredBreathing(float start, float duration, System.Random rng)
        {
            Name = "Labored breathing";
            Start = start; Duration = duration; Attack = 0.6f; Release = 1f;
            _fast = BMath.Range(rng, 0.9f, 1.4f);
            _slow = BMath.Range(rng, 2.4f, 3.6f);
            _amp = BMath.Range(rng, 0.8f, 1.2f);
            _phase = BMath.Range(rng, 0f, 1f);
            Groups = MotorGroup.None;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float a = Mathf.Clamp01(ns.Agony * 1.3f);
            float period = Mathf.Lerp(_slow, _fast, a);
            _phase += ns.Dt / period;
            float p = _phase - Mathf.Floor(_phase);
            float inhale = p < 0.38f ? BMath.SmoothStep(0f, 0.38f, p) : 1f - BMath.SmoothStep(0.38f, 1f, p);
            float amp = _amp * (0.35f + 0.65f * a);
            float lp = 1f - Mathf.Exp(-ns.Dt / 0.6f);
            foreach (var j in Breath)
            {
                int ji = (int)j;
                Vector3 phi = ns.Body.Joints[ji].Phi;
                _base[ji] = _init ? Vector3.Lerp(_base[ji], phi, lp) : phi;
            }
            _init = true;
            float u = 0.2f * k;
            var lum = _base[(int)JointId.Lumbar];
            var neck = _base[(int)JointId.Neck];
            ns.Buffer.AddBackground((int)JointId.Lumbar, 0, lum.x - 6f * amp * inhale * BMath.Deg, u, 0.25f);
            ns.Buffer.AddBackground((int)JointId.Neck, 0, neck.x - 8f * amp * inhale * BMath.Deg, u, 0.25f);
            for (int s = -1; s <= 1; s += 2)
            {
                int ji = (int)AnatomyData.Shoulder(s);
                ns.Buffer.AddBackground(ji, 0, _base[ji].x + 4f * amp * inhale * BMath.Deg, u * 0.8f, 0.2f);
                ns.Buffer.AddBackground(ji, 1, _base[ji].y + 6f * amp * inhale * BMath.Deg, u * 0.8f, 0.2f);
            }
        }
    }

    /// <summary>
    /// Last breath: a deep inhalation (chest rises, head tilts back, shoulders lift), a brief pause,
    /// then exhalation and the head rolling to the side as everything relaxes.
    /// </summary>
    internal sealed class LastBreath : MotorProgram
    {
        private readonly float _inhale, _hold;
        private readonly int _turn;
        private readonly float _turnDeg, _latDeg;
        private bool _captured;
        private Vector3 _lum, _neck, _shL, _shR;

        public LastBreath(float start, float duration, System.Random rng)
        {
            Name = "Last breath";
            Start = start; Duration = duration; Attack = 0.05f; Release = 0.1f;
            _inhale = BMath.Range(rng, 0.32f, 0.48f);
            _hold = BMath.Range(rng, 0.12f, 0.28f);
            _turn = BMath.Chance(rng, 0.5f) ? -1 : 1;
            _turnDeg = BMath.Range(rng, 35f, 60f);
            _latDeg = BMath.Range(rng, 10f, 22f);
            Groups = MotorGroup.Trunk | MotorGroup.Neck;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            if (!_captured)
            {
                _lum = Phi(ns, JointId.Lumbar); _neck = Phi(ns, JointId.Neck);
                _shL = Phi(ns, JointId.LShoulder); _shR = Phi(ns, JointId.RShoulder);
                _captured = true;
            }
            float x = ns.Time - Start;
            float D = BMath.Deg;
            if (x < _inhale + _hold)
            {
                float p = BMath.SmoothStep(0f, _inhale, x);
                float u = 0.75f * k;
                ns.Buffer.Add((int)JointId.Lumbar, 0, _lum.x - 14f * p * D, u, 0.6f);
                ns.Buffer.Add((int)JointId.Neck, 0, _neck.x - 14f * p * D, u, 0.6f);
                ns.Buffer.Add((int)JointId.LShoulder, 1, _shL.y + 10f * p * D, u * 0.7f, 0.5f);
                ns.Buffer.Add((int)JointId.RShoulder, 1, _shR.y + 10f * p * D, u * 0.7f, 0.5f);
                ns.Buffer.Add((int)JointId.LShoulder, 0, _shL.x + 6f * p * D, u * 0.7f, 0.5f);
                ns.Buffer.Add((int)JointId.RShoulder, 0, _shR.x + 6f * p * D, u * 0.7f, 0.5f);
            }
            else
            {
                float q = BMath.SmoothStep(_inhale + _hold, Duration, x);
                float u = 0.6f * (1f - 0.85f * q) * k;
                ns.Buffer.Add((int)JointId.Lumbar, 0, _lum.x + 6f * q * D, u * 0.6f, 0.4f);
                ns.Buffer.Add((int)JointId.Neck, 0, _neck.x + 12f * q * D, u, 0.4f);
                ns.Buffer.Add((int)JointId.Neck, 1, _neck.y + _turn * _latDeg * q * D, u, 0.4f);
                ns.Buffer.Add((int)JointId.Neck, 2, _neck.z + _turn * _turnDeg * q * D, u, 0.4f);
            }
        }
    }

    // ===================================================================== agony gestures

    /// <summary>
    /// Tonic-clonic seizure (cerebral anoxia, convulsive syncope): tonic phase (axial extension, flexed and
    /// abducted arms, stiff legs, vibration), then clonic flexion jerks whose rhythm slows down
    /// (from ~7 to ~2 Hz) until post-ictal exhaustion.
    /// </summary>
    internal sealed class TonicClonicSeizure : MotorProgram
    {
        private readonly float _tonic;
        private readonly float[] _jerks;
        private readonly float _jerkLen;
        private readonly float _u;

        public TonicClonicSeizure(float start, float u, System.Random rng)
        {
            Name = "Tonic-clonic seizure";
            _tonic = BMath.Range(rng, 0.45f, 0.9f);
            int n = rng.Next(5, 12);
            _jerks = new float[n];
            float t = _tonic;
            for (int i = 0; i < n; i++)
            {
                _jerks[i] = t;
                float f = n > 1 ? (float)i / (n - 1) : 0f;
                t += Mathf.Lerp(0.14f, 0.48f, Mathf.Pow(f, 1.3f)) * BMath.Range(rng, 0.9f, 1.1f);
            }
            _jerkLen = BMath.Range(rng, 0.07f, 0.1f);
            Start = start; Duration = t + 0.1f; Attack = 0.06f; Release = 0.1f;
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.All;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            if (local < _tonic)
            {
                float u = _u * k;
                ns.Rigidity = Mathf.Max(ns.Rigidity, u);
                float v = 3f * Shake(local, 7f, 0f);
                Pose(ns, JointId.Lumbar, -22f, NoChange, NoChange, u, 1f);
                Pose(ns, JointId.Neck, -24f + v, NoChange, NoChange, u, 1f);
                for (int s = -1; s <= 1; s += 2)
                {
                    Pose(ns, AnatomyData.Shoulder(s), 50f, 45f, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Elbow(s), 75f + v, NoChange, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Hip(s), -6f, -8f, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Knee(s), 2f + Mathf.Abs(v), NoChange, NoChange, u, 1f);
                }
                return;
            }
            for (int i = 0; i < _jerks.Length; i++)
            {
                float lj = local - _jerks[i];
                if (lj < 0f || lj > _jerkLen) continue;
                float u = _u * k * (1f - 0.45f * i / _jerks.Length);
                Pose(ns, JointId.Lumbar, 28f, NoChange, NoChange, u, 1f);
                Pose(ns, JointId.Neck, 28f, NoChange, NoChange, u, 1f);
                for (int s = -1; s <= 1; s += 2)
                {
                    Pose(ns, AnatomyData.Shoulder(s), 70f, 25f, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Elbow(s), 115f, NoChange, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Hip(s), 65f, NoChange, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Knee(s), 95f, NoChange, NoChange, u, 1f);
                }
                return;
            }
        }
    }

    /// <summary>
    /// Reaching out: one arm slowly rises while trembling (skyward when supine, forward when prone
    /// or on the side), head turned toward the hand, then drops suddenly.
    /// </summary>
    internal sealed class AgonalReach : MotorProgram
    {
        private readonly int _side;
        private readonly float _flex, _abd, _elbow, _neckFlex, _seed;
        private readonly float _u;

        public AgonalReach(int side, bool skyward, float start, float duration, float u, System.Random rng)
        {
            Name = skyward ? "Reaching skyward" : "Reaching out";
            _side = side;
            if (skyward)
            {
                _flex = BMath.Range(rng, 80f, 112f); _abd = BMath.Range(rng, 8f, 25f); _elbow = BMath.Range(rng, 8f, 35f);
                _neckFlex = BMath.Range(rng, 8f, 20f);
            }
            else
            {
                _flex = BMath.Range(rng, 118f, 145f); _abd = BMath.Range(rng, 15f, 35f); _elbow = BMath.Range(rng, 8f, 28f);
                _neckFlex = BMath.Range(rng, -22f, -10f);
            }
            _seed = BMath.Range(rng, 0f, 100f);
            Start = start; Duration = duration;
            Attack = Mathf.Min(BMath.Range(rng, 0.8f, 1.3f), 0.6f * duration);
            Release = 0.06f;
            _u = u;
            IsAgonyEvent = true;
            Groups = (side < 0 ? MotorGroup.LArm : MotorGroup.RArm) | MotorGroup.Neck;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            float shake = 3f * Shake(local, 4.5f, _seed) * BMath.SmoothStep(0.3f, 0.9f, local);
            float drift = 6f * BMath.SmoothNoise(local * 0.8f, _seed);
            float u = _u * k;
            Pose(ns, AnatomyData.Shoulder(_side), _flex + shake + drift, _abd, NoChange, u, 0.7f);
            Pose(ns, AnatomyData.Elbow(_side), _elbow - shake, NoChange, NoChange, u, 0.7f);
            Pose(ns, JointId.Neck, _neckFlex, NoChange, 28f * _side, u * 0.6f, 0.5f);
        }
    }

    /// <summary>
    /// Crawl attempt (prone): arms alternate reaching far forward then pulling, opposite leg
    /// bends frog-like and pushes, head lifted. With paralyzed legs, the body drags itself.
    /// </summary>
    internal sealed class CrawlAttempt : MotorProgram
    {
        private readonly float _period;
        private readonly int _first;
        private readonly float _u;

        public CrawlAttempt(int cycles, float start, float period, float u, System.Random rng)
        {
            Name = "Crawl attempt";
            _period = period;
            _first = BMath.Chance(rng, 0.5f) ? -1 : 1;
            Start = start; Duration = cycles * period; Attack = 0.25f; Release = 0.4f;
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.All;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            float halfF = local / (0.5f * _period);
            int half = (int)halfF;
            float p = halfF - half;
            int lead = (half & 1) == 0 ? _first : -_first;
            float u = _u * k;
            for (int s = -1; s <= 1; s += 2)
            {
                float flex, abd, elbow;
                if (s == lead)
                {
                    float r = BMath.SmoothStep(0f, 0.5f, p) * (1f - BMath.SmoothStep(0.5f, 1f, p));
                    flex = Mathf.Lerp(55f, 140f, r); abd = Mathf.Lerp(40f, 25f, r); elbow = Mathf.Lerp(105f, 25f, r);
                }
                else
                {
                    flex = 58f; abd = 40f; elbow = 100f;
                }
                Pose(ns, AnatomyData.Shoulder(s), flex, abd, NoChange, u, 0.85f);
                Pose(ns, AnatomyData.Elbow(s), elbow, NoChange, NoChange, u, 0.85f);
                if (s == -lead)
                {
                    float push = BMath.SmoothStep(0.5f, 0.9f, p);
                    Pose(ns, AnatomyData.Hip(s), Mathf.Lerp(50f, 0f, push), Mathf.Lerp(30f, 10f, push), NoChange, u * 0.8f, 0.8f);
                    Pose(ns, AnatomyData.Knee(s), Mathf.Lerp(80f, 10f, push), NoChange, NoChange, u * 0.8f, 0.8f);
                }
                else
                {
                    Pose(ns, AnatomyData.Hip(s), 0f, 8f, NoChange, u * 0.5f, 0.6f);
                    Pose(ns, AnatomyData.Knee(s), 12f, NoChange, NoChange, u * 0.5f, 0.6f);
                }
            }
            Pose(ns, JointId.Neck, -20f, NoChange, NoChange, u * 0.7f, 0.7f);
            Pose(ns, JointId.Lumbar, -8f, 8f * lead, NoChange, u * 0.6f, 0.7f);
        }
    }

    /// <summary>
    /// Push-up attempt (prone): hands under shoulders, arms push while trembling, chest lifts
    /// and head rises... then an elbow gives way and the body collapses.
    /// </summary>
    internal sealed class PushUpAttempt : MotorProgram
    {
        private readonly int _buckle;
        private readonly float _height;
        private readonly float _u;

        public PushUpAttempt(float start, float duration, float u, System.Random rng)
        {
            Name = "Push-up attempt";
            _buckle = BMath.Chance(rng, 0.5f) ? -1 : 1;
            _height = BMath.Range(rng, 0.6f, 1f);
            Start = start; Duration = duration; Attack = 0.15f; Release = 0.05f;
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.Arms | MotorGroup.Trunk | MotorGroup.Neck;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            float u = _u * k;
            float p = BMath.SmoothStep(0.35f, Duration - 0.15f, local) * _height;
            float shake = 4f * Shake(local, 6f, 0f) * p;
            float buckle = BMath.SmoothStep(Duration - 0.3f, Duration - 0.08f, local);
            for (int s = -1; s <= 1; s += 2)
            {
                float elbow = Mathf.Lerp(115f, 15f, p) + shake * s;
                if (s == _buckle) elbow = Mathf.Lerp(elbow, 120f, buckle);
                Pose(ns, AnatomyData.Shoulder(s), Mathf.Lerp(35f, 80f, p), Mathf.Lerp(45f, 30f, p), NoChange, u, 0.9f);
                Pose(ns, AnatomyData.Elbow(s), elbow, NoChange, NoChange, u, 0.95f);
            }
            Pose(ns, JointId.Lumbar, Mathf.Lerp(0f, -22f, p), NoChange, NoChange, u * 0.8f, 0.8f);
            Pose(ns, JointId.Neck, Mathf.Lerp(-12f, -24f, p), NoChange, NoChange, u * 0.7f, 0.7f);
        }
    }

    /// <summary>Sit-up attempt (supine): head and chest lift off the ground, propping on elbows, then everything drops back.</summary>
    internal sealed class SitUpAttempt : MotorProgram
    {
        private readonly float _u;

        public SitUpAttempt(float start, float duration, float u)
        {
            Name = "Sit-up attempt";
            Start = start; Duration = duration; Attack = 0.25f; Release = 0.12f;
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.All;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            float p = BMath.SmoothStep(0f, 0.5f * Duration, local);
            float shake = 3f * Shake(local, 5.5f, 0f) * p;
            float u = _u * k;
            Pose(ns, JointId.Neck, Mathf.Lerp(10f, 45f, p), NoChange, NoChange, u, 0.85f);
            Pose(ns, JointId.Lumbar, Mathf.Lerp(0f, 35f, p) + shake, NoChange, NoChange, u, 0.9f);
            for (int s = -1; s <= 1; s += 2)
            {
                Pose(ns, AnatomyData.Shoulder(s), 25f, 30f, NoChange, u * 0.8f, 0.8f);
                Pose(ns, AnatomyData.Elbow(s), 100f - shake, NoChange, NoChange, u * 0.8f, 0.8f);
                Pose(ns, AnatomyData.Hip(s), 30f, NoChange, NoChange, u * 0.6f, 0.7f);
                Pose(ns, AnatomyData.Knee(s), 50f, NoChange, NoChange, u * 0.6f, 0.7f);
            }
        }
    }

    /// <summary>The head slowly lifts while trembling (upward, regardless of orientation), then drops back.</summary>
    internal sealed class HeadLift : MotorProgram
    {
        private readonly float _flex, _lat;
        private readonly float _u;

        public HeadLift(float supine, float sideUp, float start, float duration, float u, System.Random rng)
        {
            Name = "Head lift";
            if (Mathf.Abs(sideUp) > Mathf.Abs(supine)) { _flex = BMath.Range(rng, 0f, 12f); _lat = 22f * Mathf.Sign(sideUp); }
            else if (supine > 0f) { _flex = BMath.Range(rng, 26f, 38f); _lat = 0f; }
            else { _flex = BMath.Range(rng, -25f, -16f); _lat = 0f; }
            Start = start; Duration = duration; Attack = Mathf.Min(0.5f, 0.45f * duration); Release = 0.07f;
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.Neck;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            float shake = 3f * Shake(local, 5f, 1.3f);
            Pose(ns, JointId.Neck, _flex + shake, _lat, NoChange, _u * k, 0.8f);
        }
    }

    /// <summary>One knee (or both) slowly draws up with the foot sliding on the ground, then the leg extends back out.</summary>
    internal sealed class KneeRaise : MotorProgram
    {
        private readonly int[] _sides;
        private readonly float _hip, _abd, _knee;
        private readonly float _u;

        public KneeRaise(int[] sides, float start, float duration, float u, System.Random rng)
        {
            Name = sides.Length > 1 ? "Knees raising" : "Knee raise";
            _sides = sides;
            _hip = BMath.Range(rng, 60f, 85f); _abd = BMath.Range(rng, 5f, 15f); _knee = BMath.Range(rng, 90f, 115f);
            Start = start; Duration = duration; Attack = 0.5f; Release = 0.3f;
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.Legs;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float x = (ns.Time - Start) / Duration;
            float slide = BMath.SmoothStep(0.5f, 1f, x);
            float u = _u * k * (1f - 0.3f * slide);
            foreach (int s in _sides)
            {
                Pose(ns, AnatomyData.Hip(s), Mathf.Lerp(_hip, 10f, slide), _abd, NoChange, u, 0.7f);
                Pose(ns, AnatomyData.Knee(s), Mathf.Lerp(_knee, 15f, slide), NoChange, NoChange, u, 0.7f);
            }
        }
    }

    /// <summary>Heel drumming: rapid alternating small flexions and extensions of the legs.</summary>
    internal sealed class HeelDrum : MotorProgram
    {
        private readonly float _omega;
        private readonly float _u;

        public HeelDrum(float start, float duration, float freq, float u)
        {
            Name = "Heel drumming";
            Start = start; Duration = duration; Attack = 0.12f; Release = 0.25f;
            _omega = BMath.TwoPi * freq;
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.Legs;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float t = ns.Time - Start;
            float u = _u * k;
            for (int s = -1; s <= 1; s += 2)
            {
                float ph = _omega * t + (s < 0 ? 0f : Mathf.PI);
                Pose(ns, AnatomyData.Hip(s), 18f + 12f * Mathf.Sin(ph), NoChange, NoChange, u, 0.95f);
                Pose(ns, AnatomyData.Knee(s), 30f + 22f * Mathf.Sin(ph + 0.5f * Mathf.PI), NoChange, NoChange, u, 0.95f);
            }
        }
    }

    /// <summary>The arm raises then slaps the ground, 2 to 4 times.</summary>
    internal sealed class GroundSlap : MotorProgram
    {
        private readonly int[] _sides;
        private readonly float _period;
        private readonly float _u;

        public GroundSlap(int[] sides, int reps, float start, float period, float u)
        {
            Name = "Ground slap";
            _sides = sides;
            _period = period;
            Start = start; Duration = reps * period; Attack = 0f; Release = 0f;
            _u = u;
            IsAgonyEvent = true;
            foreach (int s in sides) Groups |= s < 0 ? MotorGroup.LArm : MotorGroup.RArm;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float local = ns.Time - Start;
            float x = local / _period - Mathf.Floor(local / _period);
            float u = _u * k;
            foreach (int s in _sides)
            {
                if (x < 0.45f)
                {
                    Pose(ns, AnatomyData.Shoulder(s), 75f, 40f, NoChange, u * 0.8f, 0.8f);
                    Pose(ns, AnatomyData.Elbow(s), 75f, NoChange, NoChange, u * 0.8f, 0.8f);
                }
                else if (x < 0.78f)
                {
                    Pose(ns, AnatomyData.Shoulder(s), 5f, 70f, NoChange, u, 1f);
                    Pose(ns, AnatomyData.Elbow(s), 8f, NoChange, NoChange, u, 1f);
                }
            }
        }
    }

    /// <summary>Protective arms: forearms crossed in front of the face, head turned away, sometimes knees drawn up.</summary>
    internal sealed class ShieldArms : MotorProgram
    {
        private readonly int[] _sides;
        private readonly float _flex, _elbow, _turn;
        private readonly bool _legs;
        private readonly float _u;

        public ShieldArms(int[] sides, bool legs, float start, float duration, float u, System.Random rng)
        {
            Name = "Shielding face";
            _sides = sides;
            _legs = legs;
            _flex = BMath.Range(rng, 98f, 118f);
            _elbow = BMath.Range(rng, 105f, 128f);
            _turn = BMath.Range(rng, 25f, 45f) * (BMath.Chance(rng, 0.5f) ? -1f : 1f);
            Start = start; Duration = duration; Attack = 0.12f; Release = 0.4f;
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.Neck | (legs ? MotorGroup.Legs : MotorGroup.None);
            foreach (int s in sides) Groups |= s < 0 ? MotorGroup.LArm : MotorGroup.RArm;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            float shake = 2.5f * Shake(ns.Time, 5f, 0.7f);
            foreach (int s in _sides)
            {
                float ir = InternalRotSign(s);
                Pose(ns, AnatomyData.Shoulder(s), _flex + shake, -12f, 30f * ir, u, 0.85f);
                Pose(ns, AnatomyData.Elbow(s), _elbow, NoChange, NoChange, u, 0.85f);
            }
            Pose(ns, JointId.Neck, 20f, NoChange, _turn, u * 0.8f, 0.7f);
            if (_legs)
                for (int s = -1; s <= 1; s += 2)
                {
                    Pose(ns, AnatomyData.Hip(s), 45f, NoChange, NoChange, u * 0.6f, 0.7f);
                    Pose(ns, AnatomyData.Knee(s), 75f, NoChange, NoChange, u * 0.6f, 0.7f);
                }
        }
    }

    /// <summary>Scissor legs (adductor spasticity): the thighs cross and uncross.</summary>
    internal sealed class Scissor : MotorProgram
    {
        private readonly float _period;
        private readonly int _first;
        private readonly float _u;

        public Scissor(int swaps, float start, float period, float u, System.Random rng)
        {
            Name = "Scissor legs";
            _period = period;
            _first = BMath.Chance(rng, 0.5f) ? -1 : 1;
            Start = start; Duration = swaps * period; Attack = 0.15f; Release = 0.3f;
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.Legs;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            int i = (int)((ns.Time - Start) / _period);
            int lead = (i & 1) == 0 ? _first : -_first;
            float u = _u * k;
            Pose(ns, AnatomyData.Hip(lead), 14f, -24f, NoChange, u, 0.9f);
            Pose(ns, AnatomyData.Hip(-lead), -4f, -16f, NoChange, u, 0.9f);
            Pose(ns, AnatomyData.Knee(lead), 10f, NoChange, NoChange, u * 0.8f, 0.8f);
            Pose(ns, AnatomyData.Knee(-lead), 4f, NoChange, NoChange, u * 0.8f, 0.8f);
        }
    }

    /// <summary>Side-to-side rocking: trunk and head twist, opposite leg pushes, arm crosses over.</summary>
    internal sealed class Rocking : MotorProgram
    {
        private readonly float _period, _phase;
        private readonly float _u;

        public Rocking(int cycles, float start, float period, float u, System.Random rng)
        {
            Name = "Rocking";
            _period = period;
            _phase = BMath.Chance(rng, 0.5f) ? 0f : Mathf.PI;
            Start = start; Duration = cycles * period; Attack = 0.25f; Release = 0.35f;
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.All;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float s = Mathf.Sin(BMath.TwoPi * (ns.Time - Start) / _period + _phase);
            float u = _u * k;
            Pose(ns, JointId.Lumbar, 10f, 10f * s, 28f * s, u, 0.85f);
            Pose(ns, JointId.Neck, 10f, NoChange, 32f * s, u * 0.8f, 0.7f);
            int push = s > 0f ? -1 : 1;   // the leg opposite to the twist direction pushes
            float m = Mathf.Abs(s);
            Pose(ns, AnatomyData.Hip(push), 45f * m, -10f * m, NoChange, u, 0.8f);
            Pose(ns, AnatomyData.Knee(push), 65f * m, NoChange, NoChange, u, 0.8f);
            Pose(ns, AnatomyData.Shoulder(push), 60f * m, -10f, NoChange, u * 0.8f, 0.8f);
            Pose(ns, AnatomyData.Elbow(push), 40f, NoChange, NoChange, u * 0.6f, 0.7f);
        }
    }

    /// <summary>Hands to face (burns, head, blast): inverse kinematics toward the front of the face, head flexed.</summary>
    internal sealed class HandsToFace : MotorProgram
    {
        private readonly int[] _sides;
        private readonly float _u;

        public HandsToFace(int[] sides, float start, float duration, float u)
        {
            Name = "Hands to face";
            _sides = sides;
            Start = start; Duration = duration; Attack = 0.2f; Release = 0.35f;
            _u = u;
            IsAgonyEvent = true;
            Groups = MotorGroup.Neck;
            foreach (int s in sides) Groups |= s < 0 ? MotorGroup.LArm : MotorGroup.RArm;
        }

        protected override void Emit(NeuralState ns, float k)
        {
            var head = ns.Body[Seg.Head];
            float u = _u * k;
            foreach (int s in _sides)
            {
                Vector3 target = head.Com + head.F * 0.12f + head.R * (0.04f * s);
                if (ArmIK(ns, s, target, out float f, out float a, out float e))
                {
                    a = Mathf.Max(a, JointKinematics.MinShoulderAbductionRad(f * BMath.Deg) / BMath.Deg);
                    Pose(ns, AnatomyData.Shoulder(s), f, a, NoChange, u, 0.6f);
                    Pose(ns, AnatomyData.Elbow(s), e, NoChange, NoChange, u, 0.6f);
                }
            }
            Pose(ns, JointId.Neck, 22f, NoChange, NoChange, u * 0.6f, 0.6f);
        }
    }
}
