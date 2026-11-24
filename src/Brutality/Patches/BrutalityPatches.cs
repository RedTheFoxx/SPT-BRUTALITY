using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Brutality.Core;
using EFT;
using EFT.AssetsManager;
using EFT.Ballistics;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Brutality.Patches
{
    /// <summary>
    /// Game hook points (Harmony):
    ///  1. UnityEngine.Physics.Simulate  — prefix: Brutality runs before each PhysX step (EFT simulates in Script mode,
    ///     Smoothie caps the rate at 60 Hz: torques remain consistent regardless of step size);
    ///  2. Player.OnDead                 — postfix: takes over the newly created corpse;
    ///  3. *.ApplyDamageInfo             — prefix: wound history (all overloads, including Fika);
    ///  4. PlayerRigidbodySleepHierarchy.TryPutToSleep — prefix: bodies managed by Brutality are not force-slept
    ///     by EFT (which would call Sleep() every frame): PhysX natural sleep is sufficient;
    ///  5. GameWorld.OnGameStarted       — postfix: wraps BallisticsCalculator's shot delegate (like HollywoodFX)
    ///     to forward bullets hitting simulated corpses.
    /// </summary>
    internal static class BrutalityPatches
    {
        private static readonly AccessTools.FieldRef<Player, Corpse> CorpseRef = AccessTools.FieldRefAccess<Player, Corpse>("Corpse");

        public static void Apply(Harmony h)
        {
            Try("Physics.Simulate", () =>
            {
                var m = AccessTools.Method(typeof(Physics), nameof(Physics.Simulate), new[] { typeof(float) });
                h.Patch(m, prefix: new HarmonyMethod(typeof(BrutalityPatches), nameof(SimulatePrefix)) { priority = Priority.First });
                PhysicsClock.PatchInstalled = true;
            });

            Try("Player.OnDead", () =>
            {
                var m = AccessTools.Method(typeof(Player), nameof(Player.OnDead), new[] { typeof(EDamageType) });
                h.Patch(m, postfix: new HarmonyMethod(typeof(BrutalityPatches), nameof(OnDeadPostfix)) { priority = Priority.Low });
            });

            Try("ApplyDamageInfo", () =>
            {
                int n = 0;
                // Prefix: death (Player.OnDead) is triggered *during* ApplyDamageInfo; the fatal wound
                // must therefore be recorded before the original call.
                var pre = new HarmonyMethod(typeof(BrutalityPatches), nameof(DamagePrefix)) { priority = Priority.First };
                foreach (var m in FindDamageMethods())
                {
                    try
                    {
                        h.Patch(m, prefix: pre);
                        n++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"Brutality: unable to patch {m.DeclaringType?.FullName}.ApplyDamageInfo: {e.Message}");
                    }
                }
                Plugin.Log.LogInfo($"Brutality: wound tracking on {n} ApplyDamageInfo implementation(s).");
            });

            Try("GameWorld.OnGameStarted", () =>
            {
                var m = AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));
                h.Patch(m, postfix: new HarmonyMethod(typeof(BrutalityPatches), nameof(GameStartedPostfix)) { priority = Priority.Low });
            });

            // End of raid: release components (static lists, delegates, joints) instead of letting them
            // accumulate from raid to raid.
            Try("GameWorld.OnDestroy", () =>
            {
                var m = AccessTools.DeclaredMethod(typeof(GameWorld), "OnDestroy");
                if (m == null || m.DeclaringType != typeof(GameWorld))
                {
                    Plugin.LogDebug("Brutality: GameWorld.OnDestroy not declared; cleanup will run on next raid.");
                    return;
                }
                h.Patch(m, postfix: new HarmonyMethod(typeof(BrutalityPatches), nameof(WorldDestroyedPostfix)) { priority = Priority.Low });
            });

            Try("TryPutToSleep", () =>
            {
                var m = AccessTools.Method(typeof(PlayerRigidbodySleepHierarchy), nameof(PlayerRigidbodySleepHierarchy.TryPutToSleep));
                h.Patch(m, prefix: new HarmonyMethod(typeof(BrutalityPatches), nameof(SleepPrefix)) { priority = Priority.First });
            });
        }

        private static void Try(string name, Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Brutality: patch {name} failed: {e}");
            }
        }

        private static IEnumerable<MethodBase> FindDamageMethods()
        {
            var sig = new[] { typeof(DamageInfo), typeof(EBodyPart), typeof(EBodyPartColliderType), typeof(float) };
            var found = new HashSet<MethodBase>();
            var baseMethod = AccessTools.Method(typeof(Player), nameof(Player.ApplyDamageInfo), sig);
            if (baseMethod != null) found.Add(baseMethod);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                string an = asm.GetName().Name;
                if (an.StartsWith("System") || an.StartsWith("Unity") || an.StartsWith("mscorlib") || an.StartsWith("Mono.")
                    || an.StartsWith("Microsoft") || an.StartsWith("netstandard") || an.StartsWith("Sirenix") || an.StartsWith("BepInEx")
                    || an.StartsWith("0Harmony") || an.StartsWith("MonoMod") || an.StartsWith("Newtonsoft")) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var t in types)
                {
                    try
                    {
                        if (t == null || t.ContainsGenericParameters || !t.IsSubclassOf(typeof(Player))) continue;
                        var m = t.GetMethod(nameof(Player.ApplyDamageInfo),
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, sig, null);
                        if (m != null && !m.IsAbstract) found.Add(m);
                    }
                    catch { }
                }
            }
            return found;
        }

        // ------------------------------------------------------------------ patches

        private static void SimulatePrefix(float step)
        {
            PhysicsClock.PreStep(step);
        }

        private static void OnDeadPostfix(Player __instance)
        {
            if (!Settings.Enabled.Value || __instance == null) return;
            try
            {
                var corpse = CorpseRef(__instance);
                if (corpse == null || !corpse.HasRagdoll) return;
                var ragdoll = corpse.Ragdoll;
                if (ragdoll == null || !ragdoll._putToSleep) return;
                bool observed = corpse is ObservedCorpse;
                if (observed && !Settings.AffectObservedCorpses.Value) return;
                if (__instance.IsYourPlayer && !Settings.AffectOwnCorpse.Value) return;
                if (corpse.GetComponent<BrutalityRagdoll>() != null) return;

                Compat.OnFirstDeath();
                var comp = corpse.gameObject.AddComponent<BrutalityRagdoll>();
                if (!comp.Initialize(__instance, corpse, ragdoll, observed))
                    UnityEngine.Object.Destroy(comp);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Brutality: unable to take over death: " + e);
            }
        }

        private static void DamagePrefix(Player __instance, DamageInfo __0, EBodyPart __1, EBodyPartColliderType __2)
        {
            try
            {
                if (__instance == null || __instance.HealthController == null || !__instance.HealthController.IsAlive) return;
                WoundRegistry.Record(__instance, __0, __1, __2);
            }
            catch (Exception e)
            {
                Plugin.LogDebug("wound recording: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ bullets on simulated bodies

        private static readonly AccessTools.FieldRef<BallisticsCalculator, ShotDelegate> ShotDelegateRef =
            AccessTools.FieldRefAccess<BallisticsCalculator, ShotDelegate>("_shotDelegate");
        private static ShotDelegate _originalShot;
        private static readonly System.Random ShotRng = new System.Random();

        private static void GameStartedPostfix(GameWorld __instance)
        {
            try
            {
                // Any leftovers from a raid whose GameWorld did not trigger OnDestroy.
                BrutalityRagdoll.ClearAll();
                var calc = __instance != null ? __instance.GetComponent<BallisticsCalculator>() : null;
                if (calc == null) return;
                var current = ShotDelegateRef(calc);
                if (current == null) return;
                // Never wrap twice: the delegate would call itself in a loop.
                if (current == (ShotDelegate)OnShot) return;
                _originalShot = current;
                ShotDelegateRef(calc) = OnShot;
                Plugin.LogDebug("Brutality: shot delegate wrapped (bullets on simulated bodies).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Brutality: unable to intercept shots on corpses: " + e.Message);
            }
        }

        private static void WorldDestroyedPostfix()
        {
            try { BrutalityRagdoll.ClearAll(); }
            catch (Exception e) { Plugin.LogDebug("end-of-raid cleanup: " + e.Message); }
        }

        private static void OnShot(Shot shot)
        {
            try { ForwardShot(shot); }
            catch (Exception e) { Plugin.LogDebug("shot on corpse: " + e.Message); }
            _originalShot?.Invoke(shot);
        }

        /// <summary>Transferred momentum (p = m·v, variation specific to ammo class) then forwarded to the struck body.</summary>
        private static void ForwardShot(Shot shot)
        {
            if (shot == null || !Settings.Enabled.Value) return;
            var col = shot.HitCollider;
            if (col == null) return;
            var rb = col.attachedRigidbody;
            var owner = BrutalityRagdoll.OwnerOf(rb);
            if (owner == null) return;
            AmmoInfo ammo = null;
            try { if (shot.Ammo != null) ammo = AmmoDatabase.Get(shot.Ammo.TemplateId.ToString()); } catch { }
            float mass = ammo != null ? ammo.BulletMassKg : Mathf.Max(0.003f, shot.BulletMassGram / 1000f);
            float speed = Mathf.Max(shot.VelocityMagnitude, shot.Speed);
            if (ammo != null) speed = Mathf.Min(speed, ammo.InitialSpeed);
            float p = mass * speed;
            Vector3 dir = shot.CurrentVelocity.sqrMagnitude > 1f ? shot.CurrentVelocity.normalized : shot.Direction;
            var kick = AmmoKick.For(ammo, shot.PenetrationPower);
            p = Mathf.Min(60f, p * kick.Draw(ShotRng) * Settings.BulletMomentumMultiplier.Value);
            dir = kick.Scatter(dir, ShotRng);
            Vector3 point = shot.HitPoint + kick.LeverOffset(dir, ShotRng) * 0.5f;
            owner.OnBullet(rb, point, dir, p);
        }

        private static bool SleepPrefix(PlayerRigidbodySleepHierarchy __instance, ref bool __result)
        {
            var rb = __instance?.RigidbodySpawner != null ? __instance.RigidbodySpawner.Rigidbody : null;
            var owner = BrutalityRagdoll.OwnerOf(rb);
            if (owner != null && owner.KeepAwake)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
