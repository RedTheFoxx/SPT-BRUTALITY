using Brutality.Anatomy;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>Repère et paramètres géométriques d'une articulation, figés au moment de la mort.</summary>
    internal struct JointFrame
    {
        public JointKind Kind;
        public int Side;               // -1 gauche, +1 droite, 0 axial
        public Vector3 PR, PU, PF;     // repère anatomique du parent (local parent)
        public Vector3 CR, CU;         // axes droit et proximal/supérieur de l'enfant (local enfant)
        public Vector3 HingeAxisP;     // axe de flexion (genou/coude), local parent
        public Quaternion RelRest;     // inv(qP0) * qC0
        public float TwistOffset;      // rotation axiale absolue à la mort (rachis, cou)
        public float FlexMid;          // milieu de l'amplitude de flexion (rad), pour le déroulement
    }

    /// <summary>
    /// Angles articulaires anatomiques (inspirés des repères ISB, Wu et al. 2002/2005), en radians :
    /// x = flexion(+)/extension(-), y = abduction(+) / inclinaison droite(+) / varus, z = rotation axiale.
    /// Fonctions pures (aucune dépendance au moteur) : testées hors jeu.
    /// </summary>
    internal static class JointKinematics
    {
        public static Vector3 Evaluate(in JointFrame f, Quaternion qP, Quaternion qC, out float sagWeight)
        {
            float flex, side, twist;
            Quaternion delta = Quaternion.Inverse(f.RelRest) * (Quaternion.Inverse(qP) * qC);
            switch (f.Kind)
            {
                case JointKind.Hip:
                case JointKind.Shoulder:
                {
                    // Segment « descendant » : direction distale de l'enfant dans le repère du parent.
                    Vector3 R = qP * f.PR, U = qP * f.PU, F = qP * f.PF;
                    Vector3 d = qC * -f.CU;
                    float x = Vector3.Dot(d, R), y = Vector3.Dot(d, U), z = Vector3.Dot(d, F);
                    flex = Mathf.Atan2(z, -y);
                    sagWeight = Mathf.Sqrt(y * y + z * z);
                    side = BMath.SafeAsin(f.Side * x);
                    twist = BMath.TwistAngle(delta, f.CU);
                    break;
                }
                case JointKind.Lumbar:
                case JointKind.Neck:
                {
                    // Segment « montant » : axe supérieur de l'enfant dans le repère du parent.
                    Vector3 R = qP * f.PR, U = qP * f.PU, F = qP * f.PF;
                    Vector3 d = qC * f.CU;
                    float x = Vector3.Dot(d, R), y = Vector3.Dot(d, U), z = Vector3.Dot(d, F);
                    flex = Mathf.Atan2(z, y);
                    sagWeight = Mathf.Sqrt(y * y + z * z);
                    side = BMath.SafeAsin(x);
                    twist = BMath.WrapPi(f.TwistOffset + BMath.TwistAngle(delta, f.CU));
                    break;
                }
                default:
                {
                    // Charnière : angle entre directions distales autour de l'axe de flexion.
                    Vector3 a = qP * f.HingeAxisP;
                    Vector3 dP = qP * -f.PU;
                    Vector3 dC = qC * -f.CU;
                    flex = BMath.SignedAngleOnPlane(dP, dC, a);
                    float s = Vector3.Dot(dC, a);
                    sagWeight = Mathf.Sqrt(Mathf.Max(0f, 1f - s * s));
                    side = BMath.SafeAsin(s);
                    twist = BMath.TwistAngle(delta, f.CU);
                    break;
                }
            }
            // Déroulement autour du milieu de l'amplitude : la discontinuité ±180° est placée à l'opposé de la zone
            // physiologique (ex. bras au-dessus de la tête légèrement en arrière = 181° et non -179°).
            flex = f.FlexMid + BMath.WrapPi(flex - f.FlexMid);
            return new Vector3(flex, side, twist);
        }

        private static readonly Vector3[] UnitAxes = { new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f) };

        /// <summary>
        /// Gradients angulaires numériques des trois angles par rapport à une petite rotation de l'enfant,
        /// exprimés dans le repère du parent (rotation relative qRel). g[d] · ω = dφd/dt.
        /// </summary>
        public static void Gradients(in JointFrame f, Quaternion qRel, out Vector3 phi, out Vector3 g0, out Vector3 g1, out Vector3 g2)
        {
            const float eps = 0.01f;
            phi = Evaluate(f, Quaternion.identity, qRel, out _);
            g0 = g1 = g2 = new Vector3(0f, 0f, 0f);
            for (int k = 0; k < 3; k++)
            {
                Quaternion q2 = Quaternion.AngleAxis(eps * Mathf.Rad2Deg, UnitAxes[k]) * qRel;
                Vector3 p2 = Evaluate(f, Quaternion.identity, q2, out _);
                g0[k] = BMath.WrapPi(p2.x - phi.x) / eps;
                g1[k] = BMath.WrapPi(p2.y - phi.y) / eps;
                g2[k] = BMath.WrapPi(p2.z - phi.z) / eps;
            }
        }

        /// <summary>
        /// Résout la rotation relative parent→enfant qui produit les angles anatomiques demandés
        /// (moindres carrés amortis de Levenberg-Marquardt sur le jacobien numérique, départ à chaud).
        /// Insensible aux conventions de signe : il suffit qu'Evaluate soit cohérent.
        /// </summary>
        public static Quaternion SolvePose(in JointFrame f, Quaternion qStart, Vector3 target, int iterations, out float residualRad)
        {
            Quaternion q = BMath.Normalize(qStart);
            residualRad = 0f;
            for (int it = 0; it < iterations; it++)
            {
                Gradients(f, q, out Vector3 phi, out Vector3 g0, out Vector3 g1, out Vector3 g2);
                // Pour la flexion déroulée autour de FlexMid (ex. épaule [-55°, 180°]), on garde la différence directe
                // bornée à ±π afin d'éviter qu'un bras au-delà de 195° ne reparte en arrière par le chemin le plus court.
                float ex = Mathf.Clamp(target.x - phi.x, -BMath.Pi, BMath.Pi);
                Vector3 e = new Vector3(ex, BMath.WrapPi(target.y - phi.y), BMath.WrapPi(target.z - phi.z));
                residualRad = e.magnitude;
                if (residualRad < 1e-3f) break;
                // (GᵀG + λI) ω = Gᵀ e
                float lambda = 0.02f;
                float a00 = g0.x * g0.x + g1.x * g1.x + g2.x * g2.x + lambda;
                float a01 = g0.x * g0.y + g1.x * g1.y + g2.x * g2.y;
                float a02 = g0.x * g0.z + g1.x * g1.z + g2.x * g2.z;
                float a11 = g0.y * g0.y + g1.y * g1.y + g2.y * g2.y + lambda;
                float a12 = g0.y * g0.z + g1.y * g1.z + g2.y * g2.z;
                float a22 = g0.z * g0.z + g1.z * g1.z + g2.z * g2.z + lambda;
                Vector3 b = g0 * e.x + g1 * e.y + g2 * e.z;
                Vector3 w = Solve3(a00, a01, a02, a11, a12, a22, b);
                float m = w.magnitude;
                if (m < 1e-6f || float.IsNaN(m)) break;
                if (m > 0.6f) { w *= 0.6f / m; m = 0.6f; }
                q = BMath.Normalize(Quaternion.AngleAxis(m * Mathf.Rad2Deg, w / m) * q);
            }
            return q;
        }

        /// <summary>Résout un système 3×3 symétrique par la règle de Cramer.</summary>
        private static Vector3 Solve3(float a00, float a01, float a02, float a11, float a12, float a22, Vector3 b)
        {
            float c00 = a11 * a22 - a12 * a12;
            float c01 = a02 * a12 - a01 * a22;
            float c02 = a01 * a12 - a02 * a11;
            float det = a00 * c00 + a01 * c01 + a02 * c02;
            if (Mathf.Abs(det) < 1e-12f) return new Vector3(0f, 0f, 0f);
            float c11 = a00 * a22 - a02 * a02;
            float c12 = a01 * a02 - a00 * a12;
            float c22 = a00 * a11 - a01 * a01;
            float inv = 1f / det;
            return new Vector3(
                (c00 * b.x + c01 * b.y + c02 * b.z) * inv,
                (c01 * b.x + c11 * b.y + c12 * b.z) * inv,
                (c02 * b.x + c12 * b.y + c22 * b.z) * inv);
        }

        /// <summary>
        /// Axe de flexion d'une charnière (local parent), orienté pour que la flexion soit positive :
        /// le genou porte la jambe vers l'arrière, le coude porte l'avant-bras vers l'avant.
        /// </summary>
        public static Vector3 HingeAxis(JointKind kind, Quaternion qP, Vector3 pR, Vector3 pU, Vector3 pF, Quaternion qC, Vector3 cU)
        {
            Vector3 dP = qP * -pU;
            Vector3 dC = qC * -cU;
            Vector3 n;
            if (Vector3.Angle(dP, dC) > 12f)
            {
                // Un genou ou coude nettement plié à la mort (>12°) est toujours en flexion positive :
                // Cross(dP, dC) est orthogonal aux deux segments et donne directement le sens positif (SignedAngleOnPlane > 0),
                // même si l'avant-bras est replié en travers du torse (où pF de l'humérus n'est pas fiable).
                n = Vector3.Cross(dP, dC).normalized;
            }
            else
            {
                n = qP * pR;
                Vector3 flexDir = kind == JointKind.Knee ? -(qP * pF) : qP * pF;
                Vector3 test = Quaternion.AngleAxis(10f, n) * dP;
                if (Vector3.Dot(test - dP, flexDir) < 0f) n = -n;
            }
            return Quaternion.Inverse(qP) * n;
        }

        /// <summary>
        /// Repère local enfant (axis = X flexion, secondary = Y abduction/varus) pour configurer un ConfigurableJoint.
        /// Calculé analytiquement à partir des axes anatomiques (insensible à la singularité d'abduction ±90° de l'épaule).
        /// </summary>
        public static void JointAxes(in JointFrame f, Quaternion qRel0, out Vector3 axis, out Vector3 secondary)
        {
            Vector3 flexP;
            switch (f.Kind)
            {
                case JointKind.Hip:
                case JointKind.Shoulder:
                    flexP = -f.PR;
                    break;
                case JointKind.Lumbar:
                case JointKind.Neck:
                    flexP = f.PR;
                    break;
                default:
                    flexP = f.HingeAxisP;
                    break;
            }
            Quaternion invC = Quaternion.Inverse(qRel0);
            axis = BMath.SafeNormalize(invC * flexP, Vector3.right);

            // Y orthogonal à X et à l'axe longitudinal CU afin que Z = X × Y s'aligne sur la torsion axiale.
            Vector3 sec = Vector3.Cross(f.CU, axis);
            if (sec.sqrMagnitude < 0.01f)
            {
                Vector3 hint = invC * (f.Side != 0 ? f.PF * f.Side : f.PF);
                sec = hint - axis * Vector3.Dot(hint, axis);
            }
            secondary = BMath.SafeNormalize(sec, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.forward);
        }

        /// <summary>Rotation axiale absolue du segment enfant par rapport au parent (rachis, cou).</summary>
        public static float AbsoluteTwist(Quaternion qP, Vector3 pR, Quaternion qC, Vector3 cR, Vector3 cU)
        {
            return BMath.SignedAngleOnPlane(qP * pR, qC * cR, qC * cU);
        }

        /// <summary>
        /// Cinématique inverse analytique à deux segments : angles d'épaule (mêmes définitions qu'Evaluate)
        /// et flexion du coude pour amener la main sur une cible.
        /// </summary>
        public static bool ArmIK(Vector3 shoulder, float upperLen, float foreLen, Vector3 target, int side,
                                 Vector3 thoraxR, Vector3 thoraxU, Vector3 thoraxF,
                                 out float flexDeg, out float abdDeg, out float elbowDeg, out Vector3 upperDir)
        {
            flexDeg = abdDeg = elbowDeg = 0f;
            upperDir = -thoraxU;
            float L1 = Mathf.Max(0.15f, upperLen);
            float L2 = Mathf.Max(0.15f, foreLen);
            Vector3 D = target - shoulder;
            float dist = D.magnitude;
            if (dist < 1e-3f) return false;
            float d = Mathf.Clamp(dist, Mathf.Abs(L1 - L2) + 0.02f, L1 + L2 - 0.01f);
            float cosE = Mathf.Clamp((L1 * L1 + L2 * L2 - d * d) / (2f * L1 * L2), -1f, 1f);
            elbowDeg = 180f - Mathf.Acos(cosE) * Mathf.Rad2Deg;
            float cosA = Mathf.Clamp((L1 * L1 + d * d - L2 * L2) / (2f * L1 * d), -1f, 1f);
            float alpha = Mathf.Acos(cosA);
            Vector3 dn = D / dist;
            // Pôle : le coude part vers le bas et l'extérieur.
            Vector3 pole = -thoraxU + thoraxR * (0.6f * side);
            Vector3 p = pole - dn * Vector3.Dot(pole, dn);
            p = BMath.SafeNormalize(p, thoraxR * side);
            upperDir = dn * Mathf.Cos(alpha) + p * Mathf.Sin(alpha);
            float x = Vector3.Dot(upperDir, thoraxR), y = Vector3.Dot(upperDir, thoraxU), z = Vector3.Dot(upperDir, thoraxF);
            flexDeg = Mathf.Atan2(z, -y) * Mathf.Rad2Deg;
            abdDeg = BMath.SafeAsin(side * x) * Mathf.Rad2Deg;
            return true;
        }

        /// <summary>
        /// Cinématique inverse analytique de la jambe (hanche → cheville) dans le repère (R, U, F) du bassin :
        /// flexion et abduction de hanche (mêmes définitions que <see cref="Evaluate"/>) et flexion du genou (≥ 0).
        /// Le genou part vers l'avant (pôle +F), la jambe fléchie amène donc la cuisse devant la ligne hanche-cheville.
        /// </summary>
        public static bool LegIK(Vector3 hip, float thighLen, float calfLen, Vector3 target, int side,
                                 Vector3 pelvisR, Vector3 pelvisU, Vector3 pelvisF,
                                 out float flexDeg, out float abdDeg, out float kneeDeg)
        {
            flexDeg = abdDeg = kneeDeg = 0f;
            float L1 = Mathf.Max(0.2f, thighLen);
            float L2 = Mathf.Max(0.2f, calfLen);
            Vector3 D = target - hip;
            float dist = D.magnitude;
            if (dist < 1e-3f) return false;
            float d = Mathf.Clamp(dist, Mathf.Abs(L1 - L2) + 0.05f, L1 + L2 - 0.004f);
            float cosK = Mathf.Clamp((L1 * L1 + L2 * L2 - d * d) / (2f * L1 * L2), -1f, 1f);
            kneeDeg = 180f - Mathf.Acos(cosK) * Mathf.Rad2Deg;
            float cosA = Mathf.Clamp((L1 * L1 + d * d - L2 * L2) / (2f * L1 * d), -1f, 1f);
            float alpha = Mathf.Acos(cosA);
            Vector3 dn = D / dist;
            Vector3 p = pelvisF - dn * Vector3.Dot(pelvisF, dn);
            p = BMath.SafeNormalize(p, pelvisF);
            Vector3 thighDir = dn * Mathf.Cos(alpha) + p * Mathf.Sin(alpha);
            float x = Vector3.Dot(thighDir, pelvisR), y = Vector3.Dot(thighDir, pelvisU), z = Vector3.Dot(thighDir, pelvisF);
            flexDeg = Mathf.Atan2(z, -y) * Mathf.Rad2Deg;
            abdDeg = BMath.SafeAsin(side * x) * Mathf.Rad2Deg;
            return true;
        }

        /// <summary>
        /// Abduction minimale anatomique de l'épaule (rad) selon la flexion : bras le long du corps ou en arrière
        /// (flex ≤ 20°), la cage thoracique bloque l'adduction à ~0° ; devant le buste (85°–130°), le bras dégage
        /// la paroi antérieure et peut croiser jusqu'à ~−22° ; au-dessus de la tête, la tête/cou resserre la borne.
        /// </summary>
        public static float MinShoulderAbductionRad(float flexRad)
        {
            float flexDeg = flexRad / BMath.Deg;
            float openFront = BMath.SmoothStep(20f, 85f, flexDeg);
            float closeHead = BMath.SmoothStep(130f, 180f, flexDeg);
            return -22f * openFront * (1f - 0.64f * closeHead) * BMath.Deg;
        }

        /// <summary>
        /// Détecte la pénétration d'un point (monde) dans la capsule elliptique du thorax (repère anatomique du thorax).
        /// Renvoie la profondeur de pénétration (m) et la normale sortante (monde) ; 0 si le point est à l'extérieur.
        /// </summary>
        public static bool ThoraxPenetration(
            Vector3 point, Vector3 thoraxPos, Vector3 tR, Vector3 tU, Vector3 tF,
            float chestZ, float yBot, float yTop, float rx, float ryCap, float rz, int side,
            out float depth, out Vector3 normalWorld)
        {
            Vector3 d = point - thoraxPos;
            float x = Vector3.Dot(d, tR);
            float y = Vector3.Dot(d, tU);
            float z = Vector3.Dot(d, tF) - chestZ;
            float yc = Mathf.Clamp(y, yBot, yTop);
            float dy = y - yc;
            float ux = x / rx, uy = dy / ryCap, uz = z / rz;
            float q = Mathf.Sqrt(ux * ux + uy * uy + uz * uz);
            if (q >= 1f)
            {
                depth = 0f;
                normalWorld = Vector3.zero;
                return false;
            }
            float gx = x / (rx * rx);
            float gy = dy / (ryCap * ryCap);
            float gz = z / (rz * rz);
            float gm = Mathf.Sqrt(gx * gx + gy * gy + gz * gz);
            if (gm < 1e-6f || q < 1e-4f)
            {
                float s = side != 0 ? side : 1f;
                normalWorld = BMath.SafeNormalize(tR * (0.7f * s) + tF * 0.7f, tF);
                depth = Mathf.Min(rx, rz);
                return true;
            }
            float invGm = 1f / gm;
            normalWorld = BMath.SafeNormalize(tR * (gx * invGm) + tU * (gy * invGm) + tF * (gz * invGm), tF);
            depth = (1f - q) * q * invGm;
            return depth > 1e-5f;
        }

        /// <summary>Projette une cible (ex. main qui agrippe la plaie) à la surface extérieure du thorax si elle est dedans.</summary>
        public static Vector3 ClampOutsideThorax(
            Vector3 point, Vector3 thoraxPos, Vector3 tR, Vector3 tU, Vector3 tF,
            float chestZ, float yBot, float yTop, float rx, float ryCap, float rz, int side, float margin = 0.015f)
        {
            if (ThoraxPenetration(point, thoraxPos, tR, tU, tF, chestZ, yBot, yTop, rx, ryCap, rz, side, out float depth, out Vector3 n))
                return point + n * (depth + margin);
            return point;
        }
    }
}
