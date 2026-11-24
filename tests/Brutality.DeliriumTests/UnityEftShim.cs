// Shim minimal UnityEngine / EFT / Brutality pour compiler et exécuter les VRAIES classes Delirium hors-jeu,
// avec un vrai moteur de raycast 3D (sol + boîtes AABB : murs, caisses, rebords) et enregistrement des forces/couples.
using System;
using System.Collections.Generic;
using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Neural;
using EFT;
using UnityEngine;

namespace UnityEngine
{
    public enum ForceMode { Force = 0, Acceleration = 5, Impulse = 1, VelocityChange = 2 }
    public enum QueryTriggerInteraction { UseGlobal = 0, Ignore = 1, Collide = 2 }

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
        public static Vector3 back => new Vector3(0, 0, -1);

        public float this[int i]
        {
            get => i == 0 ? x : i == 1 ? y : z;
            set { if (i == 0) x = value; else if (i == 1) y = value; else z = value; }
        }
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized { get { float m = magnitude; return m > 1e-5f ? this / m : zero; } }
        public void Normalize() { float m = magnitude; if (m > 1e-5f) { x /= m; y /= m; z /= m; } else { x = y = z = 0f; } }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);

        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) =>
            new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static Vector3 Scale(Vector3 a, Vector3 b) => new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
        public static Vector3 Project(Vector3 v, Vector3 onNormal)
        {
            float sq = onNormal.sqrMagnitude;
            return sq < 1e-12f ? zero : onNormal * (Dot(v, onNormal) / sq);
        }
        public static Vector3 ClampMagnitude(Vector3 v, float maxLength)
        {
            float sq = v.sqrMagnitude;
            return sq > maxLength * maxLength ? v * (maxLength / MathF.Sqrt(sq)) : v;
        }
        public static float Angle(Vector3 from, Vector3 to)
        {
            float d = MathF.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (d < 1e-15f) return 0f;
            return MathF.Acos(Math.Clamp(Dot(from, to) / d, -1f, 1f)) * 57.29578f;
        }
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { t = Math.Clamp(t, 0f, 1f); return a + (b - a) * t; }
        public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t) => a + (b - a) * t;
        public static Vector3 Slerp(Vector3 a, Vector3 b, float t) => Lerp(a, b, t).normalized;
        public static Vector3 Max(Vector3 a, Vector3 b) => new Vector3(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));
        public override string ToString() => $"({x:0.000}, {y:0.000}, {z:0.000})";
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);

        public static Quaternion operator *(Quaternion lhs, Quaternion rhs) => new Quaternion(
            lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
            lhs.w * rhs.y + lhs.y * rhs.w + lhs.z * rhs.x - lhs.x * rhs.z,
            lhs.w * rhs.z + lhs.z * rhs.w + lhs.x * rhs.y - lhs.y * rhs.x,
            lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z);

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
            if (axis.sqrMagnitude < 1e-8f) return identity;
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
        public static float Sign(float v) => v >= 0f ? 1f : -1f;
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
        public static int RoundToInt(float v) => (int)MathF.Round(v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Repeat(float t, float length) => Clamp(t - MathF.Floor(t / length) * length, 0f, length);
    }

    public sealed class Rigidbody
    {
        public float mass = 10f;
        public Vector3 inertiaTensor = new Vector3(0.2f, 0.2f, 0.2f);
        public Vector3 velocity;
        public Vector3 angularVelocity;
        public Vector3 ForceAccum;
        public Vector3 TorqueAccum;
        public Vector3 AccelAccum;
        public Vector3 AngAccelAccum;
        public Vector3 ImpulseAccum;
        public Vector3 AngImpulseAccum;
        public int WakeCount;

        public void ClearAccum()
        {
            ForceAccum = TorqueAccum = AccelAccum = AngAccelAccum = ImpulseAccum = AngImpulseAccum = Vector3.zero;
        }

        public void AddForce(Vector3 f, ForceMode mode = ForceMode.Force)
        {
            switch (mode)
            {
                case ForceMode.Force: ForceAccum += f; break;
                case ForceMode.Acceleration: AccelAccum += f; break;
                case ForceMode.Impulse: ImpulseAccum += f; velocity += f / MathF.Max(0.1f, mass); break;
                case ForceMode.VelocityChange: velocity += f; break;
            }
        }

        public void AddTorque(Vector3 t, ForceMode mode = ForceMode.Force)
        {
            float iAvg = MathF.Max(0.01f, (inertiaTensor.x + inertiaTensor.y + inertiaTensor.z) / 3f);
            switch (mode)
            {
                case ForceMode.Force: TorqueAccum += t; break;
                case ForceMode.Acceleration: AngAccelAccum += t; break;
                case ForceMode.Impulse: AngImpulseAccum += t; angularVelocity += t / iAvg; break;
                case ForceMode.VelocityChange: angularVelocity += t; break;
            }
        }

        public void WakeUp() => WakeCount++;
    }

    public struct RaycastHit
    {
        public Vector3 point;
        public Vector3 normal;
        public float distance;
        public Rigidbody rigidbody;
    }

    /// <summary>Boîte statique du décor pour les tests (mur, caisse, rebord, table).</summary>
    public struct StaticBox
    {
        public Vector3 Min, Max;
        public StaticBox(Vector3 center, Vector3 size)
        {
            Vector3 h = size * 0.5f;
            Min = center - h;
            Max = center + h;
        }
    }

    public static class Physics
    {
        public const int DefaultRaycastLayers = -1;
        public static Vector3 gravity = new Vector3(0f, -9.81f, 0f);
        public static float GroundY = 0f;
        public static bool HasGround = true;
        public static readonly List<StaticBox> Boxes = new List<StaticBox>();

        public static void ResetWorld(float groundY = 0f)
        {
            GroundY = groundY;
            HasGround = true;
            Boxes.Clear();
        }

        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float maxDistance, int layerMask, QueryTriggerInteraction qti)
        {
            hit = default;
            Vector3 dir = direction.normalized;
            if (dir.sqrMagnitude < 1e-6f) return false;
            float bestT = maxDistance + 1f;
            Vector3 bestN = Vector3.up;
            bool found = false;

            if (HasGround && MathF.Abs(dir.y) > 1e-5f)
            {
                float t = (GroundY - origin.y) / dir.y;
                if (t >= 0f && t <= maxDistance && t < bestT)
                {
                    bestT = t;
                    bestN = dir.y < 0f ? Vector3.up : Vector3.down;
                    found = true;
                }
            }
            for (int i = 0; i < Boxes.Count; i++)
            {
                if (RayBox(origin, dir, Boxes[i], out float t, out Vector3 n) && t >= 0f && t <= maxDistance && t < bestT)
                {
                    bestT = t;
                    bestN = n;
                    found = true;
                }
            }
            if (!found) return false;
            hit = new RaycastHit { point = origin + dir * bestT, normal = bestN, distance = bestT, rigidbody = null };
            return true;
        }

        private static bool RayBox(Vector3 o, Vector3 d, StaticBox b, out float tHit, out Vector3 nHit)
        {
            tHit = 0f;
            nHit = Vector3.up;
            float tmin = 0f, tmax = float.MaxValue;
            Vector3 n = Vector3.zero;
            for (int k = 0; k < 3; k++)
            {
                float ok = o[k], dk = d[k], lo = b.Min[k], hi = b.Max[k];
                if (MathF.Abs(dk) < 1e-6f)
                {
                    if (ok < lo || ok > hi) return false;
                    continue;
                }
                float inv = 1f / dk;
                float t0 = (lo - ok) * inv, t1 = (hi - ok) * inv;
                float sign = -1f;
                if (t0 > t1) { float tmp = t0; t0 = t1; t1 = tmp; sign = 1f; }
                if (t0 > tmin)
                {
                    tmin = t0;
                    n = Vector3.zero;
                    n[k] = sign;
                }
                if (t1 < tmax) tmax = t1;
                if (tmin > tmax) return false;
            }
            if (tmin <= 0f) return false;
            tHit = tmin;
            nHit = n;
            return true;
        }
    }
}

namespace EFT
{
    public enum EPlayerPose { Stand, Duck, Prone }
    public enum EBodyPart { Head, Chest, Stomach, LeftArm, RightArm, LeftLeg, RightLeg }
    public enum EBodyPartColliderType
    {
        None, Pelvis, RibcageUp, HeadCommon, LeftUpperArm, LeftForearm, RightUpperArm, RightForearm,
        LeftThigh, LeftCalf, RightThigh, RightCalf
    }
}

public static class LayersMaskController
{
    public static int HighPolyWithTerrainMask => -1;
}

namespace Brutality
{
    internal sealed class SettingVal<T> { public T Value; public SettingVal(T v) { Value = v; } }

    internal static class Settings
    {
        public static readonly SettingVal<bool> DebugLogging = new SettingVal<bool>(true);
        public static readonly SettingVal<string> EuphoriaForce = new SettingVal<string>("Auto");
        public static readonly SettingVal<float> GestureVariety = new SettingVal<float>(1f);
        public static readonly SettingVal<float> PosturingIntensity = new SettingVal<float>(1f);
        public static readonly SettingVal<float> FallDrive = new SettingVal<float>(1f);
        public static readonly SettingVal<float> SpasmVigor = new SettingVal<float>(1f);
    }
}

namespace Brutality.Core
{
    internal enum DeathMechanism
    {
        Cerebral, CervicalCord, NeckVascular, Thoracic, ThoracicCord, Abdominal, LumbarCord, Limb, Blast, Syncope, FallTrauma, Burn
    }

    internal sealed class DeathContext
    {
        public EBodyPart BodyPart = EBodyPart.Chest;
        public EBodyPartColliderType ColliderType = EBodyPartColliderType.RibcageUp;
        public Vector3 HitPoint;
        public Vector3 Direction = new Vector3(0f, 0f, -1f);
        public float ImpactMomentum = 3.2f;
        public float KickScale = 1f;
        public string KickClass = "intermédiaire";
        public EPlayerPose Pose = EPlayerPose.Stand;
        public Vector3 Velocity = Vector3.zero;
        public DeathMechanism Mechanism = DeathMechanism.Thoracic;
        public System.Random Rng = new System.Random(42);
    }

    internal sealed class DrivenJoint
    {
        public Vector3 Anchor;
        public Vector3 Axis, Secondary, Third;
    }

    internal sealed class Segment
    {
        public int Index;
        public Seg Id;
        public Rigidbody Rb;
        public int Parent;
        public Vector3 RL, UL, FL;
        public float Length;
        public float Mass;
        public Vector3 InertiaDiag;
        public Quaternion InertiaRotLocal = Quaternion.identity;
        public Quaternion Q = Quaternion.identity;
        public Vector3 W, V, Com, Pos;
        public Vector3 R => Q * RL;
        public Vector3 U => Q * UL;
        public Vector3 F => Q * FL;
        public Vector3 DistalWorld => Pos - U * Length;
    }

    internal sealed class JointModel
    {
        public int Index;
        public JointKind Kind;
        public JointSpec Spec;
        public int Side;
        public int P, C;
        public bool[] InSubtree = new bool[AnatomyData.SegCount];
        public JointFrame Frame;
        public DrivenJoint Driven;
        public Vector3 Phi, Phi0, PhiDot;
        public readonly float[] Capacity = { 1f, 1f, 1f };
    }

    internal sealed class BodyModel
    {
        public readonly Segment[] Segs = new Segment[AnatomyData.SegCount];
        public readonly JointModel[] Joints = new JointModel[AnatomyData.JointCount];
        public float TotalMass;
        public float ThoraxHalfShoulderW = 0.18f;
        public float ThoraxChestZ = 0.03f;
        public float ThoraxYBot = -0.05f;
        public float ThoraxYTop = 0.25f;
        public float ThoraxRx = 0.14f;
        public float ThoraxRz = 0.125f;
        public const float ThoraxRyCap = 0.06f;
        public float MaxSpeed;

        public Segment this[Seg s] => Segs[(int)s];
    }

    internal sealed class BrutalityRagdoll
    {
        public static BrutalityRagdoll OwnerOf(Rigidbody rb) => null;
    }
}

namespace Brutality.Neural
{
    internal enum AgonyEvent
    {
        Clutch, Foetal, Extension, Myoclonus, Pedal, Kick, ArmFlail, RollTry, HeadTurn, Count
    }

    internal sealed class NeuralController
    {
        public float MusclesCap = 7.5f;
        public float AgonyEnd = 6.5f;
        public float GroundAt = -1f;
        public void OnNmGround(float t) { GroundAt = t; }
        public float AgonyIntensity(float t) => GroundAt >= 0f && t < AgonyEnd ? 0.85f : 0f;
    }
}
