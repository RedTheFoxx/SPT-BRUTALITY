using Brutality.Anatomy;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>
    /// Muscles et tissus passifs exprimés en drives de couple PhysX (intégration implicite par le solveur :
    /// aucune oscillation numérique, contrairement aux couples explicites — cf. Yeo et al. 2023).
    /// On pilote en couple (useAcceleration = false) pondéré par l'inertie réelle du sous-arbre autour du pivot
    /// (Steiner + segments distaux) : en mode eACCELERATION_SPRING, PhysX utilise uniquement la moyenne harmonique
    /// des inerties au CdM des deux segments adjacents, ce qui sous-évalue de 10× à 48× la raideur de la hanche,
    /// du rachis et de l'épaule.
    ///  - Activation du premier ordre (Zajac 1989 ; Thelen 2003 : 15 ms / 50 ms).
    ///  - Raideur active ∝ Ieff · a · ωn², avec avance de phase équivalente à cMus · (RefV - φ̇).
    ///  - Couple maximal = a · Tmax · f(angle) · g(vitesse) (Anderson et al. 2007, Hill 1938).
    ///  - Tonus passif de repos + couplage bi-articulaire (ischio-jambiers, droit fémoral).
    /// La pose cible (angles anatomiques) est convertie en rotation par le solveur de pose.
    /// </summary>
    internal static class DriveController
    {
        private const float TauActivation = 0.015f;
        private const float TauDeactivation = 0.050f;
        private const float PassiveK = 4f;                    // tonus passif (rad/s²/rad) pendant la phase active
        private const float PassiveC = 2.6f;                  // viscosité passive (1/s) pendant la phase active
        private const float FlaccidC = 1.1f;                  // viscosité légère d'un membre relâché (laisse tomber sous gravité)
        private const float BiarticularK = 25f;
        private const float ShoulderBarrierK = 1200f;         // butée souple implicite de la cage thoracique (rad/s²)
        private const float RefOmegaMin = MotorCommandBuffer.RefOmegaMin;      // lissage de la commande (rad/s)

        // Convention du targetRotation PhysX : réglage forcé, sinon mesure de la sonde, sinon vérification en jeu.
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
            _forced = mode == "Normale" || mode == "Inversée";
            _forcedValue = mode == "Inversée";
        }

        /// <param name="passiveTone">1 pendant la phase active, décroît vers 0 pendant le relâchement post-mortem (supprime le rappel vers NeutralDeg).</param>
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
                    // Raideur active = ω² · a · muscleStiffness (relâchement NaturalMotion : l'amortissement est conservé).
                    float wn = mc.Omega;
                    float kMus = hasMuscle * a * wn * wn * mc.MuscleStiffness;
                    float kPas = PassiveK * passive * tone;
                    float kBia = d == 0 ? BiasK[jm.Index] : 0f;
                    float neutral = spec.NeutralDeg * BMath.Deg;

                    // Barrière thoracique implicite de l'épaule : empêche le bras de s'enfoncer dans le thorax
                    // même quand les muscles sont éteints (poids du tronc en chute avant/latérale ou repos au sol).
                    float kBar = 0f;
                    float barTarget = phi;
                    if (jm.Kind == JointKind.Shoulder && d == 1)
                    {
                        float minNow = minShoulderAbd;
                        float pen = minNow - phi;
                        if (pen > 0f)
                        {
                            float bw = BMath.SmoothStep(0f, 6f * BMath.Deg, pen);
                            kBar = ShoulderBarrierK * Mathf.Max(0.5f, passive) * (0.25f + 0.75f * bw);
                            barTarget = minNow;
                            barrierY = true;
                        }
                    }

                    // Trajectoire de référence : intégration analytique critique (inconditionnellement stable quel que
                    // soit dt). Lors de la désactivation (u == 0 tandis que a décroît), on maintient la référence
                    // courante au lieu de rappeler le membre vers 0° (tm par défaut).
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
                        if (d == 0)
                            goal = Mathf.Clamp(tm, dj.Flex0Rad + dj.LowRelRad, dj.Flex0Rad + dj.HighRelRad);
                        else if (jm.Kind == JointKind.Shoulder && d == 1)
                            goal = Mathf.Max(tm, JointKinematics.MinShoulderAbductionRad(target.x));
                        else
                            goal = jm.Ref[d] + BMath.WrapPi(tm - jm.Ref[d]);
                        float wr = mc.RefOmega;
                        BMath.StepCriticallyDamped(ref jm.Ref[d], ref jm.RefV[d], goal, wr, dt, vlim);
                    }

                    float muscleTarget = a > 1e-3f ? (d == 0 ? jm.Ref[d] : phi + BMath.WrapPi(jm.Ref[d] - phi)) : phi;
                    if (jm.Kind == JointKind.Shoulder && d == 1)
                        muscleTarget = Mathf.Max(muscleTarget, JointKinematics.MinShoulderAbductionRad(target.x));
                    float kSum = kMus + kPas + kBia + kBar;
                    float cMus = hasMuscle * 2f * mc.Zeta * wn * Mathf.Sqrt(a);
                    if (kSum > 1e-4f)
                    {
                        // Avance de phase équivalente à cMus * (RefV - PhiDot) sans dépendre de targetAngularVelocity :
                        // kMus*(ref - phi) + cMus*(refV - phiDot) == kMus*((ref + cMus/kMus * refV) - phi) - cMus*phiDot.
                        float velLead = a > 1e-3f ? Mathf.Clamp(cMus * jm.RefV[d] / kSum, -0.45f, 0.45f) : 0f;
                        target[d] = (kMus * muscleTarget + kPas * neutral + kBia * BiasTarget[jm.Index] + kBar * barTarget) / kSum + velLead;
                    }
                    else
                    {
                        target[d] = phi;
                    }
                    // Borne la flexion cible dans l'intervalle ±175° autour de StartRel (évite le repliement >180° du twist PhysX).
                    if (d == 0) target[d] = Mathf.Clamp(target[d], dj.Flex0Rad + dj.LowRelRad, dj.Flex0Rad + dj.HighRelRad);
                    if (jm.Kind == JointKind.Shoulder && d == 1)
                        target[d] = Mathf.Max(target[d], Mathf.Max(minShoulderAbd, JointKinematics.MinShoulderAbductionRad(target.x)));

                    float inertia = ieff[d];
                    kp[d] = inertia * kSum;
                    // Amortissement passif/bi-articulaire/barrière : critique sur les ressorts actifs, léger au relâchement.
                    float cBase = Mathf.Lerp(FlaccidC, PassiveC, tone);
                    float cPassive = Mathf.Max(cBase, 1.6f * Mathf.Sqrt(kPas + kBia)) * dampingScale;
                    float cBar = kBar > 0f ? 2f * Mathf.Sqrt(kBar) : 0f;
                    kd[d] = inertia * (cMus + cPassive + cBar);

                    // Couple disponible : relation couple-angle en cloche et force-vitesse de Hill.
                    float lo = spec.MinDeg * BMath.Deg, hi = spec.MaxDeg * BMath.Deg;
                    float x = (phi - 0.5f * (lo + hi)) / (0.6f * Mathf.Max(0.3f, hi - lo));
                    float f = 0.45f + 0.55f * Mathf.Exp(-x * x);
                    float err = target[d] - phi;
                    // Concentrique si le couple du drive va dans le sens du mouvement, excentrique (freinage, plus fort)
                    // sinon : le freinage n'est plus bridé comme une accélération, ce qui supprimait l'amortissement
                    // en fin de geste (dépassement, rebond « élastique »).
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

                // angularYZDrive partage un unique ressort (Max(kp.y, kp.z)) sur Y et Z : si un seul des deux axes
                // est activé (muscle ou barrière thoracique), on laisse l'axe non commandé sur sa posture courante (phi)
                // afin de ne pas le verrouiller artificiellement sur sa pose de mort (neutral = 0).
                bool activeY = activeDof[1] || barrierY;
                bool activeZ = activeDof[2];
                if (activeY && !activeZ) target.z = jm.Phi.z;
                else if (activeZ && !activeY) target.y = jm.Phi.y;

                Quaternion start = dj.HasSolve ? dj.LastSolve : qRel;
                Quaternion q = JointKinematics.SolvePose(jm.Frame, start, target, 6, out _);
                dj.LastSolve = q;
                dj.HasSolve = true;

                // angularYZDrive partage un unique couple (spring, damper, maxForce) entre Y (abduction) et Z (torsion).
                // Pour les charnières (Y verrouillé), on utilise l'inertie de torsion Z ; pour les rotules, on choisit
                // l'axe actif (ou la moyenne géométrique en passif afin de ne pas sur-amortir la torsion).
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
        /// Vérifie en jeu que les drives entraînent les articulations VERS leur cible (genoux et coudes activés) ;
        /// sinon la convention du targetRotation est inversée et on la retourne. Décision prise une fois par session.
        /// </summary>
        private static void Calibrate()
        {
            if (_decided || _calN < 150) return;
            float m = _calSum / _calN;
            if (m < -0.3f && _flips < 2)
            {
                _calInverted = !_calInverted;
                _flips++;
                Plugin.Log.LogWarning($"Brutality : les drives s'éloignaient de leur cible (corrélation {m:0.00}) — convention inversée.");
            }
            else if (m > 0.3f)
            {
                _decided = true;
                Plugin.Log.LogInfo($"Brutality : convention des drives vérifiée (corrélation {m:0.00}, {(Inverted ? "inversée" : "normale")}).");
            }
            else if (_calTotal >= 800)
            {
                _decided = true;
            }
            if (!_decided) { _calSum = 0f; _calN = 0; }
        }

        /// <summary>
        /// Couplage passif bi-articulaire (Riener &amp; Edrich 1999) : ischio-jambiers étirés par flexion de hanche
        /// + extension du genou ; droit fémoral étiré par extension de hanche + flexion du genou.
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
                // Gradient d'énergie ½ K r² : rapport des bras de levier hanche/genou = 0,4 / 0,6 = 2/3.
                BiasK[knee.Index] = BiarticularK; BiasTarget[knee.Index] = kneeF - r;
                BiasK[hip.Index] = BiarticularK; BiasTarget[hip.Index] = hipF + (0.4f / 0.6f) * r;
            }
        }
    }
}
