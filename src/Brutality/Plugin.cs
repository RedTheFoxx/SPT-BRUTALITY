using BepInEx;
using BepInEx.Logging;
using Brutality.Patches;
using HarmonyLib;

namespace Brutality
{
    /// <summary>
    /// Brutality — remplacement biomécanique du système de ragdolls de SPT.
    /// La mort est simulée à trois niveaux : tissus passifs (masses, inerties, butées exponentielles),
    /// muscles (couple-angle-vitesse, activation) et système nerveux (extinction du tonus, réflexes, agonie).
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInProcess("EscapeFromTarkov.exe")]
    [BepInDependency("com.SPT.core", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.SmoothieGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.FikaGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.HollywoodFxGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.ReClassicGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.brutality.ragdoll";
        public const string Name = "Brutality";
        public const string Version = "0.3.0";

        internal static ManualLogSource Log;
        private Harmony _harmony;
        private static bool _probeErrorLogged;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{Name} {Version} : initialisation…");
            try
            {
                Settings.Bind(Config);
            }
            catch (System.Exception e)
            {
                // Sans configuration, aucun patch n'est installé : le jeu garde ses ragdolls vanilla.
                Log.LogError($"{Name} : configuration invalide, plugin désactivé. {e}");
                return;
            }
            try
            {
                Core.DriveController.ApplyConventionSetting();
                Settings.DriveConvention.SettingChanged += (_, __) => Core.DriveController.ApplyConventionSetting();
                _harmony = new Harmony(Guid);
                BrutalityPatches.Apply(_harmony);
                Compat.LogStartup();
                Log.LogInfo($"{Name} {Version} chargé.");
            }
            catch (System.Exception e)
            {
                Log.LogError($"{Name} : échec de l'initialisation. {e}");
            }
        }

        private void Update()
        {
            // La sonde est un diagnostic : une exception ne doit jamais interrompre la boucle du jeu, mais
            // elle est signalée une fois (au lieu d'être avalée silencieusement).
            try { Core.PhysxProbe.Tick(); }
            catch (System.Exception e)
            {
                if (_probeErrorLogged) return;
                _probeErrorLogged = true;
                Log?.LogWarning($"{Name} : sonde PhysX interrompue ({e.Message}) ; conventions par défaut appliquées.");
            }
        }

        internal static void LogDebug(string message)
        {
            if (Settings.DebugLogging != null && Settings.DebugLogging.Value) Log?.LogInfo(message);
        }
    }
}
