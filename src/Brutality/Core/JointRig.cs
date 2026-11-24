using System;
using Brutality.Anatomy;
using HarmonyLib;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>Articulation PhysX pilotée : ConfigurableJoint anatomique + repère de pilotage.</summary>
    internal sealed class DrivenJoint
    {
        public ConfigurableJoint Joint;
        public Quaternion StartRel;      // rotation relative parent→enfant au moment de la configuration (pose centrale)
        public Vector3 Anchor;           // ancrage du joint (local enfant)
        public Vector3 Axis, Secondary, Third; // repère orthonormé du joint (local enfant : X flex, Y abd, Z torsion)
        public float Flex0Rad;           // flexion de référence à la configuration (rad)
        public float LowRelRad;          // borne inférieure relative à Flex0Rad (rad, >= -175°)
        public float HighRelRad;         // borne supérieure relative à Flex0Rad (rad, <= +175°)
        public Quaternion LastSolve;     // dernière pose cible résolue (départ à chaud du solveur)
        public bool HasSolve;
    }

    /// <summary>
    /// Remplace les CharacterJoint d'EFT par des ConfigurableJoint anatomiques.
    /// La pose de référence du joint est la pose de la mort. L'axe X est la direction qui augmente la flexion
    /// (gradient angulaire), l'axe Y celle de l'abduction / inclinaison. La butée X reproduit exactement
    /// l'amplitude de flexion (asymétrique), dans le sens mesuré par la sonde PhysX ; les butées Y/Z (cône)
    /// couvrent l'amplitude autour de la pose de la mort. Les butées sont « molles » (ressort amorti) :
    /// la résistance monte au-delà de l'amplitude au lieu d'un arrêt sec (corpus §4 et §10).
    /// Les muscles deviennent des drives PhysX (implicites, donc inconditionnellement stables).
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
                    Plugin.Log.LogWarning($"Brutality : articulation {(JointId)jm.Index} non convertie ({e.Message}).");
                }
            }
            return ok;
        }

        private static bool BuildOne(BodyModel body, JointModel jm)
        {
            var sp = body.Segs[jm.P];
            var sc = body.Segs[jm.C];
            var spec = jm.Spec;

            // Pose de référence du joint = pose de la mort (aucune hypothèse sur l'instant où Unity la capture).
            Quaternion qP = sp.Rb.rotation;
            Quaternion qC = sc.Rb.rotation;
            Quaternion qRel0 = BMath.Normalize(Quaternion.Inverse(qP) * qC);

            // Axes analytiques du joint (local enfant) : insensibles aux singularités de gradients (épaule ±90°).
            Vector3 phi0 = JointKinematics.Evaluate(jm.Frame, Quaternion.identity, qRel0, out _);
            JointKinematics.JointAxes(jm.Frame, qRel0, out Vector3 axis, out Vector3 sec);

            // Butée X exacte et asymétrique : [min - φ0, max - φ0] (sens mesuré par la sonde PhysX).
            float f0 = phi0.x / BMath.Deg;
            float lowRel = Mathf.Min(spec.Dofs[0].MinDeg - f0, -3f);
            float highRel = Mathf.Max(spec.Dofs[0].MaxDeg - f0, 3f);
            lowRel = Mathf.Max(lowRel, -175f);
            highRel = Mathf.Min(highRel, 175f);
            bool invX = PhysxProbe.LimitXInverted ?? false;
            float lowX = invX ? -highRel : lowRel;
            float highX = invX ? -lowRel : highRel;

            // Butées Y/Z (cône PhysX, symétriques) : couvrent l'amplitude autour de la pose de la mort.
            bool isHinge = jm.Kind == JointKind.Knee || jm.Kind == JointKind.Elbow;
            float HalfAround(int d)
            {
                float p = (d == 1 ? phi0.y : phi0.z) / BMath.Deg;
                float min = spec.Dofs[d].MinDeg, max = spec.Dofs[d].MaxDeg;
                float half = Mathf.Max(max - p, p - min);
                float margin = jm.Kind == JointKind.Shoulder && d == 1 ? 5f : 15f;
                return Mathf.Clamp(Mathf.Min(half, 0.5f * (max - min) + margin), 5f, 170f);
            }
            float halfY = HalfAround(1);
            float halfZ = HalfAround(2);

            Joint old = jm.UnityJoint;
            Vector3 anchor = old != null ? old.anchor : Vector3.zero;
            var spawner = sc.T.GetComponent<JointSpawner>();

            // Équilibrage de masse borné (calculé sur les masses anatomiques de base, hors sac/gilet) :
            // évite qu'un massScale extrême (15-30× sur l'épaule équipée) ne fasse basculer tout le thorax
            // sous l'action d'un bras de 2 kg ou n'écrase la raideur des butées molles.
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
                // Charnières (genou, coude) : verrouiller le varus/valgus (Y) évite un cône elliptique 10:1 (8°×80°)
                // source de jitter PhysX ; PhysX bascule sur la contrainte universelle (twist + 1 swing Z).
                cj.angularYMotion = isHinge ? ConfigurableJointMotion.Locked : ConfigurableJointMotion.Limited;
                cj.angularZMotion = ConfigurableJointMotion.Limited;
                cj.lowAngularXLimit = new SoftJointLimit { limit = lowX };
                cj.highAngularXLimit = new SoftJointLimit { limit = highX };
                cj.angularYLimit = new SoftJointLimit { limit = halfY };
                cj.angularZLimit = new SoftJointLimit { limit = halfZ };

                // Butées progressives : raideur issue du couple passif exponentiel, bornée à l'inertie effective
                // pour éviter les raideurs excessives sur petits segments (avant-bras, tibia) et amortie de façon critique.
                float ps = Settings.PassiveStiffness.Value;
                float iTransEff = Mathf.Max(0.01f, sc.InertiaDiag.x * massScale);
                float iLongEff = Mathf.Max(0.005f, sc.InertiaDiag.y * massScale);
                float kx = Mathf.Min(6f * spec.Dofs[0].PassiveA * spec.Dofs[0].PassiveB * ps, 6400f * iTransEff);
                float kyzRaw = isHinge
                    ? 6f * spec.Dofs[2].PassiveA * spec.Dofs[2].PassiveB * ps
                    : 6f * Mathf.Max(spec.Dofs[1].PassiveA * spec.Dofs[1].PassiveB, spec.Dofs[2].PassiveA * spec.Dofs[2].PassiveB) * ps;
                float kyz = Mathf.Min(kyzRaw, 6400f * iLongEff);
                float cx = Mathf.Max(0.06f * kx, 1.8f * Mathf.Sqrt(kx * iTransEff));
                float cyz = Mathf.Max(0.06f * kyz, 1.8f * Mathf.Sqrt(kyz * iLongEff));
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
                // Le nouveau joint ne remplace l'ancien qu'une fois entièrement configuré (sinon le membre se détacherait).
                if (!configured && cj != null) UnityEngine.Object.DestroyImmediate(cj);
            }
            if (old != null) UnityEngine.Object.DestroyImmediate(old);

            if (spawner != null) SpawnerJoint(spawner) = cj; // EFT détruira ce joint lui-même (pool)
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

        /// <summary>Coupe tous les drives (muscles) : il ne reste que les butées anatomiques.</summary>
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
