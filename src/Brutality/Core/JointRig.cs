using System;
using Brutality.Anatomy;
using HarmonyLib;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>Driven PhysX joint: anatomical ConfigurableJoint + control frame.</summary>
    internal sealed class DrivenJoint
    {
        public ConfigurableJoint Joint;
        public Quaternion StartRel;      // parent→child relative rotation at setup time (neutral pose)
        public Vector3 Anchor;           // joint anchor (child local)
        public Vector3 Axis, Secondary, Third; // joint orthonormal frame (child local: X flex, Y abd, Z twist)
        public float Flex0Rad;           // reference flexion at setup (rad)
        public float LowRelRad;          // lower bound relative to Flex0Rad (rad, >= -175°)
        public float HighRelRad;         // upper bound relative to Flex0Rad (rad, <= +175°)
        public Quaternion LastSolve;     // last solved target pose (solver warm-start)
        public bool HasSolve;
    }

    /// <summary>
    /// Replaces EFT's CharacterJoints with anatomical ConfigurableJoints.
    /// The joint's reference pose is the death pose. The X axis is the direction that increases flexion
    /// (angular gradient), the Y axis is abduction / lateral tilt. The X limit reproduces the exact
    /// (asymmetric) flexion range, in the direction measured by the PhysX probe; the Y/Z limits (cone)
    /// cover the range around the death pose. Limits are "soft" (damped spring):
    /// resistance ramps up beyond the range instead of a hard stop (corpus §4 and §10).
    /// Muscles become PhysX drives (implicit, hence unconditionally stable).
    /// </summary>
    internal static class JointRig
    {
        private static readonly AccessTools.FieldRef<JointSpawner, Joint> SpawnerJoint =
            AccessTools.FieldRefAccess<JointSpawner, Joint>("_joint");

        public static int Build(BodyModel body)
        {
            int ok = 0;
            foreach (var jm in body.Joints)
            {
                try
                {
                    if (BuildOne(body, jm)) ok++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Brutality: joint {(JointId)jm.Index} not converted ({e.Message}).");
                }
            }
            return ok;
        }

        private static bool BuildOne(BodyModel body, JointModel jm)
        {
            var sp = body.Segs[jm.P];
            var sc = body.Segs[jm.C];
            var spec = jm.Spec;

            // Joint reference pose = death pose (no assumption about when Unity captures it).
            Quaternion qP = sp.Rb.rotation;
            Quaternion qC = sc.Rb.rotation;
            Quaternion qRel0 = BMath.Normalize(Quaternion.Inverse(qP) * qC);

            // Analytical joint axes (child local): immune to gradient singularities (shoulder ±90°).
            Vector3 phi0 = JointKinematics.Evaluate(jm.Frame, Quaternion.identity, qRel0, out _);
            JointKinematics.JointAxes(jm.Frame, qRel0, out Vector3 axis, out Vector3 sec);

            // Exact asymmetric X limit: [min - φ0, max - φ0] (direction measured by the PhysX probe).
            float f0 = phi0.x / BMath.Deg;
            float lowRel = Mathf.Min(spec.Dofs[0].MinDeg - f0, -3f);
            float highRel = Mathf.Max(spec.Dofs[0].MaxDeg - f0, 3f);
            lowRel = Mathf.Max(lowRel, -175f);
            highRel = Mathf.Min(highRel, 175f);
            // Unity's PhysX ConfigurableJoint X limits are negated by default (verified by PhysxProbe).
            bool invX = PhysxProbe.LimitXInverted ?? true;
            float lowX = invX ? -highRel : lowRel;
            float highX = invX ? -lowRel : highRel;

            // Y/Z limits (PhysX cone, symmetric): cover the range around the death pose without inflating
            // neck or shoulder cones beyond their physiological half-width.
            bool isHinge = jm.Kind == JointKind.Knee || jm.Kind == JointKind.Elbow;
            float HalfAround(int d)
            {
                float p = (d == 1 ? phi0.y : phi0.z) / BMath.Deg;
                float min = spec.Dofs[d].MinDeg, max = spec.Dofs[d].MaxDeg;
                float half = Mathf.Max(max - p, p - min);
                float margin = (jm.Kind == JointKind.Shoulder || jm.Kind == JointKind.Neck) ? 0f : 10f;
                return Mathf.Clamp(Mathf.Min(half, 0.5f * (max - min) + margin), 5f, 170f);
            }
            float halfY = HalfAround(1);
            float halfZ = HalfAround(2);
            if (jm.Kind == JointKind.Shoulder)
            {
                float py = phi0.y / BMath.Deg;
                halfY = py < 25f
                    ? Mathf.Clamp(Mathf.Max(spec.Dofs[1].MaxDeg - py, 58f), 58f, 95f)
                    : Mathf.Clamp(Mathf.Max(py - spec.Dofs[1].MinDeg, spec.Dofs[1].MaxDeg - py), 50f, 78f);
                halfZ = 75f;
            }

            Joint old = jm.UnityJoint;
            Vector3 anchor = old != null ? old.anchor : Vector3.zero;
            var spawner = sc.T.GetComponent<JointSpawner>();

            // Bounded mass balancing (computed on base anatomical masses, excluding backpack/vest):
            // prevents an extreme massScale (15-30× on a geared shoulder) from tipping the entire thorax
            // under a 2 kg arm or crushing the stiffness of soft limits.
            float balance = Settings.JointMassBalancing.Value;
            float baseRatio = sc.BaseMass > 1e-3f ? sp.BaseMass / sc.BaseMass : 1f;
            float massScale = Mathf.Clamp(Mathf.Pow(baseRatio, balance), 0.5f, 4f);

            ConfigurableJoint cj = null;
            bool configured = false;
            try
            {
                cj = sc.T.gameObject.AddComponent<ConfigurableJoint>();
                cj.autoConfigureConnectedAnchor = false;
                cj.anchor = anchor;
                cj.axis = axis;
                cj.secondaryAxis = sec;
                cj.connectedBody = sp.Rb;
                cj.connectedAnchor = sp.T.InverseTransformPoint(sc.T.TransformPoint(anchor));
                cj.xMotion = ConfigurableJointMotion.Locked;
                cj.yMotion = ConfigurableJointMotion.Locked;
                cj.zMotion = ConfigurableJointMotion.Locked;
                cj.angularXMotion = ConfigurableJointMotion.Limited;
                // Hinges (knee, elbow): locking varus/valgus (Y) avoids a 10:1 elliptical cone (8°×80°)
                // that causes PhysX jitter; PhysX switches to the universal constraint (twist + 1 swing Z).
                cj.angularYMotion = isHinge ? ConfigurableJointMotion.Locked : ConfigurableJointMotion.Limited;
                cj.angularZMotion = ConfigurableJointMotion.Limited;
                cj.lowAngularXLimit = new SoftJointLimit { limit = lowX };
                cj.highAngularXLimit = new SoftJointLimit { limit = highX };
                cj.angularYLimit = new SoftJointLimit { limit = halfY };
                cj.angularZLimit = new SoftJointLimit { limit = halfZ };

                // Progressive limits: stiffness derived from exponential passive torque, capped to effective inertia
                // to avoid excessive stiffness on small segments (forearm, shin) and critically damped.
                // Ball-and-socket joints share angularYZLimitSpring across swing (Y) and twist (Z), so use
                // transverse inertia there to prevent shoulder abduction limits from collapsing under trunk weight.
                float ps = Settings.PassiveStiffness.Value;
                float iTransEff = Mathf.Max(0.01f, sc.InertiaDiag.x * massScale);
                float iLongEff = Mathf.Max(0.005f, sc.InertiaDiag.y * massScale);
                float iYzEff = isHinge ? iLongEff : iTransEff;
                float kx = Mathf.Min(6f * spec.Dofs[0].PassiveA * spec.Dofs[0].PassiveB * ps, 6400f * iTransEff);
                float kyzRaw = isHinge
                    ? 6f * spec.Dofs[2].PassiveA * spec.Dofs[2].PassiveB * ps
                    : 6f * Mathf.Max(spec.Dofs[1].PassiveA * spec.Dofs[1].PassiveB, spec.Dofs[2].PassiveA * spec.Dofs[2].PassiveB) * ps;
                float kyz = Mathf.Min(kyzRaw, 6400f * iYzEff);
                float cx = Mathf.Max(0.06f * kx, 1.8f * Mathf.Sqrt(kx * iTransEff));
                float cyz = Mathf.Max(0.06f * kyz, 1.8f * Mathf.Sqrt(kyz * iYzEff));
                cj.angularXLimitSpring = new SoftJointLimitSpring { spring = kx, damper = cx };
                cj.angularYZLimitSpring = new SoftJointLimitSpring { spring = kyz, damper = cyz };

                cj.rotationDriveMode = RotationDriveMode.XYAndZ;
                cj.angularXDrive = new JointDrive { useAcceleration = false };
                cj.angularYZDrive = new JointDrive { useAcceleration = false };

                cj.projectionMode = JointProjectionMode.PositionAndRotation;
                cj.projectionDistance = 0.03f;
                cj.projectionAngle = 5f;
                cj.enableCollision = false;
                cj.enablePreprocessing = false;
                cj.massScale = massScale;
                cj.connectedMassScale = 1f;
                configured = true;
            }
            finally
            {
                // The new joint only replaces the old one once fully configured (otherwise the limb would detach).
                if (!configured && cj != null) UnityEngine.Object.DestroyImmediate(cj);
            }
            if (old != null) UnityEngine.Object.DestroyImmediate(old);

            if (spawner != null) SpawnerJoint(spawner) = cj; // EFT will destroy this joint itself (pool)
            jm.UnityJoint = cj;
            jm.Driven = new DrivenJoint
            {
                Joint = cj,
                StartRel = qRel0,
                Anchor = anchor,
                Axis = axis,
                Secondary = sec,
                Third = Vector3.Cross(axis, sec).normalized,
                Flex0Rad = phi0.x,
                LowRelRad = lowRel * BMath.Deg,
                HighRelRad = highRel * BMath.Deg,
            };
            return true;
        }

        /// <summary>Cuts all drives (muscles): only the anatomical limits remain.</summary>
        public static void Relax(BodyModel body)
        {
            foreach (var jm in body.Joints)
            {
                var cj = jm.Driven?.Joint;
                if (cj == null) continue;
                cj.angularXDrive = new JointDrive { useAcceleration = false };
                cj.angularYZDrive = new JointDrive { useAcceleration = false };
            }
        }
    }
}
