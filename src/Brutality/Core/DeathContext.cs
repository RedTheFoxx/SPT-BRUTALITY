using System;
using System.Collections.Generic;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>Dominant injury mechanism: determines the neuromuscular timeline of death.</summary>
    internal enum DeathMechanism
    {
        Cerebral,        // brain destruction: near-instantaneous loss of tone
        CervicalCord,    // cervical spinal cord transection: immediate flaccid quadriplegia
        NeckVascular,    // carotid / jugular / trachea: delayed loss of consciousness, intense agony
        Thoracic,        // heart / lungs / great vessels: collapse + anoxic myoclonus
        ThoracicCord,    // thoracic spinal cord injury: flaccid paraplegia + thorax
        Abdominal,       // abdomen / pelvis: guarding, flexion, crawling
        LumbarCord,      // lumbar spinal cord injury: flaccid lower limbs
        Limb,            // limb (hemorrhage): collapse on injured side
        Blast,           // explosion blast
        Syncope,         // hemorrhage, exhaustion, toxins: progressive syncope
        FallTrauma,      // fatal fall
        Burn,            // burns
        Melee,           // blunt or edged melee strike
    }

    internal sealed class DeathContext
    {
        public Player Player;
        public bool IsYourPlayer;
        public bool IsObserved;
        public float DeathTime;

        public EDamageType DamageType;
        public EBodyPart BodyPart;
        public EBodyPartColliderType ColliderType;
        public float Damage;
        public Vector3 HitPoint;
        public Vector3 Direction;
        public Collider HitCollider;

        public AmmoInfo Ammo;
        public float ImpactSpeed;
        public float ImpactEnergy;   // J
        public float ImpactMomentum; // N·s
        public float KickScale = 1f; // fatal-hit push variation, drawn within its ammo's range
        public string KickClass = "";

        public List<WoundRecord> LethalVolley = new List<WoundRecord>(); // wounds from the final moments (impulses)
        public List<WoundRecord> AllWounds = new List<WoundRecord>();
        public int VolleyCount => LethalVolley != null && LethalVolley.Count > 0 ? LethalVolley.Count : 1;

        public float[] PartHealth = new float[7];      // remaining health fraction per body part
        public bool[] PartDestroyed = new bool[7];

        public EPlayerPose Pose = EPlayerPose.Stand;
        public float PoseLevel = 1f;
        public Vector3 Velocity;

        public float GearThoraxKg;
        public float GearBackKg;
        public float GearHeadKg;
        public float BodyMassKg;

        public DeathMechanism Mechanism;
        public float Severity;  // 0.15..3, based on deposited energy
        public int WoundSide;   // -1 left, +1 right, 0 axial / unknown
        public System.Random Rng;

        private static readonly AccessTools.FieldRef<Player, DamageInfo> LastDamageInfoRef =
            AccessTools.FieldRefAccess<Player, DamageInfo>("LastDamageInfo");
        private static readonly AccessTools.FieldRef<Player, EBodyPart> LastBodyPartRef =
            AccessTools.FieldRefAccess<Player, EBodyPart>("LastBodyPart");

        public static DeathContext Build(Player player, bool observed)
        {
            var ctx = new DeathContext
            {
                Player = player,
                IsYourPlayer = player.IsYourPlayer,
                IsObserved = observed,
                DeathTime = Time.time,
            };
            int seed = unchecked(player.GetInstanceID() * 7919 + Environment.TickCount);
            ctx.Rng = new System.Random(seed);

            DamageInfo info = default;
            try { info = LastDamageInfoRef(player); } catch { }
            ctx.DamageType = player.LastDamageType != 0 ? player.LastDamageType : info.DamageType;
            try { ctx.BodyPart = LastBodyPartRef(player); } catch { ctx.BodyPart = player.LastDamagedBodyPart; }
            ctx.Damage = info.Damage;
            ctx.HitPoint = info.HitPoint;
            ctx.Direction = info.Direction.sqrMagnitude > 1e-6f ? info.Direction.normalized : Vector3.zero;
            ctx.HitCollider = info.HitCollider;
            ctx.ColliderType = info.BodyPartColliderType;

            // Wound history.
            var ledger = WoundRegistry.Get(player);
            if (ledger != null)
            {
                ctx.AllWounds.AddRange(ledger.Wounds);
                foreach (var w in ledger.Wounds)
                    if (ctx.DeathTime - w.Time <= 0.2f) ctx.LethalVolley.Add(w);
            }
            WoundRecord last = ctx.AllWounds.Count > 0 ? ctx.AllWounds[ctx.AllWounds.Count - 1] : null;
            if (last != null && ctx.DeathTime - last.Time < 0.2f && last.ColliderType != EBodyPartColliderType.None)
            {
                // The wound recorded at the moment of death carries the actual hit collider.
                ctx.ColliderType = last.ColliderType;
                ctx.BodyPart = last.BodyPart;
            }
            if (last != null && ctx.DeathTime - last.Time < 1f)
            {
                if (ctx.ColliderType == EBodyPartColliderType.None) ctx.ColliderType = last.ColliderType;
                if (ctx.HitPoint.sqrMagnitude < 1e-6f) ctx.HitPoint = last.HitPointWorld;
                if (ctx.Direction.sqrMagnitude < 1e-6f && last.Direction.sqrMagnitude > 1e-6f) ctx.Direction = last.Direction.normalized;
                if (ctx.HitCollider == null) ctx.HitCollider = last.HitCollider;
            }
            if (ctx.ColliderType == EBodyPartColliderType.None && ctx.HitCollider != null)
            {
                var bpc = ctx.HitCollider.GetComponent<BodyPartCollider>();
                if (bpc != null) ctx.ColliderType = bpc.BodyPartColliderType;
            }
            if (ctx.LethalVolley.Count == 0 && ctx.HitPoint.sqrMagnitude > 1e-6f)
            {
                var rec = new WoundRecord
                {
                    Time = ctx.DeathTime, BodyPart = ctx.BodyPart, ColliderType = ctx.ColliderType, DamageType = ctx.DamageType,
                    Damage = ctx.Damage, Penetration = info.PenetrationPower, HitPointWorld = ctx.HitPoint, Direction = ctx.Direction,
                    MasterOrigin = info.MasterOrigin, AmmoId = info.SourceId, HitCollider = ctx.HitCollider,
                    Bone = WoundRegistry.FindSegmentBone(ctx.HitCollider != null ? ctx.HitCollider.transform : null),
                };
                if (rec.Bone != null) rec.LocalPoint = rec.Bone.InverseTransformPoint(rec.HitPointWorld);
                ctx.LethalVolley.Add(rec);
                ctx.AllWounds.Add(rec);
            }

            // Ballistics.
            ctx.Ammo = AmmoDatabase.Get(info.SourceId ?? last?.AmmoId);
            if (ctx.Ammo != null)
            {
                float dist = info.MasterOrigin.sqrMagnitude > 1e-6f && ctx.HitPoint.sqrMagnitude > 1e-6f
                    ? Vector3.Distance(info.MasterOrigin, ctx.HitPoint) : 30f;
                ctx.ImpactSpeed = ctx.Ammo.ImpactSpeed(dist);
                ctx.ImpactEnergy = ctx.Ammo.EnergyAt(ctx.ImpactSpeed);
                ctx.ImpactMomentum = ctx.Ammo.MomentumAt(ctx.ImpactSpeed);
            }
            else
            {
                // No template (melee, shrapnel, environment): estimate from damage.
                ctx.ImpactEnergy = Mathf.Clamp(ctx.Damage * 18f, 50f, 4000f);
                ctx.ImpactMomentum = Mathf.Clamp(ctx.Damage * 0.04f, 0.5f, 10f);
            }
            ctx.Severity = Mathf.Clamp(ctx.ImpactEnergy / 1800f, 0.15f, 3f);
            var kick = AmmoKick.For(ctx.Ammo, last != null ? last.Penetration : info.PenetrationPower);
            ctx.KickScale = kick.Draw(ctx.Rng);
            ctx.KickClass = kick.Class;

            // Health state per body part (destroyed limbs = flaccid).
            try
            {
                var hc = player.HealthController;
                for (int i = 0; i < 7; i++)
                {
                    var v = hc.GetBodyPartHealth((EBodyPart)i);
                    float frac = v.Maximum > 0f ? Mathf.Clamp01(v.Current / v.Maximum) : 1f;
                    ctx.PartHealth[i] = frac;
                    ctx.PartDestroyed[i] = v.Maximum > 0f && v.Current <= 0.01f;
                }
            }
            catch
            {
                for (int i = 0; i < 7; i++) ctx.PartHealth[i] = 1f;
            }
            // Safeguard: some health controllers (observed players) reset all parts to zero
            // on death. Such a reading is unusable: treat limbs as intact instead.
            int destroyed = 0;
            for (int i = 0; i < 7; i++) if (ctx.PartDestroyed[i]) destroyed++;
            if (destroyed >= 5)
            {
                for (int i = 0; i < 7; i++)
                {
                    ctx.PartDestroyed[i] = false;
                    ctx.PartHealth[i] = 1f;
                }
            }

            try
            {
                ctx.Pose = player.Pose;
                ctx.PoseLevel = player.PoseLevel;
                ctx.Velocity = player.Velocity;
            }
            catch { }

            ReadEquipment(ctx, player);

            float mean = Settings.BodyMassKg.Value;
            ctx.BodyMassKg = Mathf.Clamp(BMath.Gaussian(ctx.Rng, mean, mean * Settings.BodyMassVariation.Value), mean * 0.7f, mean * 1.35f);

            // Head collider inconsistent with hit body part (DamageInfo from a previous shot): ignored.
            if (IsHeadCollider(ctx.ColliderType) && ctx.BodyPart != EBodyPart.Head && ctx.BodyPart != EBodyPart.Common)
                ctx.ColliderType = EBodyPartColliderType.None;
            ctx.Mechanism = Classify(ctx);
            ctx.WoundSide = SideOf(ctx.ColliderType, ctx.BodyPart);
            return ctx;
        }

        private static void ReadEquipment(DeathContext ctx, Player player)
        {
            if (!Settings.IncludeEquipmentMass.Value) return;
            try
            {
                var eq = player.Equipment;
                if (eq == null) return;
                ctx.GearThoraxKg = SlotWeight(eq, EquipmentSlot.ArmorVest) + SlotWeight(eq, EquipmentSlot.TacticalVest);
                ctx.GearBackKg = SlotWeight(eq, EquipmentSlot.Backpack);
                ctx.GearHeadKg = SlotWeight(eq, EquipmentSlot.Headwear) + SlotWeight(eq, EquipmentSlot.Earpiece)
                                 + SlotWeight(eq, EquipmentSlot.FaceCover) + SlotWeight(eq, EquipmentSlot.Eyewear);
                ctx.GearThoraxKg = Mathf.Clamp(ctx.GearThoraxKg, 0f, 22f);
                ctx.GearBackKg = Mathf.Clamp(ctx.GearBackKg, 0f, 25f);
                ctx.GearHeadKg = Mathf.Clamp(ctx.GearHeadKg, 0f, 4f);
            }
            catch (Exception e)
            {
                Plugin.LogDebug("Unreadable equipment: " + e.Message);
            }
        }

        private static float SlotWeight(InventoryEquipment eq, EquipmentSlot slot)
        {
            var item = eq.GetSlot(slot)?.ContainedItem;
            if (item == null) return 0f;
            float w = 0f;
            foreach (var it in item.GetAllItems())
                w += it.Weight * Mathf.Max(1, it.StackObjectsCount);
            return float.IsNaN(w) ? 0f : w;
        }

        private static bool Has(EDamageType t, EDamageType flag) => (t & flag) != 0;

        public static DeathMechanism Classify(DeathContext ctx)
        {
            var t = ctx.DamageType;
            if (Has(t, EDamageType.Explosion | EDamageType.Landmine | EDamageType.Artillery | EDamageType.ThermobaricExplosion))
                return DeathMechanism.Blast;
            if (Has(t, EDamageType.GrenadeFragment) && ctx.Damage > 70f)
                return DeathMechanism.Blast;
            if (Has(t, EDamageType.Fall))
                return DeathMechanism.FallTrauma;
            if (Has(t, EDamageType.Flame | EDamageType.HotGases))
                return DeathMechanism.Burn;
            if (Has(t, EDamageType.Melee))
                return DeathMechanism.Melee;
            if (Has(t, EDamageType.LightBleeding | EDamageType.HeavyBleeding | EDamageType.Dehydration | EDamageType.Exhaustion
                       | EDamageType.RadExposure | EDamageType.Poison | EDamageType.LethalToxin | EDamageType.Stimulator
                       | EDamageType.Medicine | EDamageType.Existence | EDamageType.Environment))
                return DeathMechanism.Syncope;

            switch (ctx.ColliderType)
            {
                case EBodyPartColliderType.HeadCommon:
                case EBodyPartColliderType.ParietalHead:
                case EBodyPartColliderType.BackHead:
                case EBodyPartColliderType.Eyes:
                case EBodyPartColliderType.Ears:
                case EBodyPartColliderType.Jaw:
                    return DeathMechanism.Cerebral;
                case EBodyPartColliderType.NeckBack:
                    return DeathMechanism.CervicalCord;
                case EBodyPartColliderType.NeckFront:
                    return DeathMechanism.NeckVascular;
                case EBodyPartColliderType.SpineTop:
                    return DeathMechanism.ThoracicCord;
                case EBodyPartColliderType.SpineDown:
                    return DeathMechanism.LumbarCord;
                case EBodyPartColliderType.RibcageUp:
                case EBodyPartColliderType.LeftSideChestUp:
                case EBodyPartColliderType.RightSideChestUp:
                    return DeathMechanism.Thoracic;
                case EBodyPartColliderType.RibcageLow:
                case EBodyPartColliderType.LeftSideChestDown:
                case EBodyPartColliderType.RightSideChestDown:
                case EBodyPartColliderType.Pelvis:
                case EBodyPartColliderType.PelvisBack:
                    return DeathMechanism.Abdominal;
                case EBodyPartColliderType.LeftUpperArm:
                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.RightUpperArm:
                case EBodyPartColliderType.RightForearm:
                case EBodyPartColliderType.LeftThigh:
                case EBodyPartColliderType.LeftCalf:
                case EBodyPartColliderType.RightThigh:
                case EBodyPartColliderType.RightCalf:
                    return DeathMechanism.Limb;
            }
            switch (ctx.BodyPart)
            {
                case EBodyPart.Head: return DeathMechanism.Cerebral;
                case EBodyPart.Chest: return DeathMechanism.Thoracic;
                case EBodyPart.Stomach: return DeathMechanism.Abdominal;
                case EBodyPart.LeftArm:
                case EBodyPart.RightArm:
                case EBodyPart.LeftLeg:
                case EBodyPart.RightLeg: return DeathMechanism.Limb;
            }
            return DeathMechanism.Thoracic;
        }

        private static bool IsHeadCollider(EBodyPartColliderType c)
        {
            switch (c)
            {
                case EBodyPartColliderType.HeadCommon:
                case EBodyPartColliderType.ParietalHead:
                case EBodyPartColliderType.BackHead:
                case EBodyPartColliderType.Eyes:
                case EBodyPartColliderType.Ears:
                case EBodyPartColliderType.Jaw:
                    return true;
            }
            return false;
        }

        public static int SideOf(EBodyPartColliderType c, EBodyPart part)
        {
            switch (c)
            {
                case EBodyPartColliderType.LeftUpperArm:
                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.LeftThigh:
                case EBodyPartColliderType.LeftCalf:
                case EBodyPartColliderType.LeftSideChestUp:
                case EBodyPartColliderType.LeftSideChestDown:
                    return -1;
                case EBodyPartColliderType.RightUpperArm:
                case EBodyPartColliderType.RightForearm:
                case EBodyPartColliderType.RightThigh:
                case EBodyPartColliderType.RightCalf:
                case EBodyPartColliderType.RightSideChestUp:
                case EBodyPartColliderType.RightSideChestDown:
                    return 1;
            }
            switch (part)
            {
                case EBodyPart.LeftArm:
                case EBodyPart.LeftLeg: return -1;
                case EBodyPart.RightArm:
                case EBodyPart.RightLeg: return 1;
            }
            return 0;
        }

        public bool LegDestroyed(int side) => PartDestroyed[(int)(side < 0 ? EBodyPart.LeftLeg : EBodyPart.RightLeg)];
        public bool ArmDestroyed(int side) => PartDestroyed[(int)(side < 0 ? EBodyPart.LeftArm : EBodyPart.RightArm)];
        public float LegHealth(int side) => PartHealth[(int)(side < 0 ? EBodyPart.LeftLeg : EBodyPart.RightLeg)];
        public float ArmHealth(int side) => PartHealth[(int)(side < 0 ? EBodyPart.LeftArm : EBodyPart.RightArm)];
    }
}
