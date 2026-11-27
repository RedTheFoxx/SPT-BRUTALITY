using UnityEngine;

namespace Brutality.Anatomy
{
    /// <summary>The 11 rigid segments of the EFT ragdoll (one Rigidbody each).</summary>
    internal enum Seg
    {
        Pelvis = 0,
        Thorax = 1,
        Head = 2,
        LUpperArm = 3,
        LForearm = 4,
        RUpperArm = 5,
        RForearm = 6,
        LThigh = 7,
        LCalf = 8,
        RThigh = 9,
        RCalf = 10,
    }

    /// <summary>The 10 joints. Joint index = child segment index - 1.</summary>
    internal enum JointId
    {
        Lumbar = 0,
        Neck = 1,
        LShoulder = 2,
        LElbow = 3,
        RShoulder = 4,
        RElbow = 5,
        LHip = 6,
        LKnee = 7,
        RHip = 8,
        RKnee = 9,
    }

    internal enum JointKind
    {
        Lumbar,   // thoracolumbar spine (ball-and-socket, "ascending" segment)
        Neck,     // cervical spine + atlanto-occipital (ball-and-socket, "ascending" segment)
        Hip,      // hip / coxofemoral (ball-and-socket, "descending" segment)
        Knee,     // knee / femorotibial (hinge)
        Shoulder, // glenohumeral + shoulder girdle (ball-and-socket, "descending" segment)
        Elbow,    // humeroulnar + radioulnar (hinge + pronation-supination)
    }

    /// <summary>
    /// Anatomical degree of freedom. Convention: dof 0 = flexion(+)/extension(-),
    /// dof 1 = abduction(+)/adduction(-) or lateral tilt (right +) or varus/valgus,
    /// dof 2 = axial rotation.
    /// </summary>
    internal struct DofSpec
    {
        public float MinDeg, MaxDeg;      // reference joint range of motion
        public float NeutralDeg;          // passive equilibrium position (relaxed limb)
        public float PassiveA;            // passive torque at limit (N·m)
        public float PassiveB;            // exponential rate (1/rad)
        public float K0;                  // mid-range passive stiffness (N·m/rad)
        public float Damping;             // passive viscosity (N·m·s/rad)
        public float TPos, TNeg;          // max isometric active torques, + and - directions (N·m)
        public float VMax;                // max shortening angular velocity (rad/s)

        public DofSpec(float min, float max, float neutral, float a, float b, float k0, float damping, float tPos, float tNeg, float vMax)
        {
            MinDeg = min; MaxDeg = max; NeutralDeg = neutral;
            PassiveA = a; PassiveB = b; K0 = k0; Damping = damping;
            TPos = tPos; TNeg = tNeg; VMax = vMax;
        }
    }

    internal sealed class JointSpec
    {
        public JointKind Kind;
        public DofSpec[] Dofs = new DofSpec[3];
    }

    /// <summary>Segmental inertial parameters (de Leva 1996, male), as fractions.</summary>
    internal struct SegmentInertia
    {
        public float MassFraction;   // fraction of body mass
        public float ComFraction;    // CoM position from proximal joint (fraction of length), &lt;0 = Unity auto
        public float RSag, RTrans, RLong; // radii of gyration (fraction of length), &lt;0 = Unity auto

        public SegmentInertia(float mass, float com, float rSag, float rTrans, float rLong)
        {
            MassFraction = mass; ComFraction = com; RSag = rSag; RTrans = rTrans; RLong = rLong;
        }
    }

    internal static class AnatomyData
    {
        public const int SegCount = 11;
        public const int JointCount = 10;

        public static readonly int[] Parent =
        {
            -1,                 // Pelvis
            (int)Seg.Pelvis,    // Thorax
            (int)Seg.Thorax,    // Head
            (int)Seg.Thorax,    // LUpperArm
            (int)Seg.LUpperArm, // LForearm
            (int)Seg.Thorax,    // RUpperArm
            (int)Seg.RUpperArm, // RForearm
            (int)Seg.Pelvis,    // LThigh
            (int)Seg.LThigh,    // LCalf
            (int)Seg.Pelvis,    // RThigh
            (int)Seg.RThigh,    // RCalf
        };

        public static readonly JointKind[] KindOf =
        {
            JointKind.Lumbar, JointKind.Neck,
            JointKind.Shoulder, JointKind.Elbow, JointKind.Shoulder, JointKind.Elbow,
            JointKind.Hip, JointKind.Knee, JointKind.Hip, JointKind.Knee,
        };

        /// <summary>Anatomical side of the joint: -1 left, +1 right, 0 axial.</summary>
        public static readonly int[] SideOf = { 0, 0, -1, -1, 1, 1, -1, -1, 1, 1 };

        public static int ChildSeg(int joint) => joint + 1;
        public static int ParentSeg(int joint) => Parent[joint + 1];

        public static JointId Shoulder(int side) => side < 0 ? JointId.LShoulder : JointId.RShoulder;
        public static JointId Elbow(int side) => side < 0 ? JointId.LElbow : JointId.RElbow;
        public static JointId Hip(int side) => side < 0 ? JointId.LHip : JointId.RHip;
        public static JointId Knee(int side) => side < 0 ? JointId.LKnee : JointId.RKnee;

        /// <summary>
        /// Segmental masses and inertias, de Leva (1996), J. Biomech. 29(9):1223-1230, male values.
        /// Split trunk: pelvis = lower trunk (11.17%) + ½ mid-trunk; thorax = upper trunk (15.96%) + ½ mid-trunk.
        /// Forearm+hand and calf+foot are merged (no distal rigidbody in EFT): CoM and radii of gyration
        /// recalculated via the parallel axis theorem from de Leva's values.
        /// </summary>
        public static readonly SegmentInertia[] Inertia =
        {
            new SegmentInertia(0.1934f, -1f, -1f, -1f, -1f),        // Pelvis (Unity inertia from colliders)
            new SegmentInertia(0.2412f, -1f, -1f, -1f, -1f),        // Thorax
            new SegmentInertia(0.0694f, -1f, -1f, -1f, -1f),        // Head (CoM = center of colliders)
            new SegmentInertia(0.0271f, 0.5772f, 0.285f, 0.269f, 0.158f), // Upper arm
            new SegmentInertia(0.0223f, 0.68f, 0.44f, 0.43f, 0.12f),       // Forearm + hand
            new SegmentInertia(0.0271f, 0.5772f, 0.285f, 0.269f, 0.158f),
            new SegmentInertia(0.0223f, 0.68f, 0.44f, 0.43f, 0.12f),
            new SegmentInertia(0.1416f, 0.4095f, 0.329f, 0.329f, 0.149f),  // Thigh
            new SegmentInertia(0.0570f, 0.60f, 0.36f, 0.36f, 0.11f),       // Calf + foot
            new SegmentInertia(0.1416f, 0.4095f, 0.329f, 0.329f, 0.149f),
            new SegmentInertia(0.0570f, 0.60f, 0.36f, 0.36f, 0.11f),
        };

        public static readonly JointSpec[] Specs = BuildSpecs();

        public static JointSpec SpecOf(int joint) => Specs[(int)KindOf[joint]];

        /// <summary>
        /// Ranges of motion: NASA Anthropometric Source Book vol. I (1978), Norkin &amp; White (goniometry),
        /// Panjabi &amp; White (spine). Passive torque: double exponential form (Yoon &amp; Mansour 1982;
        /// Riener &amp; Edrich 1999; Hoang et al. 2005) — near zero mid-range, exponential growth
        /// approaching the joint limit. Max active torques: adult male orders of magnitude (Anderson,
        /// Madigan &amp; Nussbaum 2007; Frey-Law et al. 2012). VMax: typical peak joint angular velocities.
        /// </summary>
        private static JointSpec[] BuildSpecs()
        {
            var specs = new JointSpec[6];

            specs[(int)JointKind.Lumbar] = new JointSpec
            {
                Kind = JointKind.Lumbar,
                Dofs = new[]
                {
                    new DofSpec(-30f, 75f, 5f, 30f, 9f, 8f, 3.0f, 150f, 230f, 8f),   // flexion / extension
                    new DofSpec(-35f, 35f, 0f, 25f, 9f, 6f, 2.5f, 140f, 140f, 8f),   // lateral bending
                    new DofSpec(-40f, 40f, 0f, 20f, 9f, 5f, 2.0f, 70f, 70f, 8f),     // axial rotation
                }
            };
            specs[(int)JointKind.Neck] = new JointSpec
            {
                Kind = JointKind.Neck,
                Dofs = new[]
                {
                    // Single rigid Head bone pivoted at skull base in EFT: keep extension realistic so the head
                    // cannot fold backward onto the upper back or stare straight up when lying on the ground.
                    new DofSpec(-28f, 42f, 8f, 10f, 12f, 2.5f, 0.50f, 30f, 50f, 10f),
                    new DofSpec(-30f, 30f, 0f, 8f, 12f, 2.0f, 0.45f, 35f, 35f, 10f),
                    new DofSpec(-55f, 55f, 0f, 7f, 11f, 1.5f, 0.40f, 20f, 20f, 10f),
                }
            };
            specs[(int)JointKind.Hip] = new JointSpec
            {
                Kind = JointKind.Hip,
                Dofs = new[]
                {
                    new DofSpec(-20f, 125f, 20f, 20f, 9f, 3f, 1.5f, 150f, 230f, 12f),
                    new DofSpec(-30f, 45f, 5f, 18f, 9f, 2.5f, 1.2f, 120f, 110f, 12f),
                    new DofSpec(-40f, 40f, 0f, 12f, 9f, 1.5f, 0.8f, 60f, 60f, 12f),
                }
            };
            specs[(int)JointKind.Knee] = new JointSpec
            {
                Kind = JointKind.Knee,
                Dofs = new[]
                {
                    new DofSpec(-5f, 140f, 25f, 18f, 10f, 1.0f, 0.8f, 130f, 210f, 16f), // flexion + (hamstrings) / extension - (quadriceps)
                    new DofSpec(-6f, 6f, 0f, 25f, 14f, 15f, 1.0f, 0f, 0f, 1f),          // varus/valgus: ligamentous, no muscle
                    new DofSpec(-20f, 20f, 0f, 10f, 10f, 2f, 0.5f, 25f, 25f, 10f),
                }
            };
            specs[(int)JointKind.Shoulder] = new JointSpec
            {
                Kind = JointKind.Shoulder,
                Dofs = new[]
                {
                    // No clavicle joint in EFT: keep flexion below 180° PhysX twist wrap-around and abduction below
                    // the ±90° sagittal gimbal-lock pole, with bounded humeral twist.
                    new DofSpec(-40f, 155f, 6f, 10f, 10f, 1.5f, 0.6f, 80f, 85f, 16f),
                    new DofSpec(-22f, 78f, 16f, 10f, 10f, 1.5f, 0.6f, 75f, 90f, 16f),
                    new DofSpec(-48f, 48f, 0f, 9f, 10f, 1.0f, 0.5f, 45f, 45f, 16f),
                }
            };
            specs[(int)JointKind.Elbow] = new JointSpec
            {
                Kind = JointKind.Elbow,
                Dofs = new[]
                {
                    new DofSpec(-5f, 145f, 18f, 8f, 11f, 0.5f, 0.3f, 75f, 55f, 20f),
                    new DofSpec(-8f, 8f, 0f, 12f, 14f, 6f, 0.4f, 0f, 0f, 1f),
                    new DofSpec(-80f, 80f, 0f, 3f, 10f, 0.3f, 0.15f, 12f, 12f, 20f),
                }
            };
            return specs;
        }
    }
}
