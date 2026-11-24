using UnityEngine;

namespace Brutality.Anatomy
{
    /// <summary>Les 11 segments rigides du ragdoll EFT (un Rigidbody chacun).</summary>
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

    /// <summary>Les 10 articulations. Indice de l'articulation = indice du segment enfant - 1.</summary>
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
        Lumbar,   // rachis thoraco-lombaire (rotule, segment « montant »)
        Neck,     // rachis cervical + atlanto-occipital (rotule, segment « montant »)
        Hip,      // coxo-fémorale (rotule, segment « descendant »)
        Knee,     // fémoro-tibiale (charnière)
        Shoulder, // gléno-humérale + ceinture scapulaire (rotule, segment « descendant »)
        Elbow,    // huméro-ulnaire + radio-ulnaire (charnière + prono-supination)
    }

    /// <summary>
    /// Degré de liberté anatomique. Convention : dof 0 = flexion(+)/extension(-),
    /// dof 1 = abduction(+)/adduction(-) ou inclinaison latérale (droite +) ou varus/valgus,
    /// dof 2 = rotation axiale.
    /// </summary>
    internal struct DofSpec
    {
        public float MinDeg, MaxDeg;      // amplitude articulaire de référence
        public float NeutralDeg;          // position d'équilibre passif (membre relâché)
        public float PassiveA;            // couple passif à la butée (N·m)
        public float PassiveB;            // taux exponentiel (1/rad)
        public float K0;                  // raideur passive de milieu de course (N·m/rad)
        public float Damping;             // viscosité passive (N·m·s/rad)
        public float TPos, TNeg;          // couples actifs max isométriques, sens + et - (N·m)
        public float VMax;                // vitesse angulaire max de raccourcissement (rad/s)

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

    /// <summary>Paramètres inertiels segmentaires (de Leva 1996, homme), en fractions.</summary>
    internal struct SegmentInertia
    {
        public float MassFraction;   // fraction de la masse corporelle
        public float ComFraction;    // position du CdM depuis l'articulation proximale (fraction de longueur), &lt;0 = auto Unity
        public float RSag, RTrans, RLong; // rayons de giration (fraction de longueur), &lt;0 = auto Unity

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

        /// <summary>Côté anatomique de l'articulation : -1 gauche, +1 droite, 0 axial.</summary>
        public static readonly int[] SideOf = { 0, 0, -1, -1, 1, 1, -1, -1, 1, 1 };

        public static int ChildSeg(int joint) => joint + 1;
        public static int ParentSeg(int joint) => Parent[joint + 1];

        public static JointId Shoulder(int side) => side < 0 ? JointId.LShoulder : JointId.RShoulder;
        public static JointId Elbow(int side) => side < 0 ? JointId.LElbow : JointId.RElbow;
        public static JointId Hip(int side) => side < 0 ? JointId.LHip : JointId.RHip;
        public static JointId Knee(int side) => side < 0 ? JointId.LKnee : JointId.RKnee;

        /// <summary>
        /// Masses et inerties segmentaires, de Leva (1996), J. Biomech. 29(9):1223-1230, valeurs masculines.
        /// Tronc scindé : bassin = tronc inférieur (11,17 %) + ½ tronc moyen ; thorax = tronc supérieur (15,96 %) + ½ tronc moyen.
        /// Avant-bras+main et jambe+pied sont fusionnés (pas de rigidbody distal dans EFT) : CdM et rayons
        /// de giration recalculés par le théorème de Huygens à partir des valeurs de de Leva.
        /// </summary>
        public static readonly SegmentInertia[] Inertia =
        {
            new SegmentInertia(0.1934f, -1f, -1f, -1f, -1f),        // Pelvis (inertie Unity depuis colliders)
            new SegmentInertia(0.2412f, -1f, -1f, -1f, -1f),        // Thorax
            new SegmentInertia(0.0694f, -1f, -1f, -1f, -1f),        // Head (CdM = centre des colliders)
            new SegmentInertia(0.0271f, 0.5772f, 0.285f, 0.269f, 0.158f), // Bras
            new SegmentInertia(0.0223f, 0.68f, 0.44f, 0.43f, 0.12f),       // Avant-bras + main
            new SegmentInertia(0.0271f, 0.5772f, 0.285f, 0.269f, 0.158f),
            new SegmentInertia(0.0223f, 0.68f, 0.44f, 0.43f, 0.12f),
            new SegmentInertia(0.1416f, 0.4095f, 0.329f, 0.329f, 0.149f),  // Cuisse
            new SegmentInertia(0.0570f, 0.60f, 0.36f, 0.36f, 0.11f),       // Jambe + pied
            new SegmentInertia(0.1416f, 0.4095f, 0.329f, 0.329f, 0.149f),
            new SegmentInertia(0.0570f, 0.60f, 0.36f, 0.36f, 0.11f),
        };

        public static readonly JointSpec[] Specs = BuildSpecs();

        public static JointSpec SpecOf(int joint) => Specs[(int)KindOf[joint]];

        /// <summary>
        /// Amplitudes : NASA Anthropometric Source Book vol. I (1978), Norkin &amp; White (goniométrie),
        /// Panjabi &amp; White (rachis). Couple passif : forme double exponentielle (Yoon &amp; Mansour 1982 ;
        /// Riener &amp; Edrich 1999 ; Hoang et al. 2005) — quasi nul en milieu de course, croissance exponentielle
        /// à l'approche de la butée. Couples actifs max : ordres de grandeur adultes masculins (Anderson,
        /// Madigan &amp; Nussbaum 2007 ; Frey-Law et al. 2012). VMax : vitesses articulaires maximales typiques.
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
                    new DofSpec(-35f, 35f, 0f, 25f, 9f, 6f, 2.5f, 140f, 140f, 8f),   // inclinaison latérale
                    new DofSpec(-40f, 40f, 0f, 20f, 9f, 5f, 2.0f, 70f, 70f, 8f),     // rotation axiale
                }
            };
            specs[(int)JointKind.Neck] = new JointSpec
            {
                Kind = JointKind.Neck,
                Dofs = new[]
                {
                    new DofSpec(-60f, 50f, 10f, 6f, 10f, 1.5f, 0.35f, 30f, 50f, 10f),
                    new DofSpec(-45f, 45f, 0f, 5f, 10f, 1.2f, 0.30f, 35f, 35f, 10f),
                    new DofSpec(-80f, 80f, 0f, 4f, 10f, 0.8f, 0.25f, 20f, 20f, 10f),
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
                    new DofSpec(-5f, 140f, 25f, 18f, 10f, 1.0f, 0.8f, 130f, 210f, 16f), // flexion + (ischio-jambiers) / extension - (quadriceps)
                    new DofSpec(-6f, 6f, 0f, 25f, 14f, 15f, 1.0f, 0f, 0f, 1f),          // varus/valgus : ligamentaire, aucun muscle
                    new DofSpec(-20f, 20f, 0f, 10f, 10f, 2f, 0.5f, 25f, 25f, 10f),
                }
            };
            specs[(int)JointKind.Shoulder] = new JointSpec
            {
                Kind = JointKind.Shoulder,
                Dofs = new[]
                {
                    new DofSpec(-55f, 180f, 15f, 8f, 9f, 1.0f, 0.6f, 80f, 85f, 16f),
                    new DofSpec(-28f, 90f, 10f, 8f, 9f, 1.0f, 0.6f, 75f, 90f, 16f),
                    new DofSpec(-80f, 80f, 0f, 6f, 9f, 0.6f, 0.4f, 45f, 45f, 16f),
                }
            };
            specs[(int)JointKind.Elbow] = new JointSpec
            {
                Kind = JointKind.Elbow,
                Dofs = new[]
                {
                    new DofSpec(-5f, 145f, 50f, 8f, 11f, 0.5f, 0.3f, 75f, 55f, 20f),
                    new DofSpec(-8f, 8f, 0f, 12f, 14f, 6f, 0.4f, 0f, 0f, 1f),
                    new DofSpec(-80f, 80f, 0f, 3f, 10f, 0.3f, 0.15f, 12f, 12f, 20f),
                }
            };
            return specs;
        }
    }
}
