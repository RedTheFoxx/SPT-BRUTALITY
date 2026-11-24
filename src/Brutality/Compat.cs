using System;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace Brutality
{
    /// <summary>Detects other mods that also touch corpses and logs configuration advice (BepInEx log).</summary>
    internal static class Compat
    {
        public const string SmoothieGuid = "com.tominocz.smoothie";
        public const string FikaGuid = "com.fika.core";
        public const string HollywoodFxGuid = "com.janky.hollywoodfx";
        public const string ReClassicGuid = "com.mizmii.reclassic";
        public const string RuptureGuid = "com.rootdarkarchon.rupture";

        private static bool _firstDeathChecked;

        public static bool Has(string guid) => Chainloader.PluginInfos.ContainsKey(guid);

        public static void LogStartup()
        {
            if (Has(SmoothieGuid))
                Plugin.Log.LogInfo("Brutality: Smoothie detected — its early corpse freeze is neutralized for Brutality-managed bodies (no setting change required).");
            if (Has(FikaGuid))
                Plugin.Log.LogInfo("Brutality: Fika detected — host simulates death, final pose is synchronized by Fika.");
            if (Has(ReClassicGuid))
                Plugin.Log.LogInfo("Brutality: ReClassic detected — unconscious bodies that die are taken over by Brutality.");
            if (Has(RuptureGuid))
                Plugin.Log.LogWarning("Brutality: Rupture is installed. Both mods drive ragdolls: disable Rupture to avoid conflicts.");
        }

        /// <summary>Other plugins bind their config lazily: check on the first death.</summary>
        public static void OnFirstDeath()
        {
            if (_firstDeathChecked) return;
            _firstDeathChecked = true;
            try
            {
                if (TryGetValue(SmoothieGuid, "3. Other fixes", "Corpses stop moving after (s)", out object v) && v is float f)
                {
                    if (f < Settings.MaxMotionSeconds.Value)
                        Plugin.Log.LogInfo($"Brutality: Smoothie freezes corpses after {f:0} s; Brutality extends its own bodies up to {Settings.MaxMotionSeconds.Value:0} s max. " +
                                           "Optional: set Smoothie 'Corpses stop moving after (s)' to 10 to align unmanaged bodies.");
                }
                if (TryGetValue(HollywoodFxGuid, "08. Ragdoll Effects (DISABLED BY VISCERAL COMBAT)", "Enable Ragdoll Effects (RESTART)", out object hv) && hv is bool hb && hb)
                {
                    Plugin.Log.LogWarning("Brutality: HollywoodFX 'Enable Ragdoll Effects' is enabled. It reanimates frozen corpses when shot " +
                                          "and adds unrealistic impulses. Recommended: disable it (and 'Enable Cinematic Ragdolls'), then restart the game.");
                }
            }
            catch (Exception e)
            {
                Plugin.LogDebug("Compat: " + e.Message);
            }
        }

        private static bool TryGetValue(string guid, string section, string key, out object value)
        {
            value = null;
            if (!Chainloader.PluginInfos.TryGetValue(guid, out var info) || info?.Instance == null) return false;
            var cfg = info.Instance.Config;
            var def = new ConfigDefinition(section, key);
            if (!cfg.ContainsKey(def)) return false;
            value = cfg[def].BoxedValue;
            return true;
        }
    }
}
