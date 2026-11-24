using System;
using System.Collections.Generic;
using Brutality.Anatomy;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>Rigid ragdoll segment with its anatomical frame (R = right, U = proximal/superior, F = anterior).</summary>
    internal sealed class Segment
    {
        public int Index;
        public Seg Id;
        public Rigidbody Rb;
        public Transform T;
        public RigidbodySpawner Spawner;
        public int Parent;

        public Vector3 RL, UL, FL;   // anatomical frame, local coordinates
        public float Length;
        public float BaseMass;       // anatomical mass without equipment (used for joint balancing)
        public float Mass;
        public Vector3 InertiaDiag;
        public Quaternion InertiaRotLocal;
        public readonly List<Collider> Colliders = new List<Collider>();
        public readonly List<PhysicMaterial> OriginalMaterials = new List<PhysicMaterial>();

        // Original rigidbody parameters (restored at the end, since the ragdoll is pooled by the game).
        public float OrigDrag, OrigAngularDrag, OrigMaxAngularVelocity, OrigSleepThreshold;
        public float OrigMass;
        public Vector3 OrigCenterOfMass, OrigInertiaTensor;
        public Quaternion OrigInertiaRotation;
        public int OrigSolverIterations, OrigSolverVelocityIterations;
        public CollisionDetectionMode OrigCollisionMode;

        // State read before each physics step.
        public Quaternion Q;
        public Vector3 W, V, Com, Pos;

        public Vector3 R => Q * RL;
        public Vector3 U => Q * UL;
        public Vector3 F => Q * FL;
        /// <summary>Distal end (hand, foot, top of skull...).</summary>
        public Vector3 DistalWorld => Pos - U * Length;
    }

    /// <summary>Joint: ISB-like anatomical angles, gradients, muscle activations.</summary>
    internal sealed class JointModel
    {
        public int Index;
        public JointKind Kind;
        public JointSpec Spec;
        public int Side;
        public int P, C;
        public Joint UnityJoint;
        public bool[] InSubtree = new bool[AnatomyData.SegCount];

        public JointFrame Frame;       // geometry frozen at death (frames, hinge axis, reference pose)
        public DrivenJoint Driven;     // driven anatomical PhysX joint (null if conversion failed)

        public Vector3 Phi, Phi0, PhiDot;
        public float SagWeight = 1f;
        public bool HasPrevPhi;
        public readonly float[] Activation = new float[3];
        public readonly float[] Capacity = { 1f, 1f, 1f };
        public readonly float[] Ref = new float[3], RefV = new float[3]; // smoothed reference trajectory (rad, rad/s)
        public Vector3 Ieff = Vector3.one * 0.1f; // effective subtree inertia around the pivot (flex, abd, twist)
    }

    internal sealed class BodyModel
    {
        public readonly Segment[] Segs = new Segment[AnatomyData.SegCount];
        public readonly JointModel[] Joints = new JointModel[AnatomyData.JointCount];
        public float TotalMass;


        // Ribcage geometry (thorax anatomical frame, measured at death)
        // used to prevent upper arms and forearms from penetrating the thorax.
        public float ThoraxHalfShoulderW = 0.18f;
        public float ThoraxChestZ = 0.03f;
        public float ThoraxYBot = -0.05f;
        public float ThoraxYTop = 0.25f;
        public float ThoraxRx = 0.14f;
        public float ThoraxRz = 0.125f;
        public const float ThoraxRyCap = 0.06f;
        private static PhysicMaterial _tissue;


        public Segment this[Seg s] => Segs[(int)s];

        // ------------------------------------------------------------------ construction

        public static BodyModel TryBuild(CorpseRagdoll ragdoll, Player player, DeathContext ctx, out string error)
        {
            error = null;
            var body = new BodyModel();
            try
            {
                if (!body.MapSegments(ragdoll, out error)) return null;
                body.BuildFrames(player);
                body.ApplyAnthropometry(ctx);
                body.BuildJoints(ctx);
                body.ConfigureRigidbodies();
                body.ApplyTissueMaterial();
                return body;
            }
            catch (Exception e)
            {
                error = e.ToString();
                try { body.Restore(); } catch { }
                return null;
            }
        }

        private static Seg? SegFor(EBodyPartColliderType t)
        {
            switch (t)
            {
                case EBodyPartColliderType.Pelvis: return Seg.Pelvis;
                case EBodyPartColliderType.RibcageUp: return Seg.Thorax;
                case EBodyPartColliderType.HeadCommon: return Seg.Head;
                case EBodyPartColliderType.LeftUpperArm: return Seg.LUpperArm;
                case EBodyPartColliderType.LeftForearm: return Seg.LForearm;
                case EBodyPartColliderType.RightUpperArm: return Seg.RUpperArm;
                case EBodyPartColliderType.RightForearm: return Seg.RForearm;
                case EBodyPartColliderType.LeftThigh: return Seg.LThigh;
                case EBodyPartColliderType.LeftCalf: return Seg.LCalf;
                case EBodyPartColliderType.RightThigh: return Seg.RThigh;
                case EBodyPartColliderType.RightCalf: return Seg.RCalf;
            }
            return null;
        }

        private bool MapSegments(CorpseRagdoll ragdoll, out string error)
        {
            error = null;
            var hierarchy = ragdoll._rigidbodySleepHierarchy;
            if (hierarchy != null)
            {
                foreach (var h in hierarchy)
                {
                    if (h?.RigidbodySpawner == null) continue;
                    var s = SegFor(h.BodyPart);
                    if (s.HasValue) Assign(s.Value, h.RigidbodySpawner);
                }
            }
            if (ragdoll._rigidbodySpawners != null)
            {
                foreach (var sp in ragdoll._rigidbodySpawners)
                {
                    if (sp == null) continue;
                    var bpc = sp.GetComponent<BodyPartCollider>();
                    if (bpc == null) continue;
                    var s = SegFor(bpc.BodyPartColliderType);
                    if (s.HasValue && Segs[(int)s.Value] == null) Assign(s.Value, sp);
                }
            }
            for (int i = 0; i < Segs.Length; i++)
            {
                if (Segs[i] == null || Segs[i].Rb == null)
                {
                    error = "missing segment: " + (Seg)i;
                    return false;
                }
            }
            return true;
        }

        private void Assign(Seg s, RigidbodySpawner sp)
        {
            var rb = sp.Rigidbody;
            if (rb == null) return;
            Segs[(int)s] = new Segment
            {
                Index = (int)s,
                Id = s,
                Spawner = sp,
                Rb = rb,
                T = sp.transform,
                Parent = AnatomyData.Parent[(int)s],
                // Copied BEFORE any anthropometric modification: the ragdoll is pooled by EFT and must be
                // returned to the game in the exact state it was borrowed.
                OrigMass = rb.mass,
                OrigCenterOfMass = rb.centerOfMass,
                OrigInertiaTensor = rb.inertiaTensor,
                OrigInertiaRotation = rb.inertiaTensorRotation,
            };
        }

        private static Transform FindDescendant(Transform root, params string[] keys)
        {
            if (root == null) return null;
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            int guard = 0;
            while (queue.Count > 0 && guard++ < 400)
            {
                var t = queue.Dequeue();
                if (t != root)
                {
                    string n = t.name.ToLowerInvariant();
                    foreach (var k in keys)
                        if (n.Contains(k)) return t;
                }
                for (int i = 0; i < t.childCount; i++) queue.Enqueue(t.GetChild(i));
            }
            return null;
        }

        private static bool CollidersCenter(Segment s, out Vector3 center)
        {
            center = Vector3.zero;
            int n = 0;
            foreach (var c in s.T.GetComponentsInChildren<Collider>(false))
            {
                // A disabled collider returns empty bounds centered at world origin.
                if (c == null || c.isTrigger || !c.enabled) continue;
                if (c.GetComponentInParent<Rigidbody>() != s.Rb) continue;
                center += c.bounds.center;
                n++;
            }
            if (n == 0) return false;
            center /= n;
            return true;
        }

        private void BuildFrames(Player player)
        {
            var pelvis = this[Seg.Pelvis].T;
            var thorax = this[Seg.Thorax].T;
            var head = this[Seg.Head].T;

            Vector3 hipL = this[Seg.LThigh].T.position, hipR = this[Seg.RThigh].T.position;
            Vector3 kneeL = this[Seg.LCalf].T.position, kneeR = this[Seg.RCalf].T.position;
            Vector3 shL = this[Seg.LUpperArm].T.position, shR = this[Seg.RUpperArm].T.position;
            Vector3 elL = this[Seg.LForearm].T.position, elR = this[Seg.RForearm].T.position;
            Vector3 neck = head.position;
            Vector3 lumbar = thorax.position;

            Vector3 ankleL = Distal(this[Seg.LCalf], kneeL - hipL, 0.43f, "foot");
            Vector3 ankleR = Distal(this[Seg.RCalf], kneeR - hipR, 0.43f, "foot");
            Vector3 wristL = PalmOr(player != null && player.PlayerBones != null ? player.PlayerBones.LeftPalm : null, this[Seg.LForearm], elL - shL);
            Vector3 wristR = PalmOr(player != null && player.PlayerBones != null ? player.PlayerBones.RightPalm : null, this[Seg.RForearm], elR - shR);

            // Pelvis: hip axis + lumbar direction.
            BMath.OrthoFrame(lumbar - pelvis.position, hipR - hipL, Vector3.forward, out var pr, out var pu, out var pf);
            if ((lumbar - pelvis.position).sqrMagnitude < 1e-4f)
                BMath.OrthoFrame(neck - pelvis.position, hipR - hipL, Vector3.forward, out pr, out pu, out pf);
            SetFrame(this[Seg.Pelvis], pr, pu, pf, Mathf.Max(0.1f, (lumbar - pelvis.position).magnitude));

            // Thorax: shoulder axis + cervical direction.
            BMath.OrthoFrame(neck - lumbar, shR - shL, pf, out var tr, out var tu, out var tf);
            SetFrame(this[Seg.Thorax], tr, tu, tf, Mathf.Max(0.2f, (neck - lumbar).magnitude));


            // Thoracic envelope measured on the actual skeleton (prevents upper arm / forearm ↔ thorax penetration).
            Vector3 shMid = 0.5f * (shL + shR);
            ThoraxHalfShoulderW = Mathf.Clamp(0.5f * Mathf.Abs(Vector3.Dot(shR - shL, tr)), 0.14f, 0.24f);
            float shY = Vector3.Dot(shMid - lumbar, tu);
            float shZ = Vector3.Dot(shMid - lumbar, tf);
            if (CollidersCenter(this[Seg.Thorax], out var tc))
                ThoraxChestZ = Mathf.Clamp(Vector3.Dot(tc - lumbar, tf), shZ - 0.04f, shZ + 0.08f);
            else
                ThoraxChestZ = shZ + 0.03f;
            ThoraxYBot = -0.05f;
            ThoraxYTop = Mathf.Max(0.10f, shY - 0.04f);
            ThoraxRx = 0.78f * ThoraxHalfShoulderW;
            ThoraxRz = Mathf.Clamp(0.72f * ThoraxHalfShoulderW, 0.11f, 0.15f);
            // Head: toward cranial colliders center; anterior from eye collider if present.
            Vector3 headTop;
            if (CollidersCenter(this[Seg.Head], out var hc) && (hc - neck).sqrMagnitude > 1e-4f)
                headTop = neck + (hc - neck).normalized * 0.22f;
            else
                headTop = neck + tu * 0.22f;
            Vector3 headFwd = tf;
            var eyes = FindColliderOfType(player, EBodyPartColliderType.Eyes);
            if (eyes != null && eyes.enabled && eyes.gameObject.activeInHierarchy) headFwd = eyes.bounds.center - neck;
            // R = U × F (Unity convention: Cross(up, forward) = right).
            BMath.OrthoFrame(headTop - neck, Vector3.Cross(headTop - neck, headFwd), headFwd, out var hr, out var hu, out var hf);
            SetFrame(this[Seg.Head], hr, hu, hf, 0.22f);

            // Limbs: U = proximal direction.
            LimbFrame(this[Seg.LThigh], hipL, kneeL, pr, pf);
            LimbFrame(this[Seg.RThigh], hipR, kneeR, pr, pf);
            LimbFrame(this[Seg.LCalf], kneeL, ankleL, pr, pf);
            LimbFrame(this[Seg.RCalf], kneeR, ankleR, pr, pf);
            LimbFrame(this[Seg.LUpperArm], shL, elL, tr, tf);
            LimbFrame(this[Seg.RUpperArm], shR, elR, tr, tf);
            LimbFrame(this[Seg.LForearm], elL, wristL, tr, tf);
            LimbFrame(this[Seg.RForearm], elR, wristR, tr, tf);
        }

        private static Collider FindColliderOfType(Player player, EBodyPartColliderType type)
        {
            try
            {
                var arr = player != null && player.PlayerBones != null ? player.PlayerBones.BodyPartColliders : null;
                if (arr == null) return null;
                foreach (var c in arr)
                    if (c != null && c.BodyPartColliderType == type && c.Collider != null) return c.Collider;
            }
            catch { }
            return null;
        }

        private Vector3 Distal(Segment s, Vector3 proximalDir, float defaultLen, string key)
        {
            var t = FindDescendant(s.T, key);
            if (t != null && (t.position - s.T.position).sqrMagnitude > 0.01f) return t.position;
            if (CollidersCenter(s, out var c) && (c - s.T.position).sqrMagnitude > 1e-3f)
                return s.T.position + (c - s.T.position) * 2f;
            return s.T.position + BMath.SafeNormalize(proximalDir, Vector3.down) * defaultLen;
        }

        private Vector3 PalmOr(Transform palm, Segment forearm, Vector3 upperDir)
        {
            if (palm != null && (palm.position - forearm.T.position).sqrMagnitude > 0.01f) return palm.position;
            return Distal(forearm, upperDir, 0.27f, "palm");
        }

        private static void LimbFrame(Segment s, Vector3 proximal, Vector3 distal, Vector3 bodyRight, Vector3 bodyFwd)
        {
            Vector3 up = proximal - distal;
            BMath.OrthoFrame(up, bodyRight, bodyFwd, out var r, out var u, out var f);
            SetFrame(s, r, u, f, Mathf.Max(0.08f, up.magnitude));
        }

        private static void SetFrame(Segment s, Vector3 r, Vector3 u, Vector3 f, float length)
        {
            Quaternion inv = Quaternion.Inverse(s.T.rotation);
            s.RL = (inv * r).normalized;
            s.UL = (inv * u).normalized;
            s.FL = (inv * f).normalized;
            s.Length = length;
        }

        // ------------------------------------------------------------------ anthropometry

        private void ApplyAnthropometry(DeathContext ctx)
        {
            float M = ctx.BodyMassKg;
            TotalMass = 0f;
            for (int i = 0; i < Segs.Length; i++)
            {
                var s = Segs[i];
                var p = AnatomyData.Inertia[i];
                float baseMass = M * p.MassFraction;
                s.BaseMass = baseMass;
                float m = baseMass;
                if (s.Id == Seg.Thorax) m += ctx.GearThoraxKg + ctx.GearBackKg;
                if (s.Id == Seg.Head) m += ctx.GearHeadKg;
                s.Mass = m;
                s.Rb.mass = m;
                TotalMass += m;

                if (p.ComFraction > 0f)
                {
                    // CoM and principal tensor along the longitudinal axis (de Leva 1996).
                    Vector3 comWorld = s.T.position - (s.T.rotation * s.UL) * (s.Length * p.ComFraction);
                    s.Rb.centerOfMass = s.T.InverseTransformPoint(comWorld);
                    float L = s.Length;
                    var diag = new Vector3(
                        m * Sq(p.RTrans * L),  // mediolateral axis (R)
                        m * Sq(p.RLong * L),   // longitudinal axis (U)
                        m * Sq(p.RSag * L));   // anteroposterior axis (F)
                    diag = Vector3.Max(diag, Vector3.one * 1e-4f);
                    s.Rb.inertiaTensorRotation = Quaternion.LookRotation(s.FL, s.UL);
                    s.Rb.inertiaTensor = diag;
                }
                else if (s.Id == Seg.Head)
                {
                    if (CollidersCenter(s, out var c)) s.Rb.centerOfMass = s.T.InverseTransformPoint(c);
                }
                else if (s.Id == Seg.Thorax && (ctx.GearBackKg > 0.1f || ctx.GearThoraxKg > 0.1f))
                {
                    // Backpack shifts trunk CoM backward, armor plates slightly forward.
                    Vector3 com = s.Rb.centerOfMass;
                    com += -s.FL * (0.16f * ctx.GearBackKg / m) + s.FL * (0.06f * ctx.GearThoraxKg / m);
                    s.Rb.centerOfMass = com;
                }

                // Safeguard: degenerate inertias (disabled colliders).
                Vector3 it = s.Rb.inertiaTensor;
                if (it.x < 1e-4f || it.y < 1e-4f || it.z < 1e-4f || float.IsNaN(it.x))
                {
                    float r = Mathf.Max(0.05f, s.Length * 0.3f);
                    s.Rb.inertiaTensor = Vector3.one * (m * r * r);
                    s.Rb.inertiaTensorRotation = Quaternion.identity;
                }
                s.InertiaDiag = s.Rb.inertiaTensor;
                s.InertiaRotLocal = s.Rb.inertiaTensorRotation;
            }
        }

        private static float Sq(float x) => x * x;

        // ------------------------------------------------------------------ joints

        private void BuildJoints(DeathContext ctx)
        {
            for (int j = 0; j < Joints.Length; j++)
            {
                int c = AnatomyData.ChildSeg(j);
                int p = AnatomyData.ParentSeg(j);
                var jm = new JointModel
                {
                    Index = j,
                    Kind = AnatomyData.KindOf[j],
                    Spec = AnatomyData.SpecOf(j),
                    Side = AnatomyData.SideOf[j],
                    P = p,
                    C = c,
                };
                for (int s = 0; s < Segs.Length; s++)
                {
                    int k = s;
                    while (k >= 0 && k != c) k = AnatomyData.Parent[k];
                    jm.InSubtree[s] = k == c;
                }
                var sp = Segs[p];
                var sc = Segs[c];
                Quaternion qP = sp.T.rotation, qC = sc.T.rotation;
                var frame = new JointFrame
                {
                    Kind = jm.Kind,
                    Side = jm.Side,
                    PR = sp.RL, PU = sp.UL, PF = sp.FL,
                    CR = sc.RL, CU = sc.UL,
                    RelRest = Quaternion.Inverse(qP) * qC,
                    FlexMid = 0.5f * (jm.Spec.Dofs[0].MinDeg + jm.Spec.Dofs[0].MaxDeg) * BMath.Deg,
                };
                if (jm.Kind == JointKind.Knee || jm.Kind == JointKind.Elbow)
                    frame.HingeAxisP = JointKinematics.HingeAxis(jm.Kind, qP, sp.RL, sp.UL, sp.FL, qC, sc.UL);
                if (jm.Kind == JointKind.Lumbar || jm.Kind == JointKind.Neck)
                    frame.TwistOffset = JointKinematics.AbsoluteTwist(qP, sp.RL, qC, sc.RL, sc.UL);
                jm.Frame = frame;

                jm.Phi = Evaluate(jm, qP, qC, out jm.SagWeight);
                jm.Phi0 = jm.Phi;

                jm.UnityJoint = sc.T.GetComponent<Joint>();
                if (jm.UnityJoint != null && jm.UnityJoint.connectedBody != sp.Rb)
                    Plugin.LogDebug($"  unexpected hierarchy: {sc.Id} connected to {(jm.UnityJoint.connectedBody != null ? jm.UnityJoint.connectedBody.name : "nothing")} (expected {sp.Id})");
                Joints[j] = jm;
            }
        }

        private void ConfigureRigidbodies()
        {
            int it = Settings.SolverIterations.Value;
            foreach (var s in Segs)
            {
                var rb = s.Rb;
                s.OrigDrag = rb.drag;
                s.OrigAngularDrag = rb.angularDrag;
                s.OrigMaxAngularVelocity = rb.maxAngularVelocity;
                s.OrigSleepThreshold = rb.sleepThreshold;
                s.OrigSolverIterations = rb.solverIterations;
                s.OrigSolverVelocityIterations = rb.solverVelocityIterations;

                s.OrigCollisionMode = rb.collisionDetectionMode;
                // Continuous speculative detection: fast limbs no longer tunnel through thin walls and floors.
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                rb.maxAngularVelocity = 18f;
                rb.solverIterations = Mathf.Max(rb.solverIterations, it);
                rb.solverVelocityIterations = Mathf.Max(rb.solverVelocityIterations, 4);
                rb.sleepThreshold = 0f;
                rb.drag = Mathf.Min(rb.drag, 0.05f);
                rb.angularDrag = Mathf.Min(rb.angularDrag, 0.05f);
                rb.WakeUp();
            }
        }

        private void ApplyTissueMaterial()
        {
            if (_tissue == null)
            {
                _tissue = new PhysicMaterial("BrutalityTissue")
                {
                    bounciness = 0.02f,
                    bounceCombine = PhysicMaterialCombine.Minimum,
                    frictionCombine = PhysicMaterialCombine.Average,
                };
            }
            float fr = Settings.GroundFriction.Value;
            _tissue.dynamicFriction = fr;
            _tissue.staticFriction = Mathf.Min(1.5f, fr * 1.15f);

            foreach (var s in Segs)
            {
                foreach (var c in s.T.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null || c.isTrigger) continue;
                    if (c.GetComponentInParent<Rigidbody>() != s.Rb) continue;
                    s.Colliders.Add(c);
                    s.OriginalMaterials.Add(c.sharedMaterial);
                    c.sharedMaterial = _tissue;
                }
            }
        }

        /// <summary>Restores everything belonging to the pooled object (colliders, joints, rigidbodies).</summary>
        public void Restore()
        {
            foreach (var s in Segs)
            {
                if (s == null) continue;
                for (int i = 0; i < s.Colliders.Count; i++)
                {
                    var c = s.Colliders[i];
                    if (c != null) c.sharedMaterial = s.OriginalMaterials[i];
                }
                s.Colliders.Clear();
                s.OriginalMaterials.Clear();
                if (s.Rb != null)
                {
                    s.Rb.mass = s.OrigMass;
                    s.Rb.centerOfMass = s.OrigCenterOfMass;
                    s.Rb.inertiaTensor = s.OrigInertiaTensor;
                    s.Rb.inertiaTensorRotation = s.OrigInertiaRotation;
                    s.Rb.drag = s.OrigDrag;
                    s.Rb.angularDrag = s.OrigAngularDrag;
                    s.Rb.maxAngularVelocity = s.OrigMaxAngularVelocity > 0f ? s.OrigMaxAngularVelocity : 7f;
                    s.Rb.sleepThreshold = s.OrigSleepThreshold;
                    if (s.OrigSolverIterations > 0) s.Rb.solverIterations = s.OrigSolverIterations;
                    if (s.OrigSolverVelocityIterations > 0) s.Rb.solverVelocityIterations = s.OrigSolverVelocityIterations;
                    if (!s.Rb.isKinematic) s.Rb.collisionDetectionMode = s.OrigCollisionMode;
                }
            }
        }

        // ------------------------------------------------------------------ kinematics

        public bool IsAlive
        {
            get
            {
                foreach (var s in Segs)
                    if (s.Rb == null || s.Rb.isKinematic) return false;
                return true;
            }
        }

        public void ReadState()
        {
            foreach (var s in Segs)
            {
                var rb = s.Rb;
                s.Q = rb.rotation;
                s.W = rb.angularVelocity;
                s.V = rb.velocity;
                s.Pos = rb.position;
                s.Com = rb.worldCenterOfMass;
            }
        }

        /// <summary>Anatomical angles (rad): x = flexion, y = abduction/lateral tilt/varus, z = axial rotation.</summary>
        public Vector3 Evaluate(JointModel jm, Quaternion qP, Quaternion qC, out float sagWeight)
        {
            return JointKinematics.Evaluate(jm.Frame, qP, qC, out sagWeight);
        }

        /// <summary>Updates anatomical angles and angular velocities (filtered time derivative).</summary>
        public void ComputeKinematics(float dt)
        {
            float invDt = dt > 1e-5f ? 1f / dt : 0f;
            foreach (var jm in Joints)
            {
                var sp = Segs[jm.P];
                var sc = Segs[jm.C];
                Vector3 prev = jm.Phi;
                Vector3 phi = Evaluate(jm, sp.Q, sc.Q, out float sagWeight);
                jm.Phi = phi;
                jm.SagWeight = sagWeight;
                if (jm.HasPrevPhi && invDt > 0f)
                {
                    // Unwrapped flexion: direct difference; abduction / twist: difference modulo 2π.
                    float vx = Mathf.Clamp((phi.x - prev.x) * invDt, -25f, 25f);
                    // Near ±90° abduction, sagittal flexion Atan2(z, -y) is ill-conditioned: damp pole spikes.
                    if (jm.Kind == JointKind.Shoulder || jm.Kind == JointKind.Hip)
                        vx *= BMath.SmoothStep(0.08f, 0.35f, sagWeight);
                    float vy = Mathf.Clamp(BMath.WrapPi(phi.y - prev.y) * invDt, -25f, 25f);
                    float vz = Mathf.Clamp(BMath.WrapPi(phi.z - prev.z) * invDt, -25f, 25f);
                    jm.PhiDot = new Vector3(vx, vy, vz);
                }
                else
                {
                    jm.PhiDot = Vector3.zero;
                    jm.HasPrevPhi = true;
                }

                // Effective subtree inertia around the joint pivot along the 3 anatomical axes (kg·m²),
                // used by torque drives (DriveController) to preserve the same angular stiffness
                // as a full multi-body model (parallel axis + subtree, not just the segment's CoM inertia).
                var dj = jm.Driven;
                if (dj != null)
                {
                    Vector3 pivot = sc.Pos + sc.Q * dj.Anchor;
                    Vector3 n0 = sc.Q * dj.Axis;
                    Vector3 n1 = sc.Q * dj.Secondary;
                    Vector3 n2 = sc.Q * dj.Third;
                    float i0 = 0f, i1 = 0f, i2 = 0f;
                    for (int s = 0; s < Segs.Length; s++)
                    {
                        if (!jm.InSubtree[s]) continue;
                        var sub = Segs[s];
                        Vector3 r = sub.Com - pivot;
                        Quaternion qI = sub.Q * sub.InertiaRotLocal;
                        float m = sub.Mass;
                        i0 += m * Vector3.Cross(r, n0).sqrMagnitude + BMath.MomentAboutAxis(qI, sub.InertiaDiag, n0);
                        i1 += m * Vector3.Cross(r, n1).sqrMagnitude + BMath.MomentAboutAxis(qI, sub.InertiaDiag, n1);
                        i2 += m * Vector3.Cross(r, n2).sqrMagnitude + BMath.MomentAboutAxis(qI, sub.InertiaDiag, n2);
                    }
                    jm.Ieff = new Vector3(Mathf.Max(0.004f, i0), Mathf.Max(0.004f, i1), Mathf.Max(0.004f, i2));
                }
            }
        }
        // ------------------------------------------------------------------ global orientation

        /// <summary>+1 supine (face up), -1 prone (face down), 0 on side.</summary>
        public float SupineScore => Vector3.Dot(this[Seg.Thorax].F, Vector3.up);

        /// <summary>Upward side when the body is on its side: +1 right side up.</summary>
        public float SideUpScore => Vector3.Dot(this[Seg.Thorax].R, Vector3.up);

        public float MaxSpeed
        {
            get
            {
                float m = 0f;
                foreach (var s in Segs) m = Mathf.Max(m, s.V.magnitude);
                return m;
            }
        }

        public float MaxAngularSpeed
        {
            get
            {
                float m = 0f;
                foreach (var s in Segs) m = Mathf.Max(m, s.W.magnitude);
                return m;
            }
        }


        public Segment SegmentForRigidbody(Rigidbody rb)
        {
            if (rb == null) return null;
            foreach (var s in Segs) if (s.Rb == rb) return s;
            return null;
        }

        public Segment SegmentForBone(Transform bone)
        {
            if (bone == null) return null;
            foreach (var s in Segs) if (s.T == bone) return s;
            return null;
        }

        public Segment SegmentForCollider(EBodyPartColliderType t)
        {
            switch (t)
            {
                case EBodyPartColliderType.HeadCommon:
                case EBodyPartColliderType.ParietalHead:
                case EBodyPartColliderType.BackHead:
                case EBodyPartColliderType.Eyes:
                case EBodyPartColliderType.Ears:
                case EBodyPartColliderType.Jaw:
                    return this[Seg.Head];
                case EBodyPartColliderType.NeckFront:
                case EBodyPartColliderType.NeckBack:
                case EBodyPartColliderType.RibcageUp:
                case EBodyPartColliderType.LeftSideChestUp:
                case EBodyPartColliderType.RightSideChestUp:
                case EBodyPartColliderType.SpineTop:
                    return this[Seg.Thorax];
                case EBodyPartColliderType.RibcageLow:
                case EBodyPartColliderType.LeftSideChestDown:
                case EBodyPartColliderType.RightSideChestDown:
                case EBodyPartColliderType.SpineDown:
                case EBodyPartColliderType.Pelvis:
                case EBodyPartColliderType.PelvisBack:
                    return this[Seg.Pelvis];
                case EBodyPartColliderType.LeftUpperArm: return this[Seg.LUpperArm];
                case EBodyPartColliderType.LeftForearm: return this[Seg.LForearm];
                case EBodyPartColliderType.RightUpperArm: return this[Seg.RUpperArm];
                case EBodyPartColliderType.RightForearm: return this[Seg.RForearm];
                case EBodyPartColliderType.LeftThigh: return this[Seg.LThigh];
                case EBodyPartColliderType.LeftCalf: return this[Seg.LCalf];
                case EBodyPartColliderType.RightThigh: return this[Seg.RThigh];
                case EBodyPartColliderType.RightCalf: return this[Seg.RCalf];
            }
            return this[Seg.Thorax];
        }
    }
}
