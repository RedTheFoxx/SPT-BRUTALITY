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
        ArmThrust,   // bras qui se tendent brusquement
        LegJerk,     // genoux qui se replient par saccades
        Spasm,       // secousse myoclonique globale (flexion ou extension)
        Decorticate, // crise de décortication
        Lazarus,     // signe de Lazare : bras qui se lèvent et se croisent sur la poitrine
        Seizure,     // crise tonico-clonique
        Reach,       // main tendue, qui retombe d'un coup
        Crawl,       // tentative de ramper (sur le ventre)
        PushUp,      // tentative de se relever sur les bras (sur le ventre)
        SitUp,       // tentative de se redresser (sur le dos)
        HeadLift,    // relève la tête
        KneeRaise,   // genou qui remonte puis glisse
        HeelDrum,    // talons qui tambourinent
        GroundSlap,  // frappe le sol
        Shield,      // se protège le visage
        Scissor,     // jambes en ciseaux
        Rocking,     // balancement d'un flanc à l'autre
        HandsToFace, // mains au visage
        HipBridge,   // pontage : bassin soulevé en arc, appui sur les pieds et les épaules
        ElbowDrag,   // reptation sur les coudes (sur le ventre)
        HeadBang,    // tête qui se soulève puis frappe le sol
        LegCross,    // une jambe se croise par-dessus l'autre
        ArmsOverhead,// bras tendus au-dessus de la tête, tremblants
        ChestClutch, // mains qui pressent la poitrine (respiration agonique)
        TwistArch,   // torsion axiale continue du tronc
        HeelSlide,   // talon qui glisse vers la fesse puis revient
        ShoulderHunch,// épaules qui montent vers les oreilles à chaque gaspe
        NeckArch,    // nuque en extension forcée puis relâchée
        Shudder,     // frisson de tout le corps
        Count,
    }

    /// <summary>
    /// Chronologie neuromusculaire d'un mécanisme lésionnel. Ordres de grandeur physiopathologiques :
    ///  - destruction encéphalique / tronc cérébral : perte du tonus en quelques dizaines de ms, posturing et
    ///    réflexes médullaires libérés (secousses, extensions brusques des membres) ;
    ///  - section médullaire : paralysie flasque immédiate sous le niveau lésionnel ;
    ///  - arrêt circulatoire (cœur, gros vaisseaux) : conscience perdue en ~5-15 s, myoclonies anoxiques fréquentes ;
    ///  - hémorragie / syncope : effondrement progressif, myoclonies de la syncope convulsive ;
    ///  - souffle : flaccidité immédiate, corps projeté.
    /// Les intensités sont réglées pour un rendu lisible en jeu (gestes francs) ; « Vigueur des spasmes » les module.
    /// Les valeurs sont tirées aléatoirement dans les intervalles pour qu'aucune mort ne se ressemble.
    /// </summary>
    internal sealed class MechanismProfile
    {
        public bool Startle;            // historique : le sursaut est joué par le comportement shot
        public float ToneDelay;
        public Vector2[] ToneTau = new Vector2[4]; // membres inférieurs, tronc, cou, membres supérieurs
        public float BuckleProb;         // historique : le flambage est joué par shotFallToKnees
        public float LockProb;
        public float DecorticateProb;
        public float FencingProb;
        public float GuardingProb;       // historique : la défense abdominale vient de injuredOnGround / bodyFoetal
        public float EarlyClutchProb;    // historique : la main sur la plaie vient du comportement shot
        public float DeathThrowProb;     // historique : le bras projeté vient du comportement shot    // extension réflexe des bras à l'impact
        public Vector2 AgonyStart = new Vector2(0.5f, 1.0f);
        public Vector2 AgonyI0;
        public Vector2 AgonyDuration;
        public float EventRate;         // événements / s à intensité 1
        public float[] Weights = new float[(int)AgonyEvent.Count];
        public float TremorDeg;
        public float GaspProb;
        public float Writhing;          // amplitude relative des contorsions continues
        public bool HeadLoll = true;
        public float RigidityProb;          // rigidité de posture continue pendant l'agonie
        public float BreathingProb = 0.85f; // respiration laborieuse visible
        public float LastBreathProb = 0.65f;// dernier soupir en fin d'agonie

        /// <summary>Poids explicitement fixé : la valeur (même nulle) ne sera pas écrasée par les gestes transversaux.</summary>
        private readonly bool[] _explicit = new bool[(int)AgonyEvent.Count];

        private MechanismProfile W(AgonyEvent e, float w)
        {
            Weights[(int)e] = w;
            _explicit[(int)e] = true;
            return this;
        }

        /// <summary>Ajoute un geste transversal sans écraser un poids explicitement fixé par le profil.</summary>
        private void Add(AgonyEvent e, float w)
        {
            int i = (int)e;
            if (i >= 0 && i < Weights.Length && !_explicit[i]) Weights[i] = w;
        }

        /// <summary>
        /// Gestes transversaux, disponibles pour tous les mécanismes. Ils sont pondérés par la palette de la mort et
        /// désactivés par les capacités articulaires (un paraplégique ne fait pas de pontage, un bras détruit ne presse
        /// pas sa poitrine) : les ajouter élargit le répertoire sans diluer les réflexes propres à chaque lésion. Les
        /// profils qui veulent les écarter les mettent explicitement à zéro.
        /// </summary>
        private MechanismProfile Shared()
        {
            Add(AgonyEvent.HipBridge, 0.65f);
            Add(AgonyEvent.ElbowDrag, 0.55f);
            Add(AgonyEvent.HeadBang, 0.6f);
            Add(AgonyEvent.LegCross, 0.6f);
            Add(AgonyEvent.ArmsOverhead, 0.55f);
            Add(AgonyEvent.ChestClutch, 0.7f);
            Add(AgonyEvent.TwistArch, 0.7f);
            Add(AgonyEvent.HeelSlide, 0.55f);
            Add(AgonyEvent.ShoulderHunch, 0.5f);
            Add(AgonyEvent.NeckArch, 0.55f);
            Add(AgonyEvent.Shudder, 0.65f);
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
                    // Encéphale détruit : le tonus s'éteint en quelques dizaines de ms et le corps lâche d'un bloc.
                    // Aucun posturing — il suppose un tronc cérébral intact — et aucune rigidité : blackout puis chute
                    // instantanée, seuls les réflexes médullaires libérés subséquents (secousses) restent.
                    return new MechanismProfile
                    {
                        Startle = false, ToneDelay = 0f,
                        ToneTau = new[] { V(0.02f, 0.04f), V(0.02f, 0.05f), V(0.015f, 0.035f), V(0.02f, 0.05f) },
                        BuckleProb = 0.45f, LockProb = 0f, DecorticateProb = 0f, FencingProb = 0f,
                        DeathThrowProb = 0.35f,
                        AgonyStart = V(0.5f, 1.1f), AgonyI0 = V(0.35f, 0.6f), AgonyDuration = V(3.5f, 7f), EventRate = 1.4f,
                        TremorDeg = 5f, GaspProb = 0.15f, Writhing = 0.3f, HeadLoll = false,
                    }.W(AgonyEvent.Clonic, 2f).W(AgonyEvent.Spasm, 2f).W(AgonyEvent.ArmThrust, 2f).W(AgonyEvent.LegJerk, 2f)
                     .W(AgonyEvent.Opisthotonus, 1.2f).W(AgonyEvent.Pedal, 0.8f).W(AgonyEvent.Kick, 0.8f).W(AgonyEvent.Flail, 0.6f)
                     .W(AgonyEvent.HeadTurn, 0.3f)
                     .W(AgonyEvent.Decorticate, 2.2f).W(AgonyEvent.Lazarus, 1.4f).W(AgonyEvent.Seizure, 1.4f)
                     .W(AgonyEvent.Scissor, 1f).W(AgonyEvent.HeelDrum, 0.9f).W(AgonyEvent.Reach, 0.4f).W(AgonyEvent.GroundSlap, 0.5f)
                     .W(AgonyEvent.HipBridge, 1.5f).W(AgonyEvent.Shudder, 1.6f).W(AgonyEvent.NeckArch, 1.2f).W(AgonyEvent.ArmsOverhead, 0.9f)
                     .Set(breathing: 0.2f, lastBreath: 0.15f);

                case DeathMechanism.CervicalCord:
                    return new MechanismProfile
                    {
                        Startle = false, ToneDelay = 0f,
                        ToneTau = new[] { V(0.02f, 0.04f), V(0.02f, 0.05f), V(0.2f, 0.5f), V(0.02f, 0.04f) },
                        BuckleProb = 0.3f, LockProb = 0.05f,
                        AgonyStart = V(0.5f, 1.0f), AgonyI0 = V(0.4f, 0.6f), AgonyDuration = V(3f, 7f), EventRate = 1.2f,
                        TremorDeg = 3f, GaspProb = 0.7f, Writhing = 0.3f,
                    }.W(AgonyEvent.HeadTurn, 3f).W(AgonyEvent.Clonic, 0.5f)
                     .W(AgonyEvent.HeadLift, 2.5f).W(AgonyEvent.HeadBang, 1.6f).W(AgonyEvent.ShoulderHunch, 2.2f)
                     .W(AgonyEvent.NeckArch, 1.4f).W(AgonyEvent.ChestClutch, 0.8f)
                     .Set(breathing: 0.95f, lastBreath: 0.8f);

                case DeathMechanism.NeckVascular:
                    return new MechanismProfile
                    {
                        Startle = true, ToneDelay = 0.08f,
                        ToneTau = new[] { V(0.25f, 0.6f), V(0.4f, 0.9f), V(0.3f, 0.8f), V(0.6f, 1.5f) },
                        BuckleProb = 0.85f, LockProb = 0.15f, EarlyClutchProb = 0.75f, DeathThrowProb = 0.3f,
                        AgonyStart = V(0.6f, 1.1f), AgonyI0 = V(0.9f, 1f), AgonyDuration = V(6f, 9f), EventRate = 2.0f,
                        TremorDeg = 7f, GaspProb = 0.9f, Writhing = 0.9f,
                    }.W(AgonyEvent.Clutch, 4f).W(AgonyEvent.Curl, 1f).W(AgonyEvent.Roll, 1.5f).W(AgonyEvent.Pedal, 1f)
                     .W(AgonyEvent.Clonic, 1f).W(AgonyEvent.Flail, 1f).W(AgonyEvent.Kick, 0.8f).W(AgonyEvent.HeadTurn, 0.8f)
                     .W(AgonyEvent.ArmThrust, 1.5f).W(AgonyEvent.LegJerk, 1.5f).W(AgonyEvent.Spasm, 1f)
                     .W(AgonyEvent.Reach, 2f).W(AgonyEvent.HeadLift, 1.5f).W(AgonyEvent.Crawl, 1.2f).W(AgonyEvent.PushUp, 1.2f)
                     .W(AgonyEvent.SitUp, 0.8f).W(AgonyEvent.KneeRaise, 1f).W(AgonyEvent.HeelDrum, 1f).W(AgonyEvent.GroundSlap, 1.2f)
                     .W(AgonyEvent.Rocking, 1f).W(AgonyEvent.Seizure, 0.6f).W(AgonyEvent.HandsToFace, 0.8f)
                     .W(AgonyEvent.ChestClutch, 2.4f).W(AgonyEvent.ShoulderHunch, 2f).W(AgonyEvent.NeckArch, 1.4f)
                     .W(AgonyEvent.HeelSlide, 1f).W(AgonyEvent.TwistArch, 1f)
                     .Set(rigidity: 0.05f, breathing: 0.95f, lastBreath: 0.8f);

                case DeathMechanism.Thoracic:
                    return new MechanismProfile
                    {
                        Startle = true, ToneDelay = 0.05f,
                        ToneTau = new[] { V(0.15f, 0.45f), V(0.3f, 0.8f), V(0.3f, 0.9f), V(0.3f, 1.0f) },
                        BuckleProb = 0.8f, LockProb = 0.25f, EarlyClutchProb = 0.3f, DeathThrowProb = 0.5f,
                        AgonyStart = V(0.5f, 1.0f), AgonyI0 = V(0.8f, 1f), AgonyDuration = V(5f, 9f), EventRate = 1.9f,
                        TremorDeg = 7f, GaspProb = 0.9f, Writhing = 0.7f,
                    }.W(AgonyEvent.Clonic, 2.5f).W(AgonyEvent.Clutch, 2f).W(AgonyEvent.Curl, 1f).W(AgonyEvent.Roll, 1.2f)
                     .W(AgonyEvent.Opisthotonus, 1f).W(AgonyEvent.Pedal, 0.8f).W(AgonyEvent.Flail, 1f).W(AgonyEvent.Kick, 0.8f)
                     .W(AgonyEvent.HeadTurn, 0.6f).W(AgonyEvent.Spasm, 2f).W(AgonyEvent.ArmThrust, 1.8f).W(AgonyEvent.LegJerk, 1.8f)
                     .W(AgonyEvent.Seizure, 1.2f).W(AgonyEvent.Reach, 1.6f).W(AgonyEvent.Crawl, 1.2f).W(AgonyEvent.PushUp, 1f)
                     .W(AgonyEvent.SitUp, 0.8f).W(AgonyEvent.HeadLift, 1.2f).W(AgonyEvent.KneeRaise, 1.2f).W(AgonyEvent.HeelDrum, 1f)
                     .W(AgonyEvent.GroundSlap, 1f).W(AgonyEvent.Shield, 0.6f).W(AgonyEvent.Rocking, 0.8f).W(AgonyEvent.Scissor, 0.4f)
                     .W(AgonyEvent.Decorticate, 0.35f)
                     .W(AgonyEvent.ChestClutch, 2.2f).W(AgonyEvent.ShoulderHunch, 1.8f).W(AgonyEvent.HipBridge, 1.2f)
                     .W(AgonyEvent.Shudder, 1.4f).W(AgonyEvent.HeadBang, 0.8f)
                     .Set(rigidity: 0.08f, breathing: 0.95f, lastBreath: 0.8f);

                case DeathMechanism.ThoracicCord:
                {
                    var p = For(DeathMechanism.Thoracic);
                    p.BuckleProb = 0.2f; // jambes flasques : elles cèdent d'elles-mêmes
                    p.Weights[(int)AgonyEvent.Pedal] = 0f;
                    p.Weights[(int)AgonyEvent.Kick] = 0f;
                    p.Weights[(int)AgonyEvent.LegJerk] = 0f;
                    p.Weights[(int)AgonyEvent.Curl] = 0.3f;
                    p.Weights[(int)AgonyEvent.Flail] = 1.6f;
                    p.Weights[(int)AgonyEvent.ArmThrust] = 2.5f;
                    // Paraplégie : les jambes ne bougent plus, le blessé se traîne sur les bras.
                    p.Weights[(int)AgonyEvent.KneeRaise] = 0f; p.Weights[(int)AgonyEvent.HeelDrum] = 0f;
                    p.Weights[(int)AgonyEvent.Scissor] = 0f; p.Weights[(int)AgonyEvent.SitUp] = 0f; p.Weights[(int)AgonyEvent.Rocking] = 0.3f;
                    p.Weights[(int)AgonyEvent.Crawl] = 2.5f; p.Weights[(int)AgonyEvent.PushUp] = 1.8f; p.Weights[(int)AgonyEvent.Reach] = 2f;
                    p.W(AgonyEvent.ElbowDrag, 2.5f); p.W(AgonyEvent.ChestClutch, 1.5f); p.W(AgonyEvent.ArmsOverhead, 1.4f);
                    p.W(AgonyEvent.HipBridge, 0f); p.W(AgonyEvent.LegCross, 0f); p.W(AgonyEvent.HeelSlide, 0f);
                    return p;
                }

                case DeathMechanism.Abdominal:
                    return new MechanismProfile
                    {
                        Startle = true, ToneDelay = 0.08f,
                        ToneTau = new[] { V(0.2f, 0.5f), V(0.6f, 1.4f), V(0.6f, 1.4f), V(0.8f, 1.6f) },
                        BuckleProb = 0.9f, LockProb = 0.15f, GuardingProb = 0.85f, EarlyClutchProb = 0.5f, DeathThrowProb = 0.25f,
                        AgonyStart = V(0.6f, 1.2f), AgonyI0 = V(0.9f, 1f), AgonyDuration = V(6f, 9f), EventRate = 2.0f,
                        TremorDeg = 6f, GaspProb = 0.6f, Writhing = 1f,
                    }.W(AgonyEvent.Curl, 3f).W(AgonyEvent.Clutch, 3f).W(AgonyEvent.Roll, 2f).W(AgonyEvent.Pedal, 1.2f)
                     .W(AgonyEvent.Clonic, 0.8f).W(AgonyEvent.Flail, 0.6f).W(AgonyEvent.Opisthotonus, 0.4f).W(AgonyEvent.Kick, 1f)
                     .W(AgonyEvent.HeadTurn, 0.6f).W(AgonyEvent.LegJerk, 2f).W(AgonyEvent.ArmThrust, 1f).W(AgonyEvent.Spasm, 1f)
                     .W(AgonyEvent.Rocking, 2f).W(AgonyEvent.KneeRaise, 2.2f).W(AgonyEvent.SitUp, 1.2f).W(AgonyEvent.Crawl, 1.3f)
                     .W(AgonyEvent.PushUp, 1f).W(AgonyEvent.Reach, 1.2f).W(AgonyEvent.HeadLift, 1f).W(AgonyEvent.HeelDrum, 0.8f)
                     .W(AgonyEvent.GroundSlap, 0.8f).W(AgonyEvent.Seizure, 0.3f)
                     .W(AgonyEvent.HipBridge, 1.6f).W(AgonyEvent.ElbowDrag, 1.4f).W(AgonyEvent.LegCross, 1.2f).W(AgonyEvent.ChestClutch, 1.2f)
                     .Set(breathing: 0.9f, lastBreath: 0.75f);

                case DeathMechanism.LumbarCord:
                {
                    var p = For(DeathMechanism.Abdominal);
                    p.BuckleProb = 0.2f;
                    p.GuardingProb = 0.4f;
                    p.Weights[(int)AgonyEvent.Pedal] = 0f;
                    p.Weights[(int)AgonyEvent.Kick] = 0f;
                    p.Weights[(int)AgonyEvent.LegJerk] = 0f;
                    p.Weights[(int)AgonyEvent.Flail] = 1.5f;
                    p.Weights[(int)AgonyEvent.Roll] = 1.2f;
                    p.Weights[(int)AgonyEvent.ArmThrust] = 2f;
                    p.Weights[(int)AgonyEvent.KneeRaise] = 0f; p.Weights[(int)AgonyEvent.HeelDrum] = 0f;
                    p.Weights[(int)AgonyEvent.Scissor] = 0f; p.Weights[(int)AgonyEvent.Rocking] = 0.6f;
                    p.Weights[(int)AgonyEvent.Crawl] = 2.5f; p.Weights[(int)AgonyEvent.PushUp] = 1.5f;
                    p.W(AgonyEvent.ElbowDrag, 2.2f); p.W(AgonyEvent.ChestClutch, 1.3f);
                    p.W(AgonyEvent.HipBridge, 0f); p.W(AgonyEvent.LegCross, 0f); p.W(AgonyEvent.HeelSlide, 0f);
                    return p;
                }

                case DeathMechanism.Limb:
                    return new MechanismProfile
                    {
                        Startle = true, ToneDelay = 0.06f,
                        ToneTau = new[] { V(0.2f, 0.6f), V(0.5f, 1.2f), V(0.5f, 1.2f), V(0.5f, 1.3f) },
                        BuckleProb = 0.85f, LockProb = 0.2f, EarlyClutchProb = 0.35f, DeathThrowProb = 0.35f,
                        AgonyStart = V(0.5f, 1.1f), AgonyI0 = V(0.8f, 1f), AgonyDuration = V(5f, 9f), EventRate = 1.9f,
                        TremorDeg = 7f, GaspProb = 0.6f, Writhing = 0.8f,
                    }.W(AgonyEvent.Clutch, 3f).W(AgonyEvent.Curl, 1.5f).W(AgonyEvent.Roll, 1.5f).W(AgonyEvent.Pedal, 1f)
                     .W(AgonyEvent.Clonic, 0.6f).W(AgonyEvent.Flail, 1f).W(AgonyEvent.Kick, 1.2f).W(AgonyEvent.HeadTurn, 0.6f)
                     .W(AgonyEvent.LegJerk, 1.5f).W(AgonyEvent.ArmThrust, 1.2f).W(AgonyEvent.Spasm, 1f)
                     .W(AgonyEvent.Crawl, 2f).W(AgonyEvent.PushUp, 1.5f).W(AgonyEvent.Reach, 1.6f).W(AgonyEvent.KneeRaise, 1f)
                     .W(AgonyEvent.GroundSlap, 1.2f).W(AgonyEvent.HeadLift, 1f).W(AgonyEvent.SitUp, 0.8f).W(AgonyEvent.Rocking, 1f)
                     .W(AgonyEvent.HeelDrum, 0.6f).W(AgonyEvent.Seizure, 0.4f)
                     .W(AgonyEvent.ElbowDrag, 1.2f).W(AgonyEvent.HeelSlide, 1.4f).W(AgonyEvent.LegCross, 1.2f).W(AgonyEvent.HipBridge, 0.8f)
                     .Set(breathing: 0.9f, lastBreath: 0.75f);

                case DeathMechanism.Blast:
                    return new MechanismProfile
                    {
                        Startle = false, ToneDelay = 0f,
                        ToneTau = new[] { V(0.01f, 0.03f), V(0.01f, 0.03f), V(0.01f, 0.03f), V(0.01f, 0.03f) },
                        BuckleProb = 0f, LockProb = 0.05f,
                        AgonyStart = V(1.0f, 1.8f), AgonyI0 = V(0.4f, 0.8f), AgonyDuration = V(3f, 7f), EventRate = 1.5f,
                        TremorDeg = 7f, GaspProb = 0.5f, Writhing = 0.4f,
                    }.W(AgonyEvent.Clonic, 2f).W(AgonyEvent.Flail, 1f).W(AgonyEvent.Curl, 0.5f).W(AgonyEvent.Roll, 0.5f)
                     .W(AgonyEvent.Pedal, 0.5f).W(AgonyEvent.HeadTurn, 0.4f).W(AgonyEvent.Spasm, 2f).W(AgonyEvent.ArmThrust, 1.5f)
                     .W(AgonyEvent.LegJerk, 1.5f)
                     .W(AgonyEvent.Seizure, 1f).W(AgonyEvent.Decorticate, 0.4f).W(AgonyEvent.GroundSlap, 0.8f)
                     .W(AgonyEvent.Reach, 0.8f).W(AgonyEvent.HeadLift, 0.8f).W(AgonyEvent.HandsToFace, 1f).W(AgonyEvent.Crawl, 0.8f)
                     .W(AgonyEvent.Shield, 0.5f)
                     .W(AgonyEvent.Shudder, 1.6f).W(AgonyEvent.ArmsOverhead, 1.2f).W(AgonyEvent.HeadBang, 0.8f)
                     .Set(rigidity: 0.2f, breathing: 0.8f, lastBreath: 0.6f);

                case DeathMechanism.Syncope:
                    return new MechanismProfile
                    {
                        Startle = false, ToneDelay = 0.12f,
                        ToneTau = new[] { V(0.35f, 0.8f), V(0.6f, 1.3f), V(0.5f, 1.2f), V(0.5f, 1.2f) },
                        BuckleProb = 0.95f, LockProb = 0.05f,
                        AgonyStart = V(0.9f, 1.6f), AgonyI0 = V(0.5f, 0.8f), AgonyDuration = V(4f, 7f), EventRate = 1.5f,
                        TremorDeg = 6f, GaspProb = 0.7f, Writhing = 0.4f,
                    }.W(AgonyEvent.Clonic, 2.5f).W(AgonyEvent.Opisthotonus, 0.6f).W(AgonyEvent.Curl, 0.5f).W(AgonyEvent.Roll, 0.3f)
                     .W(AgonyEvent.HeadTurn, 0.8f).W(AgonyEvent.Spasm, 1.5f).W(AgonyEvent.ArmThrust, 1f).W(AgonyEvent.LegJerk, 1f)
                     .W(AgonyEvent.Seizure, 2.2f).W(AgonyEvent.HeelDrum, 1f).W(AgonyEvent.HeadLift, 0.5f)
                     .W(AgonyEvent.Reach, 0.6f).W(AgonyEvent.Scissor, 0.4f)
                     .W(AgonyEvent.Shudder, 2f).W(AgonyEvent.HeadBang, 0.9f).W(AgonyEvent.NeckArch, 1f).W(AgonyEvent.ArmsOverhead, 0.7f)
                     .Set(rigidity: 0.1f, breathing: 0.9f, lastBreath: 0.85f);

                case DeathMechanism.FallTrauma:
                    return new MechanismProfile
                    {
                        Startle = false, ToneDelay = 0f,
                        ToneTau = new[] { V(0.01f, 0.02f), V(0.01f, 0.02f), V(0.01f, 0.02f), V(0.01f, 0.02f) },
                        BuckleProb = 0f,
                        AgonyStart = V(0.6f, 1.2f), AgonyI0 = V(0.2f, 0.45f), AgonyDuration = V(2f, 5f), EventRate = 1.0f,
                        TremorDeg = 4f, GaspProb = 0.4f, Writhing = 0.2f,
                    }.W(AgonyEvent.Clonic, 1f).W(AgonyEvent.HeadTurn, 0.5f).W(AgonyEvent.Spasm, 1f).W(AgonyEvent.LegJerk, 0.8f)
                     .W(AgonyEvent.HeadLift, 0.6f).W(AgonyEvent.Reach, 0.5f).W(AgonyEvent.Seizure, 0.4f)
                     .W(AgonyEvent.Shudder, 1.2f)
                     .Set(rigidity: 0.25f, breathing: 0.7f, lastBreath: 0.5f);

                case DeathMechanism.Burn:
                    return new MechanismProfile
                    {
                        Startle = true, ToneDelay = 0.1f,
                        ToneTau = new[] { V(0.3f, 0.7f), V(0.5f, 1.2f), V(0.5f, 1.2f), V(0.5f, 1.2f) },
                        BuckleProb = 0.9f, LockProb = 0.1f, DeathThrowProb = 0.4f,
                        AgonyStart = V(0.6f, 1.1f), AgonyI0 = V(1f, 1f), AgonyDuration = V(6f, 9f), EventRate = 2.2f,
                        TremorDeg = 7f, GaspProb = 0.7f, Writhing = 1.2f,
                    }.W(AgonyEvent.Curl, 3f).W(AgonyEvent.Roll, 3f).W(AgonyEvent.Flail, 2f).W(AgonyEvent.Pedal, 1f)
                     .W(AgonyEvent.Kick, 1f).W(AgonyEvent.Clonic, 0.5f).W(AgonyEvent.ArmThrust, 1.5f).W(AgonyEvent.LegJerk, 1.5f)
                     .W(AgonyEvent.Spasm, 1f)
                     .W(AgonyEvent.Shield, 2f).W(AgonyEvent.Rocking, 2.5f).W(AgonyEvent.GroundSlap, 2f).W(AgonyEvent.Crawl, 1.5f)
                     .W(AgonyEvent.HandsToFace, 2.2f).W(AgonyEvent.Reach, 1.2f).W(AgonyEvent.KneeRaise, 1f).W(AgonyEvent.Scissor, 0.5f)
                     .W(AgonyEvent.Seizure, 0.5f).W(AgonyEvent.PushUp, 1f)
                     .W(AgonyEvent.Shudder, 2f).W(AgonyEvent.ElbowDrag, 1.4f).W(AgonyEvent.ArmsOverhead, 1.2f).W(AgonyEvent.HeadBang, 1f)
                     .W(AgonyEvent.TwistArch, 1.2f)
                     .Set(rigidity: 0.05f, breathing: 0.95f, lastBreath: 0.7f);
            }
            return Build(DeathMechanism.Thoracic);
        }
    }
}
