using Brutality.Core;
using UnityEngine;

namespace Brutality.Neural
{
    internal enum AgonyEvent
    {
        Curl,
        Opisthotonus,
        Clonic,
        Pedal,
        Kick,
        Flail,
        Roll,
        Clutch,
        HeadTurn,
        ArmThrust,   // arms suddenly thrusting outward
        LegJerk,     // knees jerking into flexion
        Spasm,       // global myoclonic jerk (flexion or extension)
        Decorticate, // decorticate episode
        Lazarus,     // Lazarus sign: arms rising and crossing over the chest
        Seizure,     // tonic-clonic seizure
        Reach,       // arm reaching out, then dropping suddenly
        Crawl,       // crawl attempt (prone)
        PushUp,      // attempt to push up on arms (prone)
        SitUp,       // attempt to sit up (supine)
        HeadLift,    // head lifting
        KneeRaise,   // knee drawing up then sliding back
        HeelDrum,    // heels drumming
        GroundSlap,  // slapping the ground
        Shield,      // shielding the face
        Scissor,     // scissor legs
        Rocking,     // rocking side to side
        HandsToFace, // hands to face
        HipBridge,   // hip bridge: pelvis arched up, supported on feet and shoulders
        ElbowDrag,   // dragging on elbows (prone)
        HeadBang,    // head lifting then striking the ground
        LegCross,    // one leg crossing over the other
        ArmsOverhead,// arms stretched overhead, trembling
        ChestClutch, // hands pressing the chest (agonal breathing)
        TwistArch,   // continuous axial trunk twisting
        HeelSlide,   // heel sliding toward the buttock then extending back
        ShoulderHunch,// shoulders hunching toward the ears on each gasp
        NeckArch,    // neck forced into hyperextension then released
        Shudder,     // full-body shudder
        Count,
    }

    /// <summary>
    /// Neuromuscular timeline of an injury mechanism. Pathophysiological orders of magnitude:
    ///  - brain / brainstem destruction: tone loss within tens of ms, posturing and released
    ///    spinal reflexes (jerks, sudden limb extensions);
    ///  - spinal cord transection: immediate flaccid paralysis below the lesion level;
    ///  - circulatory arrest (heart, great vessels): consciousness lost in ~5-15 s, frequent anoxic myoclonus;
    ///  - hemorrhage / syncope: progressive collapse, convulsive syncope myoclonus;
    ///  - blast: immediate flaccidity, body thrown.
    /// Intensities are tuned for readable in-game rendering (overt gestures); "Spasm vigor" scales them.
    /// Values are sampled randomly within ranges so no two deaths look alike.
    /// </summary>
    internal sealed class MechanismProfile
    {
        public bool Startle;            // historical: startle is played by the shot behavior
        public float ToneDelay;
        public Vector2[] ToneTau = new Vector2[4]; // lower limbs, trunk, neck, upper limbs
        public float BuckleProb;         // historical: knee buckling is played by shotFallToKnees
        public float LockProb;
        public float DecorticateProb;
        public float FencingProb;
        public float GuardingProb;       // historical: abdominal guarding comes from injuredOnGround / bodyFoetal
        public float EarlyClutchProb;    // historical: hand on wound comes from the shot behavior
        public float DeathThrowProb;     // historical: thrown arm comes from the shot behavior    // reflex arm extension on impact
        public Vector2 AgonyStart = new Vector2(0.5f, 1.0f);
        public Vector2 AgonyI0;
        public Vector2 AgonyDuration;
        public float EventRate;         // events / s at intensity 1
        public float[] Weights = new float[(int)AgonyEvent.Count];
        public float TremorDeg;
        public float GaspProb;
        public float Writhing;          // relative amplitude of continuous writhing
        public bool HeadLoll = true;
        public float RigidityProb;          // continuous postural rigidity during agony
        public float BreathingProb = 0.85f; // visible labored breathing
        public float LastBreathProb = 0.65f;// last breath at end of agony

        /// <summary>Explicitly set weight: the value (even zero) will not be overwritten by shared gestures.</summary>
        private readonly bool[] _explicit = new bool[(int)AgonyEvent.Count];

        private MechanismProfile W(AgonyEvent e, float w)
        {
            Weights[(int)e] = w;
            _explicit[(int)e] = true;
            return this;
        }

        /// <summary>Adds a shared gesture without overwriting a weight explicitly set by the profile.</summary>
        private void Add(AgonyEvent e, float w)
        {
            int i = (int)e;
            if (i >= 0 && i < Weights.Length && !_explicit[i]) Weights[i] = w;
        }

        /// <summary>
        /// Shared gestures available to all mechanisms. They are weighted by the death palette and
        /// disabled by joint capacities (a paraplegic does not hip-bridge, a destroyed arm does not clutch
        /// the chest): adding them broadens the repertoire without diluting lesion-specific reflexes.
        /// Profiles that want to exclude them set them explicitly to zero.
        /// </summary>
        private MechanismProfile Shared()
        {
            Add(AgonyEvent.ChestClutch, 0.40f);
            Add(AgonyEvent.HeelSlide, 0.30f);
            Add(AgonyEvent.Shudder, 0.25f);
            Add(AgonyEvent.LegCross, 0.25f);
            return this;
        }

        private MechanismProfile Set(float rigidity = -1f, float breathing = -1f, float lastBreath = -1f)
        {
            if (rigidity >= 0f) RigidityProb = rigidity;
            if (breathing >= 0f) BreathingProb = breathing;
            if (lastBreath >= 0f) LastBreathProb = lastBreath;
            return this;
        }

        private static Vector2 V(float a, float b) => new Vector2(a, b);

        public static MechanismProfile For(DeathMechanism m) => Build(m).Shared();

        private static MechanismProfile Build(DeathMechanism m)
        {
            switch (m)
            {
                case DeathMechanism.Cerebral:
                    // Brain destroyed: rapid blackout and clean drop with only minimal residual twitching.
                    return new MechanismProfile
                    {
                        Startle = false, ToneDelay = 0f,
                        ToneTau = new[] { V(0.02f, 0.04f), V(0.02f, 0.05f), V(0.015f, 0.035f), V(0.02f, 0.05f) },
                        BuckleProb = 0.45f, LockProb = 0f, DecorticateProb = 0f, FencingProb = 0f,
                        DeathThrowProb = 0.25f,
                        AgonyStart = V(0.6f, 1.2f), AgonyI0 = V(0.05f, 0.18f), AgonyDuration = V(1.2f, 2.5f), EventRate = 0.30f,
                        TremorDeg = 2f, GaspProb = 0.1f, Writhing = 0.05f, HeadLoll = false,
                    }.W(AgonyEvent.Clonic, 0.4f).W(AgonyEvent.Spasm, 0.3f).W(AgonyEvent.ArmThrust, 0.15f).W(AgonyEvent.LegJerk, 0.2f)
                     .W(AgonyEvent.Decorticate, 0.5f).W(AgonyEvent.Shudder, 0.4f).W(AgonyEvent.HeadTurn, 0.2f)
                     .W(AgonyEvent.HipBridge, 0f).W(AgonyEvent.HeadBang, 0f).W(AgonyEvent.ArmsOverhead, 0f)
                     .W(AgonyEvent.Lazarus, 0f).W(AgonyEvent.GroundSlap, 0f).W(AgonyEvent.HeelDrum, 0f)
                     .Set(breathing: 0.15f, lastBreath: 0.1f);

                case DeathMechanism.CervicalCord:
                    return new MechanismProfile
                    {
                        Startle = false, ToneDelay = 0f,
                        ToneTau = new[] { V(0.02f, 0.04f), V(0.02f, 0.05f), V(0.2f, 0.5f), V(0.02f, 0.04f) },
                        BuckleProb = 0.3f, LockProb = 0f,
                        AgonyStart = V(0.5f, 1.0f), AgonyI0 = V(0.30f, 0.50f), AgonyDuration = V(2.5f, 5f), EventRate = 0.7f,
                        TremorDeg = 2f, GaspProb = 0.6f, Writhing = 0.15f,
                    }.W(AgonyEvent.HeadTurn, 2.5f).W(AgonyEvent.HeadLift, 1.8f).W(AgonyEvent.ShoulderHunch, 1.5f)
                     .W(AgonyEvent.HipBridge, 0f).W(AgonyEvent.HeadBang, 0f).W(AgonyEvent.ArmsOverhead, 0f)
                     .Set(breathing: 0.9f, lastBreath: 0.75f);

                case DeathMechanism.NeckVascular:
                    return new MechanismProfile
                    {
                        Startle = true, ToneDelay = 0.08f,
                        ToneTau = new[] { V(0.25f, 0.6f), V(0.4f, 0.9f), V(0.3f, 0.8f), V(0.6f, 1.5f) },
                        BuckleProb = 0.85f, LockProb = 0f, EarlyClutchProb = 0.75f, DeathThrowProb = 0.3f,
                        AgonyStart = V(0.6f, 1.1f), AgonyI0 = V(0.50f, 0.72f), AgonyDuration = V(4.5f, 7.5f), EventRate = 0.85f,
                        TremorDeg = 4f, GaspProb = 0.85f, Writhing = 0.45f,
                    }.W(AgonyEvent.Clutch, 4f).W(AgonyEvent.Curl, 1.4f).W(AgonyEvent.Roll, 1.6f)
                     .W(AgonyEvent.HeadTurn, 0.8f).W(AgonyEvent.Reach, 1.5f).W(AgonyEvent.HeadLift, 1.2f)
                     .W(AgonyEvent.Crawl, 0.9f).W(AgonyEvent.KneeRaise, 1.1f).W(AgonyEvent.Rocking, 0.8f)
                     .W(AgonyEvent.ChestClutch, 2.5f).W(AgonyEvent.ShoulderHunch, 1.4f).W(AgonyEvent.HeelSlide, 0.9f)
                     .W(AgonyEvent.ArmThrust, 0.1f).W(AgonyEvent.LegJerk, 0.1f).W(AgonyEvent.Seizure, 0f)
                     .Set(rigidity: 0.04f, breathing: 0.95f, lastBreath: 0.8f);

                case DeathMechanism.Thoracic:
                    return new MechanismProfile
                    {
                        Startle = true, ToneDelay = 0.05f,
                        ToneTau = new[] { V(0.15f, 0.45f), V(0.3f, 0.8f), V(0.3f, 0.9f), V(0.3f, 1.0f) },
                        BuckleProb = 0.8f, LockProb = 0f, EarlyClutchProb = 0.3f, DeathThrowProb = 0.5f,
                        AgonyStart = V(0.5f, 1.0f), AgonyI0 = V(0.45f, 0.70f), AgonyDuration = V(4.0f, 7.0f), EventRate = 0.75f,
                        TremorDeg = 4f, GaspProb = 0.85f, Writhing = 0.30f,
                    }.W(AgonyEvent.Clutch, 2.8f).W(AgonyEvent.ChestClutch, 2.6f).W(AgonyEvent.Curl, 1.5f).W(AgonyEvent.Roll, 1.4f)
                     .W(AgonyEvent.HeadTurn, 0.8f).W(AgonyEvent.Reach, 1.3f).W(AgonyEvent.Crawl, 0.9f)
                     .W(AgonyEvent.HeadLift, 1.0f).W(AgonyEvent.KneeRaise, 1.2f).W(AgonyEvent.Rocking, 0.8f)
                     .W(AgonyEvent.ShoulderHunch, 1.4f).W(AgonyEvent.Clonic, 0.2f).W(AgonyEvent.Spasm, 0.15f)
                     .W(AgonyEvent.ArmThrust, 0.1f).W(AgonyEvent.LegJerk, 0.1f).W(AgonyEvent.Seizure, 0f)
                     .Set(rigidity: 0.05f, breathing: 0.95f, lastBreath: 0.8f);

                case DeathMechanism.ThoracicCord:
                {
                    var p = For(DeathMechanism.Thoracic);
                    p.BuckleProb = 0.2f; // flaccid legs: they give way on their own
                    p.Weights[(int)AgonyEvent.Pedal] = 0f;
                    p.Weights[(int)AgonyEvent.Kick] = 0f;
                    p.Weights[(int)AgonyEvent.LegJerk] = 0f;
                    p.Weights[(int)AgonyEvent.Curl] = 0.3f;
                    p.Weights[(int)AgonyEvent.Flail] = 0.5f;
                    p.Weights[(int)AgonyEvent.ArmThrust] = 0.2f;
                    p.Weights[(int)AgonyEvent.KneeRaise] = 0f; p.Weights[(int)AgonyEvent.HeelDrum] = 0f;
                    p.Weights[(int)AgonyEvent.Scissor] = 0f; p.Weights[(int)AgonyEvent.SitUp] = 0f; p.Weights[(int)AgonyEvent.Rocking] = 0.3f;
                    p.Weights[(int)AgonyEvent.Crawl] = 2.0f; p.Weights[(int)AgonyEvent.PushUp] = 1.2f; p.Weights[(int)AgonyEvent.Reach] = 1.8f;
                    p.W(AgonyEvent.ElbowDrag, 1.6f); p.W(AgonyEvent.ChestClutch, 1.6f);
                    p.W(AgonyEvent.HipBridge, 0f); p.W(AgonyEvent.LegCross, 0f); p.W(AgonyEvent.HeelSlide, 0f);
                    return p;
                }

                case DeathMechanism.Abdominal:
                    return new MechanismProfile
                    {
                        Startle = true, ToneDelay = 0.08f,
                        ToneTau = new[] { V(0.2f, 0.5f), V(0.6f, 1.4f), V(0.6f, 1.4f), V(0.8f, 1.6f) },
                        BuckleProb = 0.9f, LockProb = 0f, GuardingProb = 0.85f, EarlyClutchProb = 0.5f, DeathThrowProb = 0.25f,
                        AgonyStart = V(0.6f, 1.2f), AgonyI0 = V(0.50f, 0.72f), AgonyDuration = V(4.5f, 7.5f), EventRate = 0.80f,
                        TremorDeg = 3.5f, GaspProb = 0.6f, Writhing = 0.40f,
                    }.W(AgonyEvent.Curl, 3.2f).W(AgonyEvent.Clutch, 3.2f).W(AgonyEvent.Roll, 1.8f)
                     .W(AgonyEvent.HeadTurn, 0.6f).W(AgonyEvent.Rocking, 1.6f).W(AgonyEvent.KneeRaise, 2.0f)
                     .W(AgonyEvent.Crawl, 1.0f).W(AgonyEvent.Reach, 1.0f).W(AgonyEvent.HeadLift, 0.9f)
                     .W(AgonyEvent.LegCross, 1.0f).W(AgonyEvent.ChestClutch, 1.2f)
                     .W(AgonyEvent.ArmThrust, 0.1f).W(AgonyEvent.LegJerk, 0.15f).W(AgonyEvent.Seizure, 0f)
                     .Set(breathing: 0.9f, lastBreath: 0.75f);

                case DeathMechanism.LumbarCord:
                {
                    var p = For(DeathMechanism.Abdominal);
                    p.BuckleProb = 0.2f;
                    p.GuardingProb = 0.4f;
                    p.Weights[(int)AgonyEvent.Pedal] = 0f;
                    p.Weights[(int)AgonyEvent.Kick] = 0f;
                    p.Weights[(int)AgonyEvent.LegJerk] = 0f;
                    p.Weights[(int)AgonyEvent.Flail] = 0.4f;
                    p.Weights[(int)AgonyEvent.Roll] = 1.2f;
                    p.Weights[(int)AgonyEvent.ArmThrust] = 0.2f;
                    p.Weights[(int)AgonyEvent.KneeRaise] = 0f; p.Weights[(int)AgonyEvent.HeelDrum] = 0f;
                    p.Weights[(int)AgonyEvent.Scissor] = 0f; p.Weights[(int)AgonyEvent.Rocking] = 0.6f;
                    p.Weights[(int)AgonyEvent.Crawl] = 2.0f; p.Weights[(int)AgonyEvent.PushUp] = 1.1f;
                    p.W(AgonyEvent.ElbowDrag, 1.6f); p.W(AgonyEvent.ChestClutch, 1.3f);
                    p.W(AgonyEvent.HipBridge, 0f); p.W(AgonyEvent.LegCross, 0f); p.W(AgonyEvent.HeelSlide, 0f);
                    return p;
                }

                case DeathMechanism.Limb:
                    return new MechanismProfile
                    {
                        Startle = true, ToneDelay = 0.06f,
                        ToneTau = new[] { V(0.2f, 0.6f), V(0.5f, 1.2f), V(0.5f, 1.2f), V(0.5f, 1.3f) },
                        BuckleProb = 0.85f, LockProb = 0f, EarlyClutchProb = 0.35f, DeathThrowProb = 0.35f,
                        AgonyStart = V(0.5f, 1.1f), AgonyI0 = V(0.45f, 0.70f), AgonyDuration = V(4.0f, 7.0f), EventRate = 0.75f,
                        TremorDeg = 4f, GaspProb = 0.6f, Writhing = 0.35f,
                    }.W(AgonyEvent.Clutch, 3.0f).W(AgonyEvent.Curl, 1.6f).W(AgonyEvent.Roll, 1.4f)
                     .W(AgonyEvent.HeadTurn, 0.7f).W(AgonyEvent.Crawl, 1.4f).W(AgonyEvent.Reach, 1.3f)
                     .W(AgonyEvent.KneeRaise, 1.1f).W(AgonyEvent.HeadLift, 0.9f).W(AgonyEvent.Rocking, 0.9f)
                     .W(AgonyEvent.HeelSlide, 1.2f).W(AgonyEvent.LegCross, 1.0f)
                     .W(AgonyEvent.ArmThrust, 0.1f).W(AgonyEvent.LegJerk, 0.15f).W(AgonyEvent.Seizure, 0f)
                     .Set(breathing: 0.9f, lastBreath: 0.75f);

                case DeathMechanism.Blast:
                    return new MechanismProfile
                    {
                        Startle = false, ToneDelay = 0f,
                        ToneTau = new[] { V(0.01f, 0.03f), V(0.01f, 0.03f), V(0.01f, 0.03f), V(0.01f, 0.03f) },
                        BuckleProb = 0f, LockProb = 0f,
                        AgonyStart = V(1.0f, 1.8f), AgonyI0 = V(0.08f, 0.22f), AgonyDuration = V(1.2f, 2.5f), EventRate = 0.30f,
                        TremorDeg = 2f, GaspProb = 0.2f, Writhing = 0.10f,
                    }.W(AgonyEvent.Curl, 0.8f).W(AgonyEvent.Shudder, 0.6f).W(AgonyEvent.HeadTurn, 0.4f)
                     .W(AgonyEvent.HipBridge, 0f).W(AgonyEvent.HeadBang, 0f).W(AgonyEvent.ArmsOverhead, 0f)
                     .Set(rigidity: 0f, breathing: 0.4f, lastBreath: 0.3f);

                case DeathMechanism.Syncope:
                    return new MechanismProfile
                    {
                        Startle = false, ToneDelay = 0.12f,
                        ToneTau = new[] { V(0.35f, 0.8f), V(0.6f, 1.3f), V(0.5f, 1.2f), V(0.5f, 1.2f) },
                        BuckleProb = 0.95f, LockProb = 0f,
                        AgonyStart = V(0.9f, 1.6f), AgonyI0 = V(0.35f, 0.55f), AgonyDuration = V(3.0f, 5.5f), EventRate = 0.55f,
                        TremorDeg = 3f, GaspProb = 0.7f, Writhing = 0.20f,
                    }.W(AgonyEvent.Curl, 0.8f).W(AgonyEvent.Roll, 0.5f).W(AgonyEvent.HeadTurn, 0.8f)
                     .W(AgonyEvent.HeadLift, 0.5f).W(AgonyEvent.Reach, 0.6f).W(AgonyEvent.Shudder, 1.2f)
                     .W(AgonyEvent.Clonic, 0.35f).W(AgonyEvent.Seizure, 0.2f)
                     .Set(rigidity: 0.05f, breathing: 0.9f, lastBreath: 0.85f);

                case DeathMechanism.FallTrauma:
                    return new MechanismProfile
                    {
                        Startle = false, ToneDelay = 0f,
                        ToneTau = new[] { V(0.01f, 0.02f), V(0.01f, 0.02f), V(0.01f, 0.02f), V(0.01f, 0.02f) },
                        BuckleProb = 0f, LockProb = 0f,
                        AgonyStart = V(0.8f, 1.4f), AgonyI0 = V(0.08f, 0.20f), AgonyDuration = V(1.2f, 2.4f), EventRate = 0.30f,
                        TremorDeg = 2f, GaspProb = 0.25f, Writhing = 0.08f,
                    }.W(AgonyEvent.HeadTurn, 0.5f).W(AgonyEvent.Curl, 0.6f).W(AgonyEvent.Shudder, 0.5f)
                     .W(AgonyEvent.HipBridge, 0f).W(AgonyEvent.HeadBang, 0f).W(AgonyEvent.ArmsOverhead, 0f)
                     .Set(rigidity: 0f, breathing: 0.5f, lastBreath: 0.35f);

                case DeathMechanism.Burn:
                    return new MechanismProfile
                    {
                        Startle = true, ToneDelay = 0.1f,
                        ToneTau = new[] { V(0.3f, 0.7f), V(0.5f, 1.2f), V(0.5f, 1.2f), V(0.5f, 1.2f) },
                        BuckleProb = 0.9f, LockProb = 0f, DeathThrowProb = 0.4f,
                        AgonyStart = V(0.5f, 1.0f), AgonyI0 = V(0.65f, 0.85f), AgonyDuration = V(5.0f, 8.0f), EventRate = 1.0f,
                        TremorDeg = 4.5f, GaspProb = 0.7f, Writhing = 0.75f,
                    }.W(AgonyEvent.Roll, 3.5f).W(AgonyEvent.Curl, 2.5f).W(AgonyEvent.Rocking, 1.8f)
                     .W(AgonyEvent.Shield, 1.6f).W(AgonyEvent.HandsToFace, 1.6f).W(AgonyEvent.Crawl, 1.2f)
                     .W(AgonyEvent.KneeRaise, 1.0f).W(AgonyEvent.Shudder, 1.0f)
                     .Set(rigidity: 0f, breathing: 0.95f, lastBreath: 0.7f);

                case DeathMechanism.Melee:
                    return new MechanismProfile
                    {
                        Startle = true, ToneDelay = 0.06f,
                        ToneTau = new[] { V(0.2f, 0.5f), V(0.4f, 0.9f), V(0.4f, 0.9f), V(0.5f, 1.1f) },
                        BuckleProb = 0.85f, LockProb = 0f, EarlyClutchProb = 0.4f, DeathThrowProb = 0.2f,
                        AgonyStart = V(0.5f, 1.0f), AgonyI0 = V(0.45f, 0.68f), AgonyDuration = V(3.5f, 6.5f), EventRate = 0.70f,
                        TremorDeg = 3.5f, GaspProb = 0.65f, Writhing = 0.30f,
                    }.W(AgonyEvent.Clutch, 2.5f).W(AgonyEvent.Curl, 2.0f).W(AgonyEvent.Shield, 1.4f)
                     .W(AgonyEvent.HandsToFace, 1.2f).W(AgonyEvent.Roll, 1.2f).W(AgonyEvent.HeadTurn, 0.7f)
                     .W(AgonyEvent.KneeRaise, 1.0f).W(AgonyEvent.ChestClutch, 1.4f)
                     .Set(rigidity: 0.04f, breathing: 0.9f, lastBreath: 0.75f);
            }
            return Build(DeathMechanism.Thoracic);
        }
    }
}
