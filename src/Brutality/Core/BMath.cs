using UnityEngine;

namespace Brutality.Core
{
    /// <summary>Math utilities (angles in radians).</summary>
    internal static class BMath
    {
        public const float Pi = Mathf.PI;
        public const float TwoPi = Mathf.PI * 2f;
        public const float Deg = Mathf.Deg2Rad;

        public static float WrapPi(float a)
        {
            if (float.IsNaN(a) || float.IsInfinity(a)) return 0f;
            while (a > Pi) a -= TwoPi;
            while (a < -Pi) a += TwoPi;
            return a;
        }

        /// <summary>Twist angle (swing-twist) of a quaternion around a unit axis.</summary>
        public static float TwistAngle(Quaternion q, Vector3 axis)
        {
            float d = q.x * axis.x + q.y * axis.y + q.z * axis.z;
            return WrapPi(2f * Mathf.Atan2(d, q.w));
        }

        /// <summary>Signed angle (rad) from a to b around n; a and b are projected onto the plane normal to n.</summary>
        public static float SignedAngleOnPlane(Vector3 a, Vector3 b, Vector3 n)
        {
            a -= n * Vector3.Dot(a, n);
            b -= n * Vector3.Dot(b, n);
            float y = Vector3.Dot(Vector3.Cross(a, b), n);
            float x = Vector3.Dot(a, b);
            if (Mathf.Abs(x) < 1e-9f && Mathf.Abs(y) < 1e-9f) return 0f;
            return Mathf.Atan2(y, x);
        }

        public static float SafeAsin(float x) => Mathf.Asin(Mathf.Clamp(x, -1f, 1f));

        public static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            float m = v.magnitude;
            return m > 1e-6f ? v / m : fallback;
        }

        /// <summary>Orthonormal frame (R, U, F) from a primary U axis and an R hint; F = R × U (anterior).</summary>
        public static void OrthoFrame(Vector3 up, Vector3 rightHint, Vector3 forwardHint, out Vector3 r, out Vector3 u, out Vector3 f)
        {
            u = SafeNormalize(up, Vector3.up);
            Vector3 rr = rightHint - u * Vector3.Dot(rightHint, u);
            if (rr.sqrMagnitude < 0.04f * Mathf.Max(rightHint.sqrMagnitude, 1e-6f))
            {
                // Right hint nearly parallel to U: derive R from the anterior hint (R = U × F).
                Vector3 ff = forwardHint - u * Vector3.Dot(forwardHint, u);
                ff = SafeNormalize(ff, Mathf.Abs(u.z) < 0.9f ? Vector3.forward : Vector3.right);
                rr = Vector3.Cross(u, ff);
            }
            r = SafeNormalize(rr, Vector3.right);
            f = Vector3.Cross(r, u).normalized;
        }

        public static float SmoothStep(float e0, float e1, float x)
        {
            if (e1 <= e0) return x >= e1 ? 1f : 0f;
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>Attack/sustain/release envelope between 0 and 1.</summary>
        public static float Envelope(float t, float start, float duration, float attack, float release)
        {
            float local = t - start;
            if (local <= 0f || local >= duration) return 0f;
            float a = attack > 1e-4f ? Mathf.Clamp01(local / attack) : 1f;
            float r = release > 1e-4f ? Mathf.Clamp01((duration - local) / release) : 1f;
            return Mathf.Min(a, r);
        }

        public static float Range(System.Random rng, float min, float max) => min + (float)rng.NextDouble() * (max - min);

        public static float Range(System.Random rng, Vector2 range) => Range(rng, range.x, range.y);

        public static bool Chance(System.Random rng, float p) => rng.NextDouble() < p;

        /// <summary>Gaussian draw (Box-Muller).</summary>
        public static float Gaussian(System.Random rng, float mean, float sd)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            double z = System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Sin(2.0 * System.Math.PI * u2);
            return mean + sd * (float)z;
        }

        /// <summary>Deterministic 1D smooth noise (sum of incommensurable sinusoids), in [-1, 1].</summary>
        public static float SmoothNoise(float t, float seed)
        {
            return 0.5f * Mathf.Sin(t * 1.31f + seed) + 0.3f * Mathf.Sin(t * 2.17f + seed * 1.7f) + 0.2f * Mathf.Sin(t * 3.73f + seed * 2.3f);
        }

        /// <summary>Moment of inertia of a segment around a unit axis passing through its center of mass.</summary>
        public static float MomentAboutAxis(Quaternion principalRotWorld, Vector3 principalInertia, Vector3 axisWorld)
        {
            Vector3 n = Quaternion.Inverse(principalRotWorld) * axisWorld;
            return principalInertia.x * n.x * n.x + principalInertia.y * n.y * n.y + principalInertia.z * n.z * n.z;
        }

        /// <summary>Normalizes a quaternion to prevent numerical drift after repeated multiplications.</summary>
        public static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m < 1e-8f || float.IsNaN(m)) return Quaternion.identity;
            float inv = 1f / m;
            return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
        }

        /// <summary>
        /// Exact analytical integration of a critically damped oscillator (ζ = 1) toward <paramref name="goal"/>,
        /// unconditionally stable for any <paramref name="dt"/>, with velocity clamped to ±<paramref name="vlim"/>.
        /// </summary>
        public static void StepCriticallyDamped(ref float x, ref float v, float goal, float omega, float dt, float vlim)
        {
            float e = x - goal;
            float ex = Mathf.Exp(-omega * dt);
            float c = v + omega * e;
            float xNew = goal + (e + c * dt) * ex;
            float vNew = (v - omega * c * dt) * ex;
            if (Mathf.Abs(vNew) > vlim)
            {
                vNew = vNew > 0f ? vlim : -vlim;
                xNew = x + vNew * dt;
                // Do not overshoot the target during a long step at capped velocity.
                if ((x - goal) * (xNew - goal) < 0f)
                {
                    xNew = goal;
                    vNew = 0f;
                }
            }
            x = xNew;
            v = vNew;
        }
    }
}
