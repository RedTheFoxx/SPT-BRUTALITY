using System;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace Brutality
{
    /// <summary>Détection des mods qui touchent aussi aux cadavres et conseils de configuration (journal BepInEx).</summary>
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
                Plugin.Log.LogInfo("Brutality : Smoothie détecté — son arrêt anticipé des cadavres est neutralisé pour les corps gérés par Brutality (aucun réglage requis).");
            if (Has(FikaGuid))
                Plugin.Log.LogInfo("Brutality : Fika détecté — l'hôte simule la mort, la pose finale est synchronisée par Fika.");
            if (Has(ReClassicGuid))
                Plugin.Log.LogInfo("Brutality : ReClassic détecté — les corps inconscients qui meurent sont repris par Brutality.");
            if (Has(RuptureGuid))
                Plugin.Log.LogWarning("Brutality : Rupture est installé. Les deux mods pilotent les ragdolls : désactivez Rupture pour éviter des conflits.");
        }

        /// <summary>Les autres plugins lient leur configuration tardivement : on vérifie au premier décès.</summary>
        public static void OnFirstDeath()
        {
            if (_firstDeathChecked) return;
            _firstDeathChecked = true;
            try
            {
                if (TryGetValue(SmoothieGuid, "3. Other fixes", "Corpses stop moving after (s)", out object v) && v is float f)
                {
                    if (f < Settings.MaxMotionSeconds.Value)
                        Plugin.Log.LogInfo($"Brutality : Smoothie fige les cadavres après {f:0} s ; Brutality prolonge ses propres corps jusqu'à {Settings.MaxMotionSeconds.Value:0} s maximum. " +
                                           "Optionnel : régler Smoothie « Corpses stop moving after (s) » sur 10 pour aligner les corps non gérés.");
                }
                if (TryGetValue(HollywoodFxGuid, "08. Ragdoll Effects (DISABLED BY VISCERAL COMBAT)", "Enable Ragdoll Effects (RESTART)", out object hv) && hv is bool hb && hb)
                {
                    Plugin.Log.LogWarning("Brutality : HollywoodFX « Enable Ragdoll Effects » est activé. Il réanime les cadavres figés quand on leur tire dessus " +
                                          "et ajoute des impulsions irréalistes. Recommandé : le désactiver (et « Enable Cinematic Ragdolls »), puis redémarrer le jeu.");
                }
            }
            catch (Exception e)
            {
                Plugin.LogDebug("Compat : " + e.Message);
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
