// Réimplémentation minimale des types mathématiques d'UnityEngine (mêmes conventions et formules).
using System;

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 up => new Vector3(0, 1, 0);
        public static Vector3 down => new Vector3(0, -1, 0);
        public static Vector3 right => new Vector3(1, 0, 0);
        public static Vector3 left => new Vector3(-1, 0, 0);
        public static Vector3 forward => new Vector3(0, 0, 1);
        public float this[int i]
        {
            get => i == 0 ? x : i == 1 ? y : z;
            set { if (i == 0) x = value; else if (i == 1) y = value; else z = value; }
        }
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized { get { float m = magnitude; return m > 1e-5f ? this / m : zero; } }
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) =>
            new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static float Angle(Vector3 from, Vector3 to)
        {
            float d = MathF.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (d < 1e-15f) return 0f;
            float dot = Math.Clamp(Dot(from, to) / d, -1f, 1f);
            return MathF.Acos(dot) * 57.29578f;
        }
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { t = Math.Clamp(t, 0f, 1f); return a + (b - a) * t; }
        public static Vector3 Max(Vector3 a, Vector3 b) => new Vector3(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));
        public override string ToString() => $"({x:0.000}, {y:0.000}, {z:0.000})";
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);

        // Formule identique à UnityEngine.Quaternion.operator*(Quaternion, Quaternion).
        public static Quaternion operator *(Quaternion lhs, Quaternion rhs) => new Quaternion(
            lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
            lhs.w * rhs.y + lhs.y * rhs.w + lhs.z * rhs.x - lhs.x * rhs.z,
            lhs.w * rhs.z + lhs.z * rhs.w + lhs.x * rhs.y - lhs.y * rhs.x,
            lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z);

        // Formule identique à UnityEngine.Quaternion.operator*(Quaternion, Vector3).
        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            float num = rotation.x * 2f, num2 = rotation.y * 2f, num3 = rotation.z * 2f;
            float num4 = rotation.x * num, num5 = rotation.y * num2, num6 = rotation.z * num3;
            float num7 = rotation.x * num2, num8 = rotation.x * num3, num9 = rotation.y * num3;
            float num10 = rotation.w * num, num11 = rotation.w * num2, num12 = rotation.w * num3;
            Vector3 result;
            result.x = (1f - (num5 + num6)) * point.x + (num7 - num12) * point.y + (num8 + num11) * point.z;
            result.y = (num7 + num12) * point.x + (1f - (num4 + num6)) * point.y + (num9 - num10) * point.z;
            result.z = (num8 - num11) * point.x + (num9 + num10) * point.y + (1f - (num4 + num5)) * point.z;
            return result;
        }

        public static Quaternion Inverse(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        public static Quaternion AngleAxis(float angleDeg, Vector3 axis)
        {
            axis = axis.normalized;
            float h = angleDeg * 0.0174532924f * 0.5f;
            float s = MathF.Sin(h);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, MathF.Cos(h));
        }
    }

    public static class Mathf
    {
        public const float PI = MathF.PI;
        public const float Deg2Rad = PI / 180f;
        public const float Rad2Deg = 180f / PI;
        public static float Clamp(float v, float a, float b) => v < a ? a : (v > b ? b : v);
        public static int Clamp(int v, int a, int b) => v < a ? a : (v > b ? b : v);
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
        public static float Abs(float v) => MathF.Abs(v);
        public static float Sqrt(float v) => MathF.Sqrt(v);
        public static float Atan2(float y, float x) => MathF.Atan2(y, x);
        public static float Asin(float v) => MathF.Asin(v);
        public static float Acos(float v) => MathF.Acos(v);
        public static float Sin(float v) => MathF.Sin(v);
        public static float Cos(float v) => MathF.Cos(v);
        public static float Exp(float v) => MathF.Exp(v);
        public static float Pow(float a, float b) => MathF.Pow(a, b);
        public static float Min(float a, float b) => MathF.Min(a, b);
        public static float Max(float a, float b) => MathF.Max(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Floor(float v) => MathF.Floor(v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Repeat(float t, float length) => Clamp(t - MathF.Floor(t / length) * length, 0f, length);
    }
}
