using Brutality.Anatomy;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>
    /// Real mechanical impulses at the moment of death.
    /// Bullet: projectile momentum transfer p = m·v (physical bound: a bullet does not "launch"
    /// a body; Gruber et al. 1998 also note that a rigid-body model already overestimates impact forces).
    /// Blast: impulse distributed across segments according to their mass and distance from the impact point.
    /// </summary>
    internal static class Impacts
    {
        private const float MaxVolleyImpulse = 60f; // N·s

        /// <param name="sink">If provided, receives each impulse (segment, point, direction, N·s, lethal hit?) instead of
        /// applying it directly: the Euphoria layer spreads it over time and adds the character's reaction.</param>
        public static void Apply(BodyModel body, DeathContext ctx, System.Action<Segment, Vector3, Vector3, float, bool> sink = null)
        {
            switch (ctx.Mechanism)
            {
                case DeathMechanism.FallTrauma:
                    return;
                case DeathMechanism.Blast:
                    ApplyBlast(body, ctx);
                    return;
            }
            float mult = Settings.BulletMomentumMultiplier.Value;
            if (mult <= 0f) return;

            float total = 0f;
            foreach (var w in ctx.LethalVolley)
            {
                Segment seg = null;
                if (w.HitCollider != null) seg = body.SegmentForRigidbody(w.HitCollider.attachedRigidbody);
                if (seg == null) seg = body.SegmentForBone(w.Bone);
                if (seg == null) seg = body.SegmentForCollider(w.ColliderType);
                if (seg == null) continue;

                Vector3 point = w.Bone != null ? w.CurrentWorldPoint : w.HitPointWorld;
                if (point.sqrMagnitude < 1e-6f || (point - seg.Com).sqrMagnitude > 1f) point = seg.Com;
                Vector3 dir = w.Direction.sqrMagnitude > 1e-6f ? w.Direction.normalized : ctx.Direction;
                if (dir.sqrMagnitude < 1e-6f) continue;

                float p;
                var ammo = AmmoDatabase.Get(w.AmmoId) ?? ctx.Ammo;
                if (ammo != null)
                {
                    float dist = w.MasterOrigin.sqrMagnitude > 1e-6f ? Vector3.Distance(w.MasterOrigin, w.HitPointWorld) : 30f;
                    p = ammo.MomentumAt(ammo.ImpactSpeed(dist));
                }
                else
                {
                    p = Mathf.Clamp(w.Damage * 0.04f, 0.5f, 8f); // melee, fragment, unknown projectile
                }
                // Per-ammo-type variability: transfer, scatter, and lever arm drawn within its range.
                var kick = AmmoKick.For(ammo, w.Penetration);
                p *= mult * (w == ctx.LethalVolley[ctx.LethalVolley.Count - 1] ? ctx.KickScale : kick.Draw(ctx.Rng));
                dir = kick.Scatter(dir, ctx.Rng);
                point += kick.LeverOffset(dir, ctx.Rng);
                if (total + p > MaxVolleyImpulse) p = Mathf.Max(0f, MaxVolleyImpulse - total);
                if (p <= 0f) break;
                total += p;
                if (sink != null) sink(seg, point, dir, p, w == ctx.LethalVolley[ctx.LethalVolley.Count - 1]);
                else seg.Rb.AddForceAtPosition(dir * p, point, ForceMode.Impulse);
            }
            Plugin.LogDebug($"  total ballistic impulse {total:0.0} N·s");
        }

        private static void ApplyBlast(BodyModel body, DeathContext ctx)
        {
            var rng = ctx.Rng;
            // Blast: actual distance to charge, obstacle shielding, and body orientation vary from death to death.
            float J = Mathf.Clamp(ctx.Damage * 0.6f, 15f, 220f) * Settings.ExplosionImpulseMultiplier.Value * BMath.Range(rng, 0.7f, 1.4f);
            if (J <= 0f) return;
            Vector3 pelvis = body[Seg.Pelvis].Com;
            Vector3 origin = ctx.HitPoint.sqrMagnitude > 1e-6f ? ctx.HitPoint : pelvis;
            Vector3 dir = ctx.Direction;
            if (dir.sqrMagnitude < 1e-6f)
            {
                float a = BMath.Range(rng, 0f, BMath.TwoPi);
                dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            }
            dir = (dir.normalized + Vector3.up * 0.35f).normalized;

            float sum = 0f;
            var w = new float[body.Segs.Length];
            for (int i = 0; i < body.Segs.Length; i++)
            {
                var s = body.Segs[i];
                w[i] = s.Mass / (1f + 2f * Vector3.Distance(s.Com, origin));
                sum += w[i];
            }
            if (sum <= 0f) return;
            for (int i = 0; i < body.Segs.Length; i++)
            {
                var s = body.Segs[i];
                float share = J * w[i] / sum;
                Vector3 jitter = new Vector3(BMath.Range(rng, -1f, 1f), BMath.Range(rng, -0.3f, 1f), BMath.Range(rng, -1f, 1f)) * 0.2f;
                s.Rb.AddForce((dir + jitter) * share, ForceMode.Impulse);
                // Chaotic rotation of distal segments (flailing limbs).
                bool distal = s.Id != Seg.Pelvis && s.Id != Seg.Thorax;
                if (distal)
                {
                    Vector3 spin = new Vector3(BMath.Range(rng, -1f, 1f), BMath.Range(rng, -1f, 1f), BMath.Range(rng, -1f, 1f));
                    s.Rb.AddTorque(spin * share * 0.04f, ForceMode.Impulse);
                }
            }
            Plugin.LogDebug($"  blast impulse {J:0} N·s");
        }
    }

    /// <summary>
    /// Impulse profile for an ammunition class. True momentum (m·v) is only an order of magnitude:
    /// the fraction actually transferred to the body depends on deformation, fragmentation, tumbling in tissue,
    /// and whether the projectile exits. Each class therefore draws its multiplier from its own range, with
    /// directional scatter (ricochet off gear, deflected path) and a variable lever arm around the impact point
    /// (causing more or less rotation of the struck segment).
    /// </summary>
    internal readonly struct AmmoKick
    {
        public readonly string Class;
        public readonly float Min, Max;     // transfer multiplier
        public readonly float ConeDeg;      // directional scatter
        public readonly float Lever;        // max lateral offset of application point (m)
        public readonly float Skew;         // > 1: draws lean toward the upper end of the range

        public AmmoKick(string cls, float min, float max, float cone, float lever, float skew)
        {
            Class = cls; Min = min; Max = max; ConeDeg = cone; Lever = lever; Skew = skew;
        }

        public static AmmoKick For(AmmoInfo ammo, float penetration)
        {
            AmmoKick k;
            if (ammo == null) k = new AmmoKick("unknown", 0.8f, 1.4f, 14f, 0.05f, 1f);
            else if (ammo.IsBuckshot)
                k = ammo.ProjectileCount <= 1
                    ? new AmmoKick("slug", 1.1f, 2.1f, 7f, 0.07f, 1.2f)
                    : new AmmoKick("buckshot", 0.7f, 1.7f, 20f, 0.09f, 1f);
            else if (ammo.IsGrenade) k = new AmmoKick("grenade", 1.2f, 2.2f, 15f, 0.08f, 1f);
            else if (ammo.DiameterMm >= 11.5f) k = new AmmoKick("heavy caliber", 1.15f, 2.1f, 6f, 0.08f, 1.3f);
            else if (ammo.BulletMassKg >= 0.009f && ammo.InitialSpeed < 400f)
                k = new AmmoKick("heavy subsonic", 1.05f, 1.75f, 5f, 0.05f, 1.4f);   // dumps nearly all its energy
            else if (ammo.BulletMassKg >= 0.0085f && ammo.InitialSpeed >= 650f)
                k = new AmmoKick("full power", 0.9f, 1.85f, 6f, 0.07f, 1f);
            else if (ammo.InitialSpeed >= 600f)
                k = new AmmoKick("intermediate", 0.75f, 1.55f, 12f, 0.06f, 0.9f);    // tumbling, fragmentation
            else k = new AmmoKick("pistol", 0.85f, 1.4f, 9f, 0.04f, 1f);

            // High penetration: the bullet often exits the body and transfers only part of its momentum.
            if (penetration > 40f)
            {
                float f = Mathf.Lerp(1f, 0.72f, Mathf.Clamp01((penetration - 40f) / 30f));
                k = new AmmoKick(k.Class + " armor-piercing", k.Min * f, k.Max * Mathf.Lerp(1f, 0.85f, 1f - f), k.ConeDeg * 0.7f, k.Lever, k.Skew * 0.8f);
            }
            return k;
        }

        /// <summary>Multiplier drawn in [Min, Max] (distribution skewed by Skew).</summary>
        public float Draw(System.Random rng)
        {
            float x = Mathf.Pow((float)rng.NextDouble(), 1f / Mathf.Max(0.3f, Skew));
            return Mathf.Lerp(Min, Max, x);
        }

        public Vector3 Scatter(Vector3 dir, System.Random rng)
        {
            if (ConeDeg <= 0f || dir.sqrMagnitude < 1e-8f) return dir;
            Vector3 n = dir.normalized;
            Vector3 a = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            float spin = BMath.Range(rng, 0f, 360f);
            float tilt = ConeDeg * Mathf.Sqrt((float)rng.NextDouble());
            return Quaternion.AngleAxis(spin, n) * (Quaternion.AngleAxis(tilt, a) * n);
        }

        public Vector3 LeverOffset(Vector3 dir, System.Random rng)
        {
            Vector3 n = BMath.SafeNormalize(dir, Vector3.forward);
            Vector3 a = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 b = Vector3.Cross(n, a);
            return (a * BMath.Range(rng, -1f, 1f) + b * BMath.Range(rng, -1f, 1f)) * (0.7f * Lever);
        }
    }
}
