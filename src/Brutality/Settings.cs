using BepInEx.Configuration;

namespace Brutality
{
    /// <summary>
    /// Configuration BepInEx (fichier BepInEx/config/com.brutality.ragdoll.cfg, modifiable en jeu via F12).
    /// </summary>
    internal static class Settings
    {
        // 1. Général
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> MaxMotionSeconds;
        public static ConfigEntry<bool> AffectOwnCorpse;
        public static ConfigEntry<bool> AffectObservedCorpses;
        public static ConfigEntry<int> MaxSimultaneousCorpses;
        public static ConfigEntry<float> FullSimulationDistance;
        public static ConfigEntry<bool> PermanentSimulation;
        public static ConfigEntry<int> MaxPermanentCorpses;

        // 2. Corps
        public static ConfigEntry<float> BodyMassKg;
        public static ConfigEntry<float> BodyMassVariation;
        public static ConfigEntry<bool> IncludeEquipmentMass;
        public static ConfigEntry<float> GroundFriction;

        // 3. Comportement
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

        // 5. Stabilité numérique
        public static ConfigEntry<float> JointMassBalancing;
        public static ConfigEntry<int> SolverIterations;
        public static ConfigEntry<string> DriveConvention;

        // 6. Debug
        public static ConfigEntry<bool> DebugLogging;
        public static ConfigEntry<string> EuphoriaForce;

        public static void Bind(ConfigFile cfg)
        {
            const string g = "1. Général";
            Enabled = cfg.Bind(g, "Activer Brutality", true,
                "Remplace la dynamique de mort/ragdoll vanilla par la simulation biomécanique de Brutality. Désactivé : comportement vanilla pour les morts suivantes.");
            MaxMotionSeconds = cfg.Bind(g, "Durée maximale du mouvement (s)", 10f,
                new ConfigDescription("Durée de la phase active (chute, agonie), plafonnée à 10 s. Ensuite le corps reste simulé au repos " +
                                      "(simulation permanente) ou est figé (rigidbodies kinématiques).",
                    new AcceptableValueRange<float>(3f, 10f)));
            PermanentSimulation = cfg.Bind(g, "Simulation permanente des cadavres", true,
                "Les cadavres ne sont plus jamais figés : ils restent simulés (au repos, ils dorment dans PhysX et ne coûtent presque rien) " +
                "et bougent quand on leur tire dessus ou qu'on les heurte. Désactivé : figés à la fin du mouvement (vanilla). " +
                "Ne s'applique pas aux corps observés d'un client Fika (la pose finale reste celle de l'hôte).");
            MaxPermanentCorpses = cfg.Bind(g, "Cadavres simulés en permanence (max)", 40,
                new ConfigDescription("Au-delà, les plus anciens sont figés comme en vanilla.", new AcceptableValueRange<int>(5, 150)));
            AffectOwnCorpse = cfg.Bind(g, "Appliquer à votre propre corps", true,
                "Votre propre mort utilise aussi la simulation Brutality.");
            AffectObservedCorpses = cfg.Bind(g, "Appliquer aux corps observés (Fika client)", true,
                "En coop Fika, anime aussi les corps dont la physique est simulée par l'hôte (la pose finale reste celle de l'hôte).");
            MaxSimultaneousCorpses = cfg.Bind(g, "Corps simulés simultanément (max)", 12,
                new ConfigDescription("Au-delà, les nouveaux corps reçoivent seulement la physique passive (tissus, butées, masses), sans agonie.",
                    new AcceptableValueRange<int>(1, 40)));
            FullSimulationDistance = cfg.Bind(g, "Distance de simulation complète (m)", 150f,
                new ConfigDescription("Au-delà de cette distance de la caméra, l'agonie est désactivée (chute et tissus restent simulés).",
                    new AcceptableValueRange<float>(20f, 1000f)));

            const string b = "2. Corps";
            BodyMassKg = cfg.Bind(b, "Masse corporelle moyenne (kg)", 80f,
                new ConfigDescription("Masse moyenne d'un combattant nu, répartie par segment selon de Leva (1996).",
                    new AcceptableValueRange<float>(50f, 120f)));
            BodyMassVariation = cfg.Bind(b, "Variation de masse (écart-type relatif)", 0.10f,
                new ConfigDescription("Variabilité morphologique (ordre de grandeur ANSUR II).", new AcceptableValueRange<float>(0f, 0.3f)));
            IncludeEquipmentMass = cfg.Bind(b, "Inclure la masse du matériel porté", true,
                "Gilet, plaques, sac et casque alourdissent le tronc et la tête et déplacent leur centre de masse.");
            GroundFriction = cfg.Bind(b, "Friction des tissus au sol", 0.8f,
                new ConfigDescription("Coefficient de frottement dynamique peau/vêtements contre le sol.", new AcceptableValueRange<float>(0.2f, 1.2f)));

            const string c = "3. Comportement";
            PostMortemReaction = cfg.Bind(c, "Réaction des cadavres aux balles", 1f,
                new ConfigDescription("Impulsion des balles sur un cadavre (amplifiée de 1 + 2 × ce réglage, avec rebond sur le sol) et " +
                                      "contraction idiomusculaire locale (phénomène de Zsako). 0 = impulsion physique seule.",
                    new AcceptableValueRange<float>(0f, 2f)));
            AgonyIntensity = cfg.Bind(c, "Intensité des agonies", 1f,
                new ConfigDescription("Multiplie la fréquence et la force des mouvements involontaires au sol (0 = aucune agonie).",
                    new AcceptableValueRange<float>(0f, 2f)));
            SpasmVigor = cfg.Bind(c, "Vigueur des spasmes", 1f,
                new ConfigDescription("Force et netteté des gestes involontaires (bras qui se tendent, genoux qui se replient, convulsions) " +
                                      "et amplitude des contorsions continues. 1 = rendu spectaculaire par défaut, 0,5 = plus sobre.",
                    new AcceptableValueRange<float>(0.3f, 2f)));
            FallDrive = cfg.Bind(c, "Force des chutes dirigées", 1f,
                new ConfigDescription("Perte d'équilibre initiale qui fait basculer le corps (en avant, à la renverse, sur le côté, en pivotant). " +
                                      "0 = le corps s'effondre seulement sous son poids.", new AcceptableValueRange<float>(0f, 2f)));
            PosturingIntensity = cfg.Bind(c, "Intensité du posturing cérébral", 1f,
                new ConfigDescription("Décérébration (membres raides en extension, bras en rotation interne), décortication (bras repliés sur " +
                                      "la poitrine), signe de Lazare et chutes en planche. 0 = jamais, 2 = plus fréquent et plus marqué.",
                    new AcceptableValueRange<float>(0f, 2f)));
            GestureVariety = cfg.Bind(c, "Variété des gestes", 1f,
                new ConfigDescription("Fréquence des gestes élaborés (ramper, se relever, main tendue, crises, chutes spécifiques à la course, " +
                                      "à la posture accroupie ou couchée) par rapport aux réflexes simples. 0 = gestes de base uniquement.",
                    new AcceptableValueRange<float>(0f, 2f)));
            VisibleBreathing = cfg.Bind(c, "Respiration agonique visible", true,
                "Soulèvement rythmique du buste pendant l'agonie, qui ralentit jusqu'au dernier soupir.");
            ReflexIntensity = cfg.Bind(c, "Intensité des réflexes", 1f,
                new ConfigDescription("Sursaut, flexion de protection, posturing cérébral, verrouillages articulaires.",
                    new AcceptableValueRange<float>(0f, 2f)));
            MuscleStrength = cfg.Bind(c, "Force musculaire", 1f,
                new ConfigDescription("Multiplie les couples maximaux (Anderson 2007).", new AcceptableValueRange<float>(0.2f, 2f)));
            PassiveStiffness = cfg.Bind(c, "Raideur passive des articulations", 1f,
                new ConfigDescription("Multiplie le couple passif exponentiel aux butées (Riener & Edrich 1999).",
                    new AcceptableValueRange<float>(0.2f, 3f)));

            const string i = "4. Impacts";
            BulletMomentumMultiplier = cfg.Bind(i, "Multiplicateur de quantité de mouvement des balles", 1f,
                new ConfigDescription("1 = transfert physique réel (m·v du projectile). Une balle ne projette pas un corps : la chute vient surtout de la perte du tonus.",
                    new AcceptableValueRange<float>(0f, 10f)));
            ExplosionImpulseMultiplier = cfg.Bind(i, "Multiplicateur du souffle des explosions", 1f,
                new ConfigDescription("Échelle de l'impulsion de souffle répartie sur les segments.", new AcceptableValueRange<float>(0f, 5f)));
            CancelForeignImpulses = cfg.Bind(i, "Neutraliser les impulsions de mort des autres mods", true,
                "Annule, au premier pas physique, les forces appliquées au cadavre par d'autres mods au moment de la mort " +
                "(ex. HollywoodFX « Gore » : jusqu'à 200 N·s) avant d'appliquer l'impulsion balistique réelle.");

            const string s = "5. Stabilité numérique";
            JointMassBalancing = cfg.Bind(s, "Équilibrage des masses articulaires", 1f,
                new ConfigDescription("1 = méthode EFT (stabilité maximale du solveur). 0 = masses brutes (plus exact, moins stable).",
                    new AcceptableValueRange<float>(0f, 1f)));
            DriveConvention = cfg.Bind(s, "Convention des drives articulaires", "Auto",
                new ConfigDescription("Sens de la rotation cible des drives PhysX. Auto = vérifié pendant les premières morts du raid.",
                    new AcceptableValueList<string>("Auto", "Normale", "Inversée")));
            SolverIterations = cfg.Bind(s, "Itérations du solveur PhysX", 12,
                new ConfigDescription("Itérations de position par segment simulé.", new AcceptableValueRange<int>(6, 30)));

            DebugLogging = cfg.Bind("6. Debug", "Journalisation détaillée", false,
                "Écrit le diagnostic de chaque mort (mécanisme, énergie, programmes moteurs, comportements Euphoria) dans LogOutput.log.");
            EuphoriaForce = cfg.Bind("6. Debug", "Forcer un comportement Euphoria", "Auto",
                new ConfigDescription("Pour tester : impose le plan de chute Euphoria des morts suivantes (debout).",
                    new AcceptableValueList<string>("Auto", "Titube", "Chute à genoux", "Équilibre puis effondrement", "Moulinets",
                        "Effondrement inerte", "Roulade", "Effondrement assis", "Effondrement latéral", "Plongeon", "Chute en planche",
                        "À quatre pattes", "Glissade sur les genoux", "Pivot", "Vrille")));
        }
    }
}
