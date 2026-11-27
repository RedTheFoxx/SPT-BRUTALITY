using BepInEx.Configuration;

namespace Brutality
{
    /// <summary>
    /// BepInEx configuration (BepInEx/config/com.red.brutality.cfg, editable in-game via F12).
    /// </summary>
    internal static class Settings
    {
        // 1. General
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> MaxMotionSeconds;
        public static ConfigEntry<bool> AffectOwnCorpse;
        public static ConfigEntry<bool> AffectObservedCorpses;
        public static ConfigEntry<int> MaxSimultaneousCorpses;
        public static ConfigEntry<float> FullSimulationDistance;
        public static ConfigEntry<bool> PermanentSimulation;
        public static ConfigEntry<int> MaxPermanentCorpses;
        public static ConfigEntry<bool> DropCorpseWeapon;

        // 2. Body
        public static ConfigEntry<float> BodyMassKg;
        public static ConfigEntry<float> BodyMassVariation;
        public static ConfigEntry<bool> IncludeEquipmentMass;
        public static ConfigEntry<float> GroundFriction;

        // 3. Behavior
        public static ConfigEntry<float> PostMortemReaction;
        public static ConfigEntry<float> AgonyIntensity;
        public static ConfigEntry<float> ReflexIntensity;
        public static ConfigEntry<float> SpasmVigor;
        public static ConfigEntry<float> FallDrive;
        public static ConfigEntry<float> PosturingIntensity;
        public static ConfigEntry<float> GestureVariety;
        public static ConfigEntry<bool> VisibleBreathing;
        public static ConfigEntry<float> MuscleStrength;
        public static ConfigEntry<float> PassiveStiffness;

        // 4. Impacts
        public static ConfigEntry<float> BulletMomentumMultiplier;
        public static ConfigEntry<float> ExplosionImpulseMultiplier;
        public static ConfigEntry<bool> CancelForeignImpulses;

        // 5. Numerical Stability
        public static ConfigEntry<float> JointMassBalancing;
        public static ConfigEntry<int> SolverIterations;
        public static ConfigEntry<string> DriveConvention;

        // 6. Debug
        public static ConfigEntry<bool> DebugLogging;
        public static ConfigEntry<string> EuphoriaForce;

        public static void Bind(ConfigFile cfg)
        {
            const string g = "1. General";
            Enabled = cfg.Bind(g, "Enable Brutality", true,
                "Replaces vanilla death/ragdoll dynamics with Brutality's biomechanical simulation. Disabled: vanilla behavior for subsequent deaths.");
            MaxMotionSeconds = cfg.Bind(g, "Max motion duration (s)", 10f,
                new ConfigDescription("Duration of the active phase (fall, agony), capped at 10 s. Afterwards the body stays simulated at rest " +
                                      "(permanent simulation) or is frozen (kinematic rigidbodies).",
                    new AcceptableValueRange<float>(3f, 10f)));
            PermanentSimulation = cfg.Bind(g, "Permanent corpse simulation", true,
                "Corpses are never frozen: they stay simulated (at rest they sleep in PhysX and cost almost nothing) " +
                "and react when shot or bumped. Disabled: frozen at the end of motion (vanilla). " +
                "Does not apply to observed bodies on a Fika client (final pose remains the host's).");
            MaxPermanentCorpses = cfg.Bind(g, "Max permanently simulated corpses", 40,
                new ConfigDescription("Beyond this limit, the oldest corpses are frozen as in vanilla.", new AcceptableValueRange<int>(5, 150)));
            DropCorpseWeapon = cfg.Bind(g, "Drop corpse weapon to ground", true,
                "Detaches the weapon joint on Brutality-managed corpses so the weapon falls freely to the ground instead of vibrating against the ragdoll.");
            AffectOwnCorpse = cfg.Bind(g, "Apply to your own corpse", true,
                "Your own death also uses the Brutality simulation.");
            AffectObservedCorpses = cfg.Bind(g, "Apply to observed corpses (Fika client)", true,
                "In Fika co-op, also animates bodies whose physics are simulated by the host (final pose remains the host's).");
            MaxSimultaneousCorpses = cfg.Bind(g, "Max simultaneous simulated corpses", 12,
                new ConfigDescription("Beyond this limit, new bodies receive only passive physics (tissues, limits, masses), without agony.",
                    new AcceptableValueRange<int>(1, 40)));
            FullSimulationDistance = cfg.Bind(g, "Full simulation distance (m)", 150f,
                new ConfigDescription("Beyond this distance from the camera, agony is disabled (fall and passive tissues remain simulated).",
                    new AcceptableValueRange<float>(20f, 1000f)));

            const string b = "2. Body";
            BodyMassKg = cfg.Bind(b, "Average body mass (kg)", 80f,
                new ConfigDescription("Average mass of an unarmored fighter, distributed per segment according to de Leva (1996).",
                    new AcceptableValueRange<float>(50f, 120f)));
            BodyMassVariation = cfg.Bind(b, "Body mass variation (relative std dev)", 0.10f,
                new ConfigDescription("Morphological variability (ANSUR II order of magnitude).", new AcceptableValueRange<float>(0f, 0.3f)));
            IncludeEquipmentMass = cfg.Bind(b, "Include carried gear mass", true,
                "Rig, plates, backpack, and helmet add weight to the trunk and head and shift their center of mass.");
            GroundFriction = cfg.Bind(b, "Tissue ground friction", 0.8f,
                new ConfigDescription("Dynamic friction coefficient of skin/clothing against the ground.", new AcceptableValueRange<float>(0.2f, 1.2f)));

            const string c = "3. Behavior";
            PostMortemReaction = cfg.Bind(c, "Corpse bullet reaction", 1f,
                new ConfigDescription("Bullet impulse on a corpse (amplified by 1 + 2 * this setting, with ground bounce) and " +
                                      "local idiomuscular contraction (Zsako phenomenon). 0 = physical impulse only.",
                    new AcceptableValueRange<float>(0f, 2f)));
            AgonyIntensity = cfg.Bind(c, "Agony intensity", 1f,
                new ConfigDescription("Multiplies frequency and strength of involuntary ground movements (0 = no agony).",
                    new AcceptableValueRange<float>(0f, 2f)));
            SpasmVigor = cfg.Bind(c, "Spasm vigor", 1f,
                new ConfigDescription("Strength and crispness of involuntary gestures (arms extending, knees jerking up, convulsions) " +
                                      "and amplitude of continuous contortions. 1 = dramatic default, 0.5 = subtler.",
                    new AcceptableValueRange<float>(0.3f, 2f)));
            FallDrive = cfg.Bind(c, "Directional fall drive", 1f,
                new ConfigDescription("Initial loss-of-balance drive that tips the body (forward, backward, sideways, pivoting). " +
                                      "0 = body collapses solely under its own weight.", new AcceptableValueRange<float>(0f, 2f)));
            PosturingIntensity = cfg.Bind(c, "Cerebral posturing intensity", 1f,
                new ConfigDescription("Decerebration (stiff extended limbs, internally rotated arms), decortication (arms flexed onto " +
                                      "chest), Lazarus sign, and board falls. 0 = never, 2 = more frequent and pronounced.",
                    new AcceptableValueRange<float>(0f, 2f)));
            GestureVariety = cfg.Bind(c, "Gesture variety", 1f,
                new ConfigDescription("Frequency of elaborate gestures (crawling, pushing up, reaching hand, seizures, sprint/crouch/prone-specific falls) " +
                                      "versus basic reflexes. 0 = basic gestures only.",
                    new AcceptableValueRange<float>(0f, 2f)));
            VisibleBreathing = cfg.Bind(c, "Visible agonal breathing", true,
                "Rhythmic rising of the chest during agony, slowing down until the final breath.");
            ReflexIntensity = cfg.Bind(c, "Reflex intensity", 1f,
                new ConfigDescription("Flinch, protective flexion, cerebral posturing, joint locks.",
                    new AcceptableValueRange<float>(0f, 2f)));
            MuscleStrength = cfg.Bind(c, "Muscle strength", 1f,
                new ConfigDescription("Multiplies peak muscle torques (Anderson 2007).", new AcceptableValueRange<float>(0.2f, 2f)));
            PassiveStiffness = cfg.Bind(c, "Passive joint stiffness", 1f,
                new ConfigDescription("Multiplies exponential passive torque at joint limits (Riener & Edrich 1999).",
                    new AcceptableValueRange<float>(0.2f, 3f)));

            const string i = "4. Impacts";
            BulletMomentumMultiplier = cfg.Bind(i, "Bullet momentum multiplier", 1f,
                new ConfigDescription("1 = real physical transfer (projectile m*v). A bullet does not launch a body: the fall comes mostly from tone loss.",
                    new AcceptableValueRange<float>(0f, 10f)));
            ExplosionImpulseMultiplier = cfg.Bind(i, "Explosion blast multiplier", 1f,
                new ConfigDescription("Scale of blast impulse distributed across segments.", new AcceptableValueRange<float>(0f, 5f)));
            CancelForeignImpulses = cfg.Bind(i, "Neutralize death impulses from other mods", true,
                "Cancels, on the first physics step, forces applied to the corpse by other mods at death " +
                "(e.g. HollywoodFX 'Gore': up to 200 N*s) before applying the real ballistic impulse.");

            const string s = "5. Numerical stability";
            JointMassBalancing = cfg.Bind(s, "Joint mass balancing", 1f,
                new ConfigDescription("1 = EFT method (maximum solver stability). 0 = raw masses (more accurate, less stable).",
                    new AcceptableValueRange<float>(0f, 1f)));
            DriveConvention = cfg.Bind(s, "Joint drive convention", "Auto",
                new ConfigDescription("Target rotation direction for PhysX drives. Auto = verified during the first deaths of the raid.",
                    new AcceptableValueList<string>("Auto", "Normal", "Inverted")));
            SolverIterations = cfg.Bind(s, "PhysX solver iterations", 12,
                new ConfigDescription("Position iterations per simulated segment.", new AcceptableValueRange<int>(6, 30)));

            DebugLogging = cfg.Bind("6. Debug", "Detailed logging", false,
                "Writes diagnostics for each death (mechanism, energy, motor programs, Euphoria behaviors) to LogOutput.log.");
            EuphoriaForce = cfg.Bind("6. Debug", "Force Euphoria behavior", "Auto",
                new ConfigDescription("For testing: forces the Euphoria fall plan on subsequent standing deaths.",
                    new AcceptableValueList<string>("Auto", "Stagger", "Fall to knees", "Balance then collapse", "Windmill",
                        "Slack collapse", "Roll", "Seated collapse", "Side collapse", "Dive", "Board fall",
                        "All fours", "Knee slide", "Pivot", "Spin", "High fall", "On fire", "Melee guard")));
        }
    }
}
