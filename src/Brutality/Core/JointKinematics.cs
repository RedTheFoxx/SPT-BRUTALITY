using Brutality.Anatomy;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>Frame and geometric parameters of a joint, frozen at the moment of death.</summary>
    internal struct JointFrame
    {
        public JointKind Kind;
        public int Side;               // -1 left, +1 right, 0 axial
        public Vector3 PR, PU, PF;     // parent anatomical frame (parent local)
        public Vector3 CR, CU;         // child right and proximal/superior axes (child local)
        public Vector3 HingeAxisP;     // flexion axis (knee/elbow), parent local
        public Quaternion RelRest;     // inv(qP0) * qC0
        public float TwistOffset;      // absolute axial rotation at death (spine, neck)
        public float FlexMid;          // midpoint of flexion range (rad), for unwrapping
    }

    /// <summary>
    /// Anatomical joint angles (inspired by ISB frames, Wu et al. 2002/2005), in radians:
    /// x = flexion(+)/extension(-), y = abduction(+) / right lateral tilt(+) / varus, z = axial rotation.
    /// Pure functions (no engine dependency): unit-tested outside the game.
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
                    // "Descending" segment: child's distal direction in the parent's frame.
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
                    // "Ascending" segment: child's superior axis in the parent's frame.
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
                    // Hinge: angle between distal directions around the flexion axis.
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
            // Unwrap around the midpoint of the range: the ±180° discontinuity is placed opposite the
            // physiological zone (e.g. arm raised overhead slightly backward = 181°, not -179°).
            flex = f.FlexMid + BMath.WrapPi(flex - f.FlexMid);
            return new Vector3(flex, side, twist);
        }

        private static readonly Vector3[] UnitAxes = { new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f) };

        /// <summary>
        /// Numerical angular gradients of the three angles with respect to a small child rotation,
        /// expressed in the parent's frame (relative rotation qRel). g[d] · ω = dφd/dt.
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
        /// Solves for the parent→child relative rotation that produces the requested anatomical angles
        /// (Levenberg-Marquardt damped least squares on the numerical Jacobian, warm start).
        /// Insensitive to sign conventions: it is sufficient that Evaluate be consistent.
        /// </summary>
        public static Quaternion SolvePose(in JointFrame f, Quaternion qStart, Vector3 target, int iterations, out float residualRad)
        {
            Quaternion q = BMath.Normalize(qStart);
            residualRad = 0f;
            for (int it = 0; it < iterations; it++)
            {
                Gradients(f, q, out Vector3 phi, out Vector3 g0, out Vector3 g1, out Vector3 g2);
                // For flexion unwrapped around FlexMid (e.g. shoulder [-55°, 180°]), keep the direct difference
                // clamped to ±π so an arm past 195° does not swing backward via the shortest path.
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

        /// <summary>Solves a symmetric 3×3 system using Cramer's rule.</summary>
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
        /// Flexion axis of a hinge (parent local), oriented so flexion is positive:
        /// the knee swings the calf backward, the elbow swings the forearm forward.
        /// </summary>
        public static Vector3 HingeAxis(JointKind kind, Quaternion qP, Vector3 pR, Vector3 pU, Vector3 pF, Quaternion qC, Vector3 cU)
        {
            Vector3 dP = qP * -pU;
            Vector3 dC = qC * -cU;
            Vector3 n;
            if (Vector3.Angle(dP, dC) > 12f)
            {
                // A knee or elbow clearly bent at death (>12°) is always in positive flexion:
                // Cross(dP, dC) is orthogonal to both segments and directly gives the positive direction (SignedAngleOnPlane > 0),
                // even if the forearm is folded across the chest (where the humerus pF is unreliable).
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
        /// Child local frame (axis = X flexion, secondary = Y abduction/varus) to configure a ConfigurableJoint.
        /// Computed analytically from anatomical axes (immune to the ±90° shoulder abduction singularity).
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

            // Y orthogonal to X and to longitudinal axis CU so that Z = X × Y aligns with axial twist.
            Vector3 sec = Vector3.Cross(f.CU, axis);
            if (sec.sqrMagnitude < 0.01f)
            {
                Vector3 hint = invC * (f.Side != 0 ? f.PF * f.Side : f.PF);
                sec = hint - axis * Vector3.Dot(hint, axis);
            }
            secondary = BMath.SafeNormalize(sec, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.forward);
        }

        /// <summary>Absolute axial rotation of the child segment relative to the parent (spine, neck).</summary>
        public static float AbsoluteTwist(Quaternion qP, Vector3 pR, Quaternion qC, Vector3 cR, Vector3 cU)
        {
            return BMath.SignedAngleOnPlane(qP * pR, qC * cR, qC * cU);
        }

        /// <summary>
        /// Analytical two-segment inverse kinematics: shoulder angles (same definitions as Evaluate)
        /// and elbow flexion to bring the hand to a target.
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
            // Pole: elbow points downward and outward.
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
        /// Analytical leg inverse kinematics (hip → ankle) in the pelvis (R, U, F) frame:
        /// hip flexion and abduction (same definitions as <see cref="Evaluate"/>) and knee flexion (≥ 0).
        /// The knee points forward (+F pole), so flexing the leg brings the thigh in front of the hip-ankle line.
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
        /// Minimum anatomical shoulder abduction (rad) by flexion: with the arm at the side or behind
        /// (flex ≤ 20°), the ribcage blocks adduction at ~0°; in front of the chest (85°–130°), the arm clears
        /// the anterior wall and can cross up to ~−22°; overhead, the head/neck tightens the bound again.
        /// </summary>
        public static float MinShoulderAbductionRad(float flexRad)
        {
            float flexDeg = flexRad / BMath.Deg;
            float openFront = BMath.SmoothStep(20f, 85f, flexDeg);
            float closeHead = BMath.SmoothStep(130f, 180f, flexDeg);
            return -22f * openFront * (1f - 0.64f * closeHead) * BMath.Deg;
        }

        /// <summary>
        /// Detects penetration of a world-space point into the elliptical thorax capsule (thorax anatomical frame).
        /// Returns penetration depth (m) and outward normal (world); 0 if the point is outside.
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

        /// <summary>Projects a target (e.g. hand clutching a wound) onto the outer surface of the thorax if inside.</summary>
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
