using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>Une blessure enregistrée au moment où le joueur la reçoit.</summary>
    internal sealed class WoundRecord
    {
        public float Time;
        public int Frame;
        public EBodyPart BodyPart;
        public EBodyPartColliderType ColliderType;
        public EDamageType DamageType;
        public float Damage;
        public float Penetration;
        public Vector3 HitPointWorld;
        public Vector3 Direction;
        public Vector3 MasterOrigin;
        public Transform Bone;        // os portant un RigidbodySpawner (segment du ragdoll)
        public Vector3 LocalPoint;    // point d'impact dans le repère de l'os
        public string AmmoId;
        public Collider HitCollider;

        public Vector3 CurrentWorldPoint => Bone != null ? Bone.TransformPoint(LocalPoint) : HitPointWorld;
    }

    internal sealed class WoundLedger
    {
        public readonly List<WoundRecord> Wounds = new List<WoundRecord>(8);
    }

    /// <summary>Historique des blessures par joueur (alimenté par les patchs ApplyDamageInfo).</summary>
    internal static class WoundRegistry
    {
        private static readonly ConditionalWeakTable<Player, WoundLedger> Table = new ConditionalWeakTable<Player, WoundLedger>();
        private const float KeepSeconds = 45f;

        public static WoundLedger Get(Player p) => p == null ? null : Table.GetOrCreateValue(p);

        public static void Record(Player player, DamageInfo info, EBodyPart part, EBodyPartColliderType colliderType)
        {
            if (player == null) return;
            var ledger = Get(player);
            float now = UnityEngine.Time.time;
            int frame = UnityEngine.Time.frameCount;
            var list = ledger.Wounds;

            // Dédoublonnage : une surcharge qui appelle base.ApplyDamageInfo déclenche deux postfix.
            for (int i = list.Count - 1; i >= 0 && list[i].Frame == frame; i--)
            {
                var w = list[i];
                if (w.BodyPart == part && Mathf.Abs(w.Damage - info.Damage) < 1e-3f && (w.HitPointWorld - info.HitPoint).sqrMagnitude < 1e-6f)
                    return;
            }

            // Purge des blessures anciennes.
            int remove = 0;
            while (remove < list.Count && now - list[remove].Time > KeepSeconds) remove++;
            if (remove > 0) list.RemoveRange(0, remove);
            if (list.Count > 64) list.RemoveRange(0, list.Count - 64);

            var rec = new WoundRecord
            {
                Time = now,
                Frame = frame,
                BodyPart = part,
                ColliderType = colliderType != EBodyPartColliderType.None ? colliderType : info.BodyPartColliderType,
                DamageType = info.DamageType,
                Damage = info.Damage,
                Penetration = info.PenetrationPower,
                HitPointWorld = info.HitPoint,
                Direction = info.Direction,
                MasterOrigin = info.MasterOrigin,
                AmmoId = info.SourceId,
                HitCollider = info.HitCollider,
            };
            if (rec.ColliderType == EBodyPartColliderType.None && info.HitCollider != null)
            {
                var bpc = info.HitCollider.GetComponent<BodyPartCollider>();
                if (bpc != null) rec.ColliderType = bpc.BodyPartColliderType;
            }
            rec.Bone = FindSegmentBone(info.HitCollider != null ? info.HitCollider.transform : null);
            if (rec.Bone != null && info.HitPoint.sqrMagnitude > 1e-6f)
                rec.LocalPoint = rec.Bone.InverseTransformPoint(info.HitPoint);
            list.Add(rec);
        }

        /// <summary>Remonte la hiérarchie jusqu'à l'os qui porte un RigidbodySpawner.</summary>
        public static Transform FindSegmentBone(Transform t)
        {
            int guard = 0;
            while (t != null && guard++ < 12)
            {
                if (t.GetComponent<RigidbodySpawner>() != null) return t;
                t = t.parent;
            }
            return null;
        }
    }

    /// <summary>Données balistiques d'une munition (gabarit EFT).</summary>
    internal sealed class AmmoInfo
    {
        public string Id;
        public string Caliber;
        public float BulletMassKg;
        public float InitialSpeed;
        public int ProjectileCount;
        public float DiameterMm;
        public bool IsBuckshot;
        public bool IsGrenade;

        public float MomentumAt(float speed) => BulletMassKg * speed;
        public float EnergyAt(float speed) => 0.5f * BulletMassKg * speed * speed;

        /// <summary>Vitesse d'impact approchée (perte de vitesse quasi linéaire aux distances de combat).</summary>
        public float ImpactSpeed(float distance)
        {
            float loss = IsBuckshot ? 0.0028f : (BulletMassKg < 0.006f ? 0.0013f : 0.0009f);
            return InitialSpeed * Mathf.Clamp(1f - loss * Mathf.Max(0f, distance), 0.35f, 1f);
        }
    }

    internal static class AmmoDatabase
    {
        private static readonly Dictionary<string, AmmoInfo> Cache = new Dictionary<string, AmmoInfo>();

        public static AmmoInfo Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (Cache.TryGetValue(id, out var cached)) return cached;
            AmmoInfo info = null;
            bool resolvable = false;
            try
            {
                if (Singleton<ItemFactory>.Instantiated)
                {
                    var templates = Singleton<ItemFactory>.Instance.ItemTemplates;
                    resolvable = templates != null;
                    if (resolvable && templates.TryGetValue(id, out var template) && template is AmmoTemplate ammo)
                    {
                        info = new AmmoInfo
                        {
                            Id = id,
                            Caliber = ammo.Caliber,
                            BulletMassKg = Mathf.Max(0.0005f, ammo.BulletMassGram / 1000f),
                            InitialSpeed = Mathf.Max(50f, ammo.InitialSpeed),
                            ProjectileCount = Mathf.Max(1, ammo.ProjectileCount),
                            DiameterMm = ammo.BulletDiameterMilimeters,
                            IsBuckshot = ammo.ProjectileCount > 1 || (ammo.ammoType != null && ammo.ammoType.ToLowerInvariant().Contains("buckshot")),
                            IsGrenade = ammo.HasGrenaderComponent,
                        };
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.LogDebug("AmmoDatabase: " + e.Message);
            }
            // Un résultat lu avant le chargement des gabarits d'objets n'est pas mémorisé : sinon l'absence
            // resterait définitive pour cette munition pendant toute la session.
            if (resolvable) Cache[id] = info;
            return info;
        }
    }
}
