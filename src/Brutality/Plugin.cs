using BepInEx;
using BepInEx.Logging;
using Brutality.Patches;
using HarmonyLib;

namespace Brutality
{
    /// <summary>
    /// Brutality — biomechanical replacement for SPT's ragdoll system.
    /// Death is simulated at three levels: passive tissues (masses, inertias, exponential limits),
    /// muscles (torque-angle-velocity, activation), and nervous system (tone extinction, reflexes, agony).
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
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        private Harmony _harmony;
        private static bool _probeErrorLogged;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{Name} {Version}: initializing…");
            try
            {
                Settings.Bind(Config);
            }
            catch (System.Exception e)
            {
                // Without valid configuration, no patches are installed: the game keeps vanilla ragdolls.
                Log.LogError($"{Name}: invalid configuration, plugin disabled. {e}");
                return;
            }
            try
            {
                Core.DriveController.ApplyConventionSetting();
                Settings.DriveConvention.SettingChanged += (_, __) => Core.DriveController.ApplyConventionSetting();
                _harmony = new Harmony(Guid);
                BrutalityPatches.Apply(_harmony);
                Compat.LogStartup();
                Log.LogInfo($"{Name} {Version} loaded.");
            }
            catch (System.Exception e)
            {
                Log.LogError($"{Name}: initialization failed. {e}");
            }
        }

        private void Update()
        {
            // The probe is diagnostic only: an exception must never break the game loop,
            // but it is reported once instead of being silently swallowed.
            try { Core.PhysxProbe.Tick(); }
            catch (System.Exception e)
            {
                if (_probeErrorLogged) return;
                _probeErrorLogged = true;
                Log?.LogWarning($"{Name}: PhysX probe interrupted ({e.Message}); default conventions applied.");
            }
        }

        internal static void LogDebug(string message)
        {
            if (Settings.DebugLogging != null && Settings.DebugLogging.Value) Log?.LogInfo(message);
        }
    }
}
