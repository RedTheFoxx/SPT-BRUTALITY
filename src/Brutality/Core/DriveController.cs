using Brutality.Anatomy;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>
    /// Muscles and passive tissues expressed as PhysX torque drives (implicit solver integration:
    /// no numerical oscillation, unlike explicit torques — cf. Yeo et al. 2023).
    /// Driven in torque (useAcceleration = false) weighted by the true subtree inertia around the pivot
    /// (parallel axis + distal segments): in eACCELERATION_SPRING mode, PhysX only uses the harmonic mean
    /// of the CoM inertias of the two adjacent segments, which underestimates hip, spine, and shoulder
    /// stiffness by 10× to 48×.
    ///  - First-order activation (Zajac 1989; Thelen 2003: 15 ms / 50 ms).
    ///  - Active stiffness ∝ Ieff · a · ωn², with phase lead equivalent to cMus · (RefV - φ̇).
    ///  - Max torque = a · Tmax · f(angle) · g(velocity) (Anderson et al. 2007, Hill 1938).
    ///  - Resting passive tone + biarticular coupling (hamstrings, rectus femoris).
    /// Target pose (anatomical angles) is converted to a rotation by the pose solver.
    /// </summary>
    internal static class DriveController
    {
        private const float TauActivation = 0.015f;
        private const float TauDeactivation = 0.050f;
        private const float PassiveK = 4f;                    // passive tone (rad/s²/rad) during active phase
        private const float PassiveC = 2.6f;                  // passive viscosity (1/s) during active phase
        private const float FlaccidC = 0.55f;                 // light viscosity of a relaxed limb (lets gravity drop it)
        private const float BiarticularK = 25f;
        private const float ShoulderBarrierK = 220f;          // implicit soft ribcage limit (rad/s²)
        private const float RefOmegaMin = MotorCommandBuffer.RefOmegaMin;      // command smoothing (rad/s)

        // PhysX targetRotation convention: forced setting, else probe measurement, else in-game check.
        private static bool _forced;
        private static bool _forcedValue;
        private static bool _calInverted;
        private static float _calSum;
        private static int _calN;
        private static int _calTotal;
        private static int _flips;
        private static bool _decided;
        public static bool Inverted
        {
            get
            {
                if (_forced) return _forcedValue;
                if (PhysxProbe.DriveInverted.HasValue) return PhysxProbe.DriveInverted.Value;
                return _calInverted;
            }
        }

        private static readonly float[] BiasTarget = new float[AnatomyData.JointCount];
        private static readonly float[] BiasK = new float[AnatomyData.JointCount];

        public static void ApplyConventionSetting()
        {
            string mode = Settings.DriveConvention.Value;
            _forced = mode == "Normal" || mode == "Inverted";
            _forcedValue = mode == "Inverted";
        }

        /// <param name="passiveTone">1 during the active phase, decays toward 0 during post-mortem relaxation (removes pull toward NeutralDeg).</param>
        public static void Step(BodyModel body, MotorCommandBuffer cmd, float dt, float dampingScale, bool muscles, float passiveTone = 1f)
        {
            float strength = Settings.MuscleStrength.Value;
            float passive = Settings.PassiveStiffness.Value;

            for (int j = 0; j < BiasK.Length; j++) BiasK[j] = 0f;
            Biarticular(body, -1);
            Biarticular(body, 1);

            foreach (var jm in body.Joints)
            {
                var dj = jm.Driven;
                if (dj == null || dj.Joint == null) continue;
                var sp = body.Segs[jm.P];
                var sc = body.Segs[jm.C];
                Quaternion qRel = Quaternion.Inverse(sp.Q) * sc.Q;

                Vector3 target = Vector3.zero;
                Vector3 kp = Vector3.zero, kd = Vector3.zero, fmax = Vector3.zero;
                bool[] activeDof = { false, false, false };
                bool barrierY = false;
                Vector3 ieff = jm.Ieff;
                float tone = Mathf.Clamp01(passiveTone);
                float minShoulderAbd = jm.Kind == JointKind.Shoulder
                    ? JointKinematics.MinShoulderAbductionRad(jm.Phi.x)
                    : float.NegativeInfinity;
                for (int d = 0; d < 3; d++)
                {
                    ref readonly DofSpec spec = ref jm.Spec.Dofs[d];
                    cmd.Resolve(jm.Index, d, out MotorCommand mc);
                    float tm = mc.Target;
                    float u = muscles ? mc.U * jm.Capacity[d] : 0f;
                    float a = jm.Activation[d];
                    a += (u - a) * (1f - Mathf.Exp(-dt / (u > a ? TauActivation : TauDeactivation)));
                    jm.Activation[d] = a;

                    float phi = jm.Phi[d];
                    float hasMuscle = spec.TPos > 0f || spec.TNeg > 0f ? 1f : 0f;
                    bool commanded = hasMuscle > 0f && u >= 0.01f;
                    activeDof[d] = hasMuscle > 0f && a >= 0.02f;
                    // Active stiffness = ω² · a · muscleStiffness (NaturalMotion relaxation: damping is preserved).
                    float wn = mc.Omega;
                    float kMus = hasMuscle * a * wn * wn * mc.MuscleStiffness;
                    float kPas = PassiveK * passive * tone;
                    float kBia = d == 0 ? BiasK[jm.Index] : 0f;
                    float neutral = spec.NeutralDeg * BMath.Deg;

                    // Implicit shoulder thoracic barrier: prevents the arm from sinking into the thorax
                    // even when muscles are off (trunk weight in forward/side fall or resting on the ground).
                    float kBar = 0f;
                    float barTarget = phi;
                    if (jm.Kind == JointKind.Shoulder && d == 1)
                    {
                        float minNow = minShoulderAbd;
                        float pen = minNow - phi - 2f * BMath.Deg;
                        if (pen > 0f)
                        {
                            float bw = BMath.SmoothStep(0f, 10f * BMath.Deg, pen);
                            kBar = ShoulderBarrierK * Mathf.Max(0.5f, passive) * (0.20f + 0.80f * bw);
                            barTarget = minNow;
                            barrierY = true;
                        }
                    }

                    // Reference trajectory: analytical critically damped integration (unconditionally stable for any
                    // dt). During deactivation (u == 0 while a decays), hold the current reference
                    // instead of pulling the limb back toward 0° (default tm).
                    float vlim = 0.6f * Mathf.Max(1f, spec.VMax);
                    if (!commanded && a < 0.02f)
                    {
                        jm.Ref[d] = phi;
                        jm.RefV[d] = Mathf.Clamp(jm.PhiDot[d], -vlim, vlim);
                    }
                    else if (!commanded)
                    {
                        jm.RefV[d] *= Mathf.Exp(-RefOmegaMin * dt);
                        jm.Ref[d] += jm.RefV[d] * dt;
                    }
                    else
                    {
                        float goal;
                        float loRad = spec.MinDeg * BMath.Deg, hiRad = spec.MaxDeg * BMath.Deg;
                        if (d == 0)
                            goal = Mathf.Clamp(tm, dj.Flex0Rad + dj.LowRelRad, dj.Flex0Rad + dj.HighRelRad);
                        else if (jm.Kind == JointKind.Shoulder && d == 1)
                            goal = Mathf.Clamp(tm, JointKinematics.MinShoulderAbductionRad(target.x), hiRad);
                        else
                            goal = Mathf.Clamp(jm.Ref[d] + BMath.WrapPi(tm - jm.Ref[d]), loRad, hiRad);
                        float wr = mc.RefOmega;
                        BMath.StepCriticallyDamped(ref jm.Ref[d], ref jm.RefV[d], goal, wr, dt, vlim);
                    }

                    float muscleTarget = a > 1e-3f ? (d == 0 ? jm.Ref[d] : phi + BMath.WrapPi(jm.Ref[d] - phi)) : phi;
                    if (jm.Kind == JointKind.Shoulder && d == 1)
                        muscleTarget = Mathf.Max(muscleTarget, JointKinematics.MinShoulderAbductionRad(target.x));
                    // Shoulder/Hip axial twist is measured relative to the death pose (RelRest), not neutral anatomy:
                    // when twist is uncommanded, hold current twist instead of pulling back toward a gun-holding pose.
                    float kPasDof = (d == 2 && (jm.Kind == JointKind.Shoulder || jm.Kind == JointKind.Hip) && !activeDof[2]) ? 0f : kPas;
                    float kSum = kMus + kPasDof + kBia + kBar;
                    float cMus = hasMuscle * 2f * mc.Zeta * wn * Mathf.Sqrt(a);
                    if (kSum > 1e-4f)
                    {
                        // Phase lead equivalent to cMus * (RefV - PhiDot) without relying on targetAngularVelocity:
                        // kMus*(ref - phi) + cMus*(refV - phiDot) == kMus*((ref + cMus/kMus * refV) - phi) - cMus*phiDot.
                        float velLead = a > 1e-3f ? Mathf.Clamp(cMus * jm.RefV[d] / kSum, -0.45f, 0.45f) : 0f;
                        target[d] = (kMus * muscleTarget + kPasDof * neutral + kBia * BiasTarget[jm.Index] + kBar * barTarget) / kSum + velLead;
                    }
                    else
                    {
                        target[d] = phi;
                    }
                    // Clamp target flexion within ±175° around StartRel (prevents >180° PhysX twist wrap-around)
                    // and keep Y/Z within anatomical ROM.
                    if (d == 0) target[d] = Mathf.Clamp(target[d], dj.Flex0Rad + dj.LowRelRad, dj.Flex0Rad + dj.HighRelRad);
                    else target[d] = Mathf.Clamp(target[d], spec.MinDeg * BMath.Deg, spec.MaxDeg * BMath.Deg);
                    if (jm.Kind == JointKind.Shoulder && d == 1)
                        target[d] = Mathf.Max(target[d], Mathf.Max(minShoulderAbd, JointKinematics.MinShoulderAbductionRad(target.x)));

                    float inertia = ieff[d];
                    kp[d] = inertia * kSum;
                    // Passive/biarticular/barrier damping: critical on active springs, light during relaxation.
                    float cBase = Mathf.Lerp(FlaccidC, PassiveC, tone);
                    float cPassive = Mathf.Max(cBase, 1.6f * Mathf.Sqrt(kPasDof + kBia)) * dampingScale;
                    float cBar = kBar > 0f ? 1.3f * Mathf.Sqrt(kBar) : 0f;
                    kd[d] = inertia * (cMus + cPassive + cBar);

                    // Available torque: bell-shaped torque-angle and Hill force-velocity curves.
                    float lo = spec.MinDeg * BMath.Deg, hi = spec.MaxDeg * BMath.Deg;
                    float x = (phi - 0.5f * (lo + hi)) / (0.6f * Mathf.Max(0.3f, hi - lo));
                    float f = 0.45f + 0.55f * Mathf.Exp(-x * x);
                    float err = target[d] - phi;
                    // Concentric if the drive torque acts in the direction of motion, eccentric (braking, stronger)
                    // otherwise: braking is no longer throttled like acceleration, which used to suppress damping
                    // at the end of a movement (overshoot, "elastic" bounce).
                    float drive = kp[d] * err - kd[d] * jm.PhiDot[d];
                    float v = drive >= 0f ? jm.PhiDot[d] : -jm.PhiDot[d];
                    float vmax = Mathf.Max(1f, spec.VMax);
                    float g = v >= 0f ? Mathf.Max(0.05f, (1f - v / vmax) / (1f + v / (0.25f * vmax))) : 1f + 0.8f * (-v) / (-v + 0.15f * vmax);
                    float tmax = drive >= 0f ? spec.TPos : spec.TNeg;
                    fmax[d] = hasMuscle * a * tmax * strength * f * g + 15f * passive + (kBia > 0f ? 60f : 0f) + (kBar > 0f ? 120f : 0f);
                    if (d == 0 && !_decided && !_forced && !PhysxProbe.DriveInverted.HasValue && a > 0.3f && Mathf.Abs(err) > 0.15f &&
                        (jm.Kind == JointKind.Knee || jm.Kind == JointKind.Elbow))
                    {
                        _calSum += Mathf.Sign(err) * Mathf.Clamp(jm.PhiDot[d] / 2f, -1f, 1f);
                        _calN++;
                        _calTotal++;
                    }
                }

                // Near ±90° abduction on ball-and-socket limbs, sagittal flexion Atan2(z, -y) approaches gimbal lock:
                // hold flexion near its current angle and attenuate flexion spring so SolvePose cannot spin the arm.
                if ((jm.Kind == JointKind.Shoulder || jm.Kind == JointKind.Hip) && jm.SagWeight < 0.35f)
                {
                    float sagW = BMath.SmoothStep(0.08f, 0.35f, jm.SagWeight);
                    target.x = Mathf.Lerp(jm.Phi.x, target.x, sagW);
                    kp.x *= sagW;
                    fmax.x *= 0.2f + 0.8f * sagW;
                }

                // angularYZDrive shares a single spring (Max(kp.y, kp.z)) across Y and Z: if only one of the two
                // axes is active (muscle or thoracic barrier), leave the uncommanded axis at its current posture (phi)
                // so it is not artificially locked to its death pose (neutral = 0).
                bool activeY = activeDof[1] || barrierY;
                bool activeZ = activeDof[2];
                if (activeY && !activeZ) target.z = jm.Phi.z;
                else if (activeZ && !activeY) target.y = jm.Phi.y;

                Quaternion start = dj.HasSolve && Quaternion.Angle(dj.LastSolve, qRel) < 90f ? dj.LastSolve : qRel;
                Quaternion q = JointKinematics.SolvePose(jm.Frame, start, target, 10, out _);
                dj.LastSolve = q;
                dj.HasSolve = true;

                // angularYZDrive shares a single drive (spring, damper, maxForce) between Y (abduction) and Z (twist).
                // For hinges (Y locked), use Z twist inertia; for ball-and-socket joints, pick the active axis
                // (or the geometric mean when passive so twist is not over-damped).
                bool isHinge = jm.Kind == JointKind.Knee || jm.Kind == JointKind.Elbow;
                float kpYZ, kdYZ, fmaxYZ;
                if (isHinge || (activeZ && !activeY))
                {
                    kpYZ = kp.z; kdYZ = kd.z; fmaxYZ = fmax.z;
                }
                else if (activeY && !activeZ)
                {
                    kpYZ = kp.y; kdYZ = kd.y; fmaxYZ = fmax.y;
                }
                else if (activeY && activeZ)
                {
                    kpYZ = Mathf.Max(kp.y, kp.z); kdYZ = Mathf.Max(kd.y, kd.z); fmaxYZ = Mathf.Max(fmax.y, fmax.z);
                }
                else
                {
                    kpYZ = Mathf.Sqrt(kp.y * kp.z); kdYZ = Mathf.Sqrt(kd.y * kd.z); fmaxYZ = Mathf.Max(fmax.y, fmax.z);
                }

                var cj = dj.Joint;
                cj.targetRotation = PhysxProbe.DriveTarget(q, dj.StartRel, dj.Axis, dj.Secondary, Inverted);
                cj.angularXDrive = new JointDrive { positionSpring = kp.x, positionDamper = kd.x, maximumForce = fmax.x, useAcceleration = false };
                cj.angularYZDrive = new JointDrive { positionSpring = kpYZ, positionDamper = kdYZ, maximumForce = fmaxYZ, useAcceleration = false };
            }
            Calibrate();
        }

        /// <summary>
        /// Verifies in-game that drives pull joints TOWARD their target (active knees and elbows);
        /// otherwise the targetRotation convention is inverted and flipped. Decision made once per session.
        /// </summary>
        private static void Calibrate()
        {
            if (_decided || _calN < 150) return;
            float m = _calSum / _calN;
            if (m < -0.3f && _flips < 2)
            {
                _calInverted = !_calInverted;
                _flips++;
                Plugin.Log.LogWarning($"Brutality: drives were moving away from target (correlation {m:0.00}) — convention inverted.");
            }
            else if (m > 0.3f)
            {
                _decided = true;
                Plugin.Log.LogInfo($"Brutality: drive convention verified (correlation {m:0.00}, {(Inverted ? "inverted" : "normal")}).");
            }
            else if (_calTotal >= 800)
            {
                _decided = true;
            }
            if (!_decided) { _calSum = 0f; _calN = 0; }
        }

        /// <summary>
        /// Passive biarticular coupling (Riener &amp; Edrich 1999): hamstrings stretched by hip flexion
        /// + knee extension; rectus femoris stretched by hip extension + knee flexion.
        /// </summary>
        private static void Biarticular(BodyModel body, int side)
        {
            var hip = body.Joints[(int)AnatomyData.Hip(side)];
            var knee = body.Joints[(int)AnatomyData.Knee(side)];
            float hipF = hip.Phi.x, kneeF = knee.Phi.x;

            float h = hipF - 0.75f * kneeF - 70f * BMath.Deg;
            if (h > 0f)
            {
                BiasK[hip.Index] = BiarticularK; BiasTarget[hip.Index] = hipF - h;
                BiasK[knee.Index] = BiarticularK; BiasTarget[knee.Index] = kneeF + 0.75f * h;
            }
            float r = 0.6f * kneeF - 0.4f * hipF - 75f * BMath.Deg;
            if (r > 0f)
            {
                // Energy gradient ½ K r²: hip/knee moment-arm ratio = 0.4 / 0.6 = 2/3.
                BiasK[knee.Index] = BiarticularK; BiasTarget[knee.Index] = kneeF - r;
                BiasK[hip.Index] = BiarticularK; BiasTarget[hip.Index] = hipF + (0.4f / 0.6f) * r;
            }
        }
    }
}
