using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Euphoria
{
    /// <summary>Options du balancier, tirées du plan de mort (Delirium : message bodyBalance / staggerFall / shotFallToKnees).</summary>
    internal sealed class DlBalanceOptions
    {
        public bool Stagger, FallToKnees;
        public int MaxSteps = 3;
        public float MaxTime = 2f, PerStep = 1f, KneelHold = 0.6f, Sink0, DontStepTime;
        public int InjuredLeg;
        /// <summary>Balle dans le dos : le quadriceps cède, le bassin descend sur les genoux au lieu de basculer.</summary>
        public float GiveWay;
        /// <summary>Effondrement assis : les hanches plient, le bassin descend, aucun pas n'est tenté.</summary>
        public bool Sit;
        /// <summary>Inclinaison latérale imposée (-1 gauche, +1 droite) : la chute part sur le côté.</summary>
        public int LeanSide;
        /// <summary>Vrille autour de la verticale au début de l'équilibre (rad/s²) : le corps pivote avant de tomber.</summary>
        public float YawAccel;
        /// <summary>Delirium leanInDirection : direction horizontale d'inclinaison du balancier.</summary>
        public Vector3 LeanDir;
        /// <summary>Intensité de leanInDirection (0 = inactif, 0.1..0.35 = typique Delirium).</summary>
        public float LeanAmount;
        /// <summary>Appliquer leanInDirection comme force horizontale sur le bassin (Delirium : applyAsForce).</summary>
        public bool LeanAsForce;
        /// <summary>Mode de dérobement des jambes : 0 = aucun, 1 = une jambe cède, 2 = deux jambes ensemble, 3 = deux jambes en décalé.</summary>
        public int BuckleMode;
        /// <summary>Jambe qui cède en premier (-1 gauche, +1 droite).</summary>
        public int BuckleLeg;
        /// <summary>Délai avant le dérobement initial (s) et décalage de la seconde jambe en mode 3 (s).</summary>
        public float BuckleDelay = 0.08f, BuckleStaggerDelay = 0.16f;
    }

    /// <summary>
    /// Balancier dynamique, porté depuis <c>nmbb::BodyBalance</c> (loi du haut du corps) et <c>nmdbm</c>
    /// (gestionnaire d'équilibre dynamique : placement des pieds, contrôle du bassin, cinématique inverse des jambes)
    /// de Delirium.
    ///
    /// Principe identique : le personnage n'a pas de chevilles motrices, donc le bassin est tenu par une force
    /// corrective (Delirium : la raideur des hanches et le « stabilise ») tandis que les jambes sont posées par
    /// cinématique inverse sur un point de capture (Hof 2005 ; Pratt 2006) borné par l'amplitude de hanche. Chaque
    /// pas réduit la raideur des jambes ; quand le point de capture sort du polygone de sustentation et que le
    /// balancier renonce, le corps tombe (retour « balance », puis catchFall).
    ///
    /// Les membres supérieurs sont contrebalancés par la cinématique des hanches comme dans Delirium :
    /// l'épaule suit la hanche opposée, le coude suit l'écart hanche désirée/réelle, l'abduction suit celle de la
    /// hanche, et un terme de « moulinet » (somersault / sideSomersault) s'ajoute quand le corps tourne vite.
    /// </summary>
    internal sealed class DlBalance
    {
        public readonly DlBalanceOptions O;
        public readonly string Name;
        public bool Active = true;
        public bool Failed;
        public string Why = "";
        public float FailT = 1e9f;
        public float LowerK;
        public int Steps;
        public bool KneesDown;
        public float Sink;
        public float MaxTime;
        public int MaxSteps;
        public Vector3 LeanVec;
        /// <summary>Échelle de raideur du genou par jambe (Delirium : dbm_knee_scale, abaissée par shot.collapse).</summary>
        public readonly float[] KneeScale = { 1f, 1f };
        /// <summary>Niveau de dérobement instantané [0..1] par jambe (0 = gauche, 1 = droite).</summary>
        public readonly float[] LegBuckle = { 0f, 0f };
        /// <summary>Inclinaison additionnelle du bassin demandée par shot.collapse (Delirium : dbm_acc).</summary>
        public float AccBias;

        private readonly DlBody _b;
        private readonly DlCharacter _ch;
        private readonly NmCharacter _s;
        private readonly NmPerformance _perf;
        private readonly System.Random _rng;
        private readonly float _start, _y0, _legLength, _footWidth;
        private float _now;
        private readonly bool[] _shotCollapsed = new bool[2];
        private bool _buckleLogged;

        private readonly Vector3[] _ankle = new Vector3[2];
        private readonly Vector3[] _swingFrom = new Vector3[2];
        private readonly bool[] _planted = { true, true };
        private float _stepT, _stepDur, _stepClamp = DlCfg.StepClamp;
        private int _swing, _lastSwing;
        private float _bendT = -1f, _kneelT;
        private float _lean, _p52, _p56;
        private float _yaw0, _yaw1;
        private float _somersault, _sideSomersault, _armsOut;

        public DlBalance(NmPerformance perf, float t, DlBalanceOptions o)
        {
            _perf = perf;
            _b = perf.Dl;
            _ch = perf.Eff;
            _s = perf.Ch;
            _rng = perf.Rng;
            O = o;
            Name = o.Stagger ? "staggerFall" : o.FallToKnees ? "shotFallToKnees" : o.Sit ? "sitDown" : "bodyBalance";
            _start = t;
            MaxTime = o.MaxTime;
            MaxSteps = o.MaxSteps;
            Sink = o.Sink0;
            LowerK = o.Stagger ? 13f : DlCfg.BalanceLegStiffness;
            _legLength = _b.Body[Seg.LThigh].Length + _b.Body[Seg.LCalf].Length;
            _y0 = _b.Body[Seg.Pelvis].Com.y;
            _footWidth = Mathf.Clamp(DlBody.Horiz(_s.Ankle[0] - _s.Ankle[1]).magnitude * 0.5f, 0.09f, 0.18f);
            for (int k = 0; k < 2; k++) _ankle[k] = _s.Ankle[k];
            Activate(t);
        }

        private void Activate(float t)
        {
            _ch.ResetAll();
            foreach (var j in DlCharacter.Mask("us")) _ch.SetMuscleStiffness((int)j, 0.8f);
            foreach (var j in DlCharacter.Mask("un")) _ch.SetMuscleStiffness((int)j, 0.8f);
            // Seuls les membres sont soulagés : porter le rachis reviendrait à annuler la gravité du tronc.
            _ch.SetOpposeGravity("ua", 1f);
            _ch.SetOpposeGravity("lb", 0.4f);
            _ch.BlendToZero("uc", 1f);
            _somersault = 1f;
            _sideSomersault = 1f;
            _armsOut = 0f;
            _now = t;
            _perf.Log(t, Name);
        }

        public float Strength(float t)
        {
            float k01 = Mathf.Clamp01((LowerK - 4f) / (DlCfg.BalanceLegStiffness - 4f)) * _perf.U(t);
            if (!Failed) return k01;
            return k01 * (1f - Mathf.Clamp01((t - FailT) / 0.18f));
        }

        public void Stop(float t) => Active = false;

        public void Fail(float t, string why)
        {
            if (Failed) return;
            Failed = true;
            FailT = t;
            Why = why;
            _perf.Log(t, "déséquilibre : " + why);
        }

        /// <summary>Connecte <c>nmshot::Shot::collapse</c> au balancier (Delirium : dbm_knee_scale + dbm_acc).</summary>
        public void CollapseLeg(int side, float strength01)
        {
            if (side == 0) return;
            int k = NmCharacter.Si(side);
            float w = Mathf.Clamp01(strength01);
            _shotCollapsed[k] = w > 0.05f;
            KneeScale[k] = Mathf.Min(KneeScale[k], Mathf.Lerp(1f, 0.25f, w));
            LegBuckle[k] = Mathf.Max(LegBuckle[k], w);
            AccBias = Mathf.Min(AccBias, -0.5f * w);
        }

        // ------------------------------------------------------------------ haut du corps (nmbb::BodyBalance::tick)

        public void Tick(NeuralState ns, float t, float dt)
        {
            if (!Active) return;
            var b = _b;
            _now = t;
            if (Failed)
            {
                if (Strength(t) <= 0.01f) Active = false;
                return;
            }
            float lean = Mathf.Sqrt(Mathf.Max(0f, 1f - _s.Hip2HeadUp * _s.Hip2HeadUp));
            bool lastStand = _perf.LastStandUntil > t;
            if (!O.FallToKnees && lean > (lastStand ? 0.85f : DlCfg.BalanceAbortThreshold)) { Fail(t, "inclinaison"); return; }
            if (!O.FallToKnees && _s.Com.y - _s.FootMinY < DlCfg.GiveUpHeight) { Fail(t, "hauteur"); return; }
            if (t - _start > MaxTime) { Fail(t, "durée max"); return; }
            if (O.Stagger && LowerK <= 8.01f) { Fail(t, "jambes"); return; }

            // Raideurs de base des membres (Delirium nmbb.rs:159-167 : seuls les coudes prennent 0.6 × bodyStiffness,
            // les épaules conservent bodyStiffness).
            _ch.SetStiffness("ub", DlCfg.BalanceBodyStiffness, DlCfg.BalanceDamping);
            _ch.SetStiffness("ua", DlCfg.BalanceBodyStiffness, DlCfg.BalanceDamping);
            _ch.SetStiffness("un", DlCfg.BalanceBodyStiffness, 1f);
            _ch.SetStiffness("uc", DlCfg.BalanceBodyStiffness, 1f);
            float ke = DlCfg.BalanceBodyStiffness * 0.6f;
            _ch.SetStiffness(JointId.LElbow, ke, DlCfg.BalanceDamping);
            _ch.SetStiffness(JointId.RElbow, ke, DlCfg.BalanceDamping);
            foreach (var j in DlCharacter.Mask("us")) _ch.SetMuscleStiffness((int)j, 0.8f);
            foreach (var j in DlCharacter.Mask("un")) _ch.SetMuscleStiffness((int)j, 0.8f);

            // Flexion du rachis : S-curve de repos, puis compensation de l'inclinaison du tronc.
            float roll = Mathf.Asin(Mathf.Clamp(Vector3.Dot(_b.Part(Seg.Thorax).U, _b.Right), -1f, 1f)) * Mathf.Rad2Deg;
            var spine = DlCharacter.Mask("us");
            DlCfg.Range((int)JointId.Lumbar, 1, out float lo1, out float hi1);
            float mid1 = 0.5f * (lo1 + hi1);
            float[] baseLean = { -0.10f, 0.01f, -0.10f, 0.10f };
            float[] twistGain = { 0.35f, 0.20f, 0.60f, 0.10f };

            // Torsion : écart hanche gauche/droite (Delirium : tw = (hip0.desired - hip1.desired)·a + (hip0.actual - hip1.actual)).
            float hipDesDiff = _ch.Desired((int)JointId.LHip).x - _ch.Desired((int)JointId.RHip).x;
            float hipActDiff = _ch.Actual((int)JointId.LHip, _b.Body).x - _ch.Actual((int)JointId.RHip, _b.Body).x;
            float tw = hipDesDiff * 0.3f + hipActDiff;

            for (int i = 0; i < spine.Length; i++)
            {
                int j = (int)spine[i];
                DlCfg.Range(j, 0, out float lo, out float hi);
                float mid = 0.5f * (lo + hi);
                _ch.SetDesired(j, 0, mid + baseLean[i]);
                _ch.SetDesired(j, 1, mid1 + 0.5f * (roll * BMath.Deg) * 0.5f);
                DlCfg.Range(j, 2, out float lo2, out float hi2);
                _ch.SetDesired(j, 2, 0.5f * (lo2 + hi2) + Mathf.Clamp(tw * twistGain[i], -0.35f, 0.35f));
            }

            // Bras : contrebalancement des hanches (Delirium nmbb.rs:275-318 : saute les bras occupés par grab/shot).
            bool grounded = _s.Contact[0] || _s.Contact[1];
            for (int side = 0; side < 2; side++)
            {
                if (_ch.Busy[side] || !grounded) continue;
                int sd = side == 0 ? -1 : 1;
                var sh = AnatomyData.Shoulder(sd);
                var el = AnatomyData.Elbow(sd);
                var hipOther = AnatomyData.Hip(sd == -1 ? 1 : -1);
                var hipMine = AnatomyData.Hip(sd);
                float hoAct = _ch.Actual((int)hipOther, _b.Body).x;
                float hoDes = _ch.Desired((int)hipOther).x;
                float hsAct = _ch.Actual((int)hipMine, _b.Body).x;

                float x = -hoAct * 0.3f + hoDes;
                x = (-hoAct * 0.3f + hsAct) * 0.2f + x;
                x += _somersault * _armsOut * 0.4f;
                float shFlex = x * DlCfg.BalanceShoulder + _ch.Actual((int)sh, _b.Body).x;
                _ch.SetDesired((int)sh, 0, shFlex);
                DlCfg.Range((int)sh, 2, out float slo, out float shi);
                _ch.SetDesired((int)sh, 2, 0.5f * (slo + shi) - 0.35f);

                float elbow = (hoDes - hoAct) * DlCfg.BalanceElbow + _ch.Actual((int)el, _b.Body).x;
                DlCfg.Range((int)el, 0, out float elo, out float ehi);
                _ch.SetDesired((int)el, 0, Mathf.Clamp(elbow, elo + 0.15f, ehi - 0.05f));

                float l2 = _ch.Actual((int)hipMine, _b.Body).y * 0.5f + 0.5f;
                if (_armsOut > 0f) l2 += _somersault * _armsOut * 0.5f;
                DlCfg.Range((int)sh, 1, out float alo, out float ahi);
                _ch.SetDesired((int)sh, 1, 0.5f * (alo + ahi) + 0.5f * (ahi - alo) * Mathf.Clamp(l2 * 2f - 1f, -1f, 1f));
                _ch.SetOpposeGravity((int)sh, 1f);
            }

            // « Moulinet » lié aux vitesses angulaires : Delirium borne chaque rotation au-delà d'un seuil et la
            // réinjecte dans l'abduction (somersaultAngle / sideSomersaultAngle) quand armsOutOnPush est actif.
            Vector3 w = _b.AngVel;
            float ratePitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(_b.Part(Seg.Thorax).U, BMath.SafeNormalize(_b.Vel, _b.Fwd)), -1f, 1f)) * Mathf.Rad2Deg;
            float overPitch = Over(ratePitch, 25f);
            float overRoll = Over(roll, 25f);
            _somersault = Mathf.Lerp(_somersault, 1f + Mathf.Clamp(overPitch, 0f, 1f), 1f - Mathf.Exp(-dt * 6f));
            _sideSomersault = Mathf.Lerp(_sideSomersault, 1f + Mathf.Clamp(overRoll, 0f, 1f), 1f - Mathf.Exp(-dt * 6f));
            if (w.magnitude > 4f) _armsOut = Mathf.Min(1f, _armsOut + dt * 2f);
            else _armsOut = Mathf.Max(0f, _armsOut - dt);

            // Inclinaison latérale imposée : le buste part sur le côté, la chute se fait dans le plan frontal.
            if (O.LeanSide != 0)
                _ch.SetDesired((int)JointId.Lumbar, 1, mid1 + O.LeanSide * 0.22f);

            // Vitesse : blend_to_zero partiel du haut du corps (Delirium nmbb.rs:322 : k ∈ [0.25, 0.53], jamais 1.0).
            float f19 = Mathf.Clamp01(_s.Vel.magnitude);
            float blendK = 0.25f + 0.28f * (1f - f19);
            foreach (var j in DlCharacter.Mask("ub"))
            {
                if ((j == JointId.LShoulder || j == JointId.LElbow) && _ch.Busy[0]) continue;
                if ((j == JointId.RShoulder || j == JointId.RElbow) && _ch.Busy[1]) continue;
                _ch.BlendToZero((int)j, blendK);
            }

            // Gestionnaire d'équilibre : pieds, bassin, jambes.
            Legs(ns, t, dt, lean);
        }

        private static float Over(float x, float thr) => Mathf.Clamp(Mathf.Abs(x), thr, 60f) - thr;

        // ------------------------------------------------------------------ jambes (nmdbm)

        private void Legs(NeuralState ns, float t, float dt, float lean)
        {
            var body = _b.Body;
            var pelvis = body[Seg.Pelvis];
            float g = Mathf.Max(1f, -Physics.gravity.y);
            float w0 = Mathf.Sqrt(g / Mathf.Max(0.4f, _s.H));
            Vector3 comH = DlBody.Horiz(_s.Com), velH = DlBody.Horiz(_s.Vel);

            // Point de capture : le pied doit se poser là où le centre de masse arrivera pendant le pas.
            float pred = O.Stagger ? 0.1f : 0f;
            Vector3 apex = comH + velH * (1f / w0 + pred);
            // Chute latérale : le point de capture est décalé du côté de l'inclinaison, le corps n'a plus d'appui.
            if (O.LeanSide != 0) apex += _s.Right * (O.LeanSide * 0.24f);

            float giveWay = O.GiveWay * BMath.SmoothStep(0.01f, 0.18f, t - _start);
            // Dérobement programmé d'une ou deux jambes (toujours mis à jour avant les retours anticipés Sit/FallToKnees).
            UpdateBuckle(t, giveWay);

            // Effondrement assis : les hanches plient, le bassin descend, les pieds ne bougent plus.
            if (O.Sit && t - _start > 0.1f)
            {
                if (_bendT < 0f) { _bendT = t; _perf.Log(t, "sitDown : les hanches plient"); }
                Sink = Mathf.Min(0.46f, Sink + 1.1f * dt);
                float k = Mathf.Clamp01(Sink / 0.46f);
                for (int sd = -1; sd <= 1; sd += 2)
                {
                    int hip = (int)AnatomyData.Hip(sd), knee = (int)AnatomyData.Knee(sd);
                    DlCfg.Range(hip, 0, out float hlo, out float hhi);
                    DlCfg.Range(knee, 0, out float klo, out float khi);
                    _ch.SetDesired(hip, 0, Mathf.Lerp(hlo + 0.2f * (hhi - hlo), hlo + 0.7f * (hhi - hlo), k));
                    _ch.SetDesired(knee, 0, Mathf.Lerp(klo + 0.2f * (khi - klo), klo + 0.9f * (khi - klo), k));
                    _ch.SetStiffness((JointId)hip, LowerK, 1f);
                    _ch.SetStiffness((JointId)knee, LowerK * 0.55f, 1f);
                }
                return;
            }

            // shotFallToKnees : flexion progressive, appui sur les genoux, puis effondrement.
            if (O.FallToKnees && t - _start > 0.7f)
            {
                if (_bendT < 0f) { _bendT = t; _perf.Log(t, "shotFallToKnees : flexion"); }
                Sink = Mathf.Min(0.44f, Sink + 0.5f * dt);
                float kk = 1f - Sink / 0.44f;
                for (int sd = -1; sd <= 1; sd += 2)
                {
                    int hip = (int)AnatomyData.Hip(sd), knee = (int)AnatomyData.Knee(sd);
                    DlCfg.Range(hip, 0, out float hlo, out float hhi);
                    DlCfg.Range(knee, 0, out float klo, out float khi);
                    _ch.SetDesired(hip, 0, Mathf.Lerp(hlo + 0.25f * (hhi - hlo), hlo + 0.55f * (hhi - hlo), 1f - kk));
                    _ch.SetDesired(knee, 0, Mathf.Lerp(klo + 0.25f * (khi - klo), klo + 0.75f * (khi - klo), 1f - kk));
                    _ch.SetStiffness((JointId)hip, LowerK, 1f);
                    _ch.SetStiffness((JointId)knee, LowerK, 1f);
                }
                if (!KneesDown && Mathf.Min(_s.Knee[0].y, _s.Knee[1].y) - _s.GroundY < 0.12f)
                {
                    KneesDown = true;
                    _kneelT = t;
                    MaxTime = Mathf.Max(MaxTime, t - _start + O.KneelHold + 0.1f);
                    _perf.OnKneesImpact(t);
                }
                // Force de soutien : les genoux portent le poids au lieu de tomber.
                if (KneesDown) _perf.OnKneeSupport(t, dt, Strength(t) * 0.6f);
                if (KneesDown && t - _kneelT > O.KneelHold) Fail(t, "genoux");
                return;
            }

            // Décision du pas.
            if (_swing == 0 && t - _start >= O.DontStepTime)
            {
                Vector3 a = DlBody.Horiz(_s.Ankle[0]), c = DlBody.Horiz(_s.Ankle[1]);
                Vector3 ac = c - a;
                float u01 = Mathf.Clamp01(Vector3.Dot(apex - a, ac) / Mathf.Max(1e-6f, ac.sqrMagnitude));
                float dist = Vector3.Distance(apex, a + ac * u01);
                if (dist > 0.08f || ac.magnitude > 0.23f + 0.45f)
                {
                    if (Steps >= MaxSteps) { Fail(t, "pas max"); return; }
                    float lat = Vector3.Dot(apex - comH, _s.Right);
                    int sw = Mathf.Abs(lat) > 0.06f ? (lat > 0f ? 1 : -1)
                        : (Vector3.Distance(a, apex) > Vector3.Distance(c, apex) ? -1 : 1);
                    if (sw == _lastSwing && Mathf.Abs(lat) < 0.12f) sw = -sw;
                    if (O.InjuredLeg == sw && BMath.Chance(_rng, 0.5f)) sw = -sw;
                    _swing = sw;
                    _stepT = 0f;
                    _stepDur = BMath.Range(_rng, 0.26f, 0.34f) + (1f - Mathf.Clamp01(Strength(_now))) * 0.08f;
                    _swingFrom[NmCharacter.Si(sw)] = _s.Ankle[NmCharacter.Si(sw)];
                    _planted[NmCharacter.Si(sw)] = false;
                }
            }

            float hipH = _y0 - Sink;
            // Delirium leanInDirection : inclinaison du vecteur up de référence (ou poussée horizontale si LeanAsForce).
            if (O.LeanAmount > 0.005f && O.LeanDir.sqrMagnitude > 1e-4f)
            {
                Vector3 ld = DlBody.Horiz(O.LeanDir);
                if (ld.sqrMagnitude > 1e-4f) LeanVec = ld.normalized * O.LeanAmount;
            }
            for (int sd = -1; sd <= 1; sd += 2)
            {
                int k = NmCharacter.Si(sd);
                var thigh = body[sd < 0 ? Seg.LThigh : Seg.RThigh];
                Vector3 target;
                if (sd == _swing)
                {
                    _stepT += dt;
                    float ph = Mathf.Clamp01(_stepT / _stepDur);
                    Vector3 stance = DlBody.Horiz(_s.Ankle[NmCharacter.Si(-sd)]);
                    Vector3 tgt = apex + _s.Right * (sd * _footWidth);
                    tgt = ClampReach(sd, tgt, _stepClamp);
                    Vector3 from = _swingFrom[k];
                    target = Vector3.Lerp(from, tgt, BMath.SmoothStep(0f, 1f, ph));
                    float lift = DlCfg.StepHeight * Mathf.Sin(Mathf.PI * ph);
                    target.y = _s.FootMinY + 0.05f + lift;
                    if (ph >= 1f || (ph > 0.7f && _s.Contact[k]))
                    {
                        Steps++;
                        _lastSwing = sd;
                        _swing = 0;
                        _planted[k] = true;
                        _ankle[k] = target;
                        _perf.Log(t, "pas " + Steps + (sd < 0 ? " (G)" : " (D)"));
                        if (O.Stagger && Steps >= 2) LowerK = Mathf.Max(8f - 0.02f, LowerK - O.PerStep);
                    }
                }
                else
                {
                    // Jambe d'appui : cheville posée, bassin maintenu à hauteur, pied orienté vers l'avant.
                    target = _ankle[k];
                    target.y = _s.Ankle[k].y;
                    _ankle[k] = target;
                }
                LegTo(sd, target, hipH, dt, LegBuckle[k]);
            }

            // Répartition du déséquilibre latéral entre les deux hanches et le rachis (Delirium : over-range sharing).
            float overL = HipOver(-1), overR = HipOver(1);
            float aO = Mathf.Max(overL, -overR), bO = Mathf.Max(overR, -overL);
            if (aO > 0f && bO <= 0f)
            {
                _ch.SetDesired((int)JointId.RHip, 1, _ch.Desired((int)JointId.RHip).y + aO);
                if (_planted[0]) _ch.SetDesired((int)JointId.LHip, 1, _ch.Desired((int)JointId.LHip).y - aO);
                _ch.SetDesired((int)JointId.Lumbar, 1, _ch.Desired((int)JointId.Lumbar).y - aO);
            }
            else if (bO > 0f && aO <= 0f)
            {
                _ch.SetDesired((int)JointId.LHip, 1, _ch.Desired((int)JointId.LHip).y + bO);
                if (_planted[1]) _ch.SetDesired((int)JointId.RHip, 1, _ch.Desired((int)JointId.RHip).y - bO);
                _ch.SetDesired((int)JointId.Lumbar, 1, _ch.Desired((int)JointId.Lumbar).y - bO);
            }

            // Contrôle du bassin : inclinaison avant/arrière et latérale d'après le déplacement du CdM par rapport
            // aux appuis (Delirium : pelvis_control), transmise au rachis et à l'inclinaison des hanches.
            Vector3 support = SupportPoint(comH);
            float ahead = Vector3.Dot(comH - support, _s.Fwd);
            float side = Vector3.Dot(comH - support, _s.Right);
            float vAhead = Vector3.Dot(velH, _s.Fwd), vSide = Vector3.Dot(velH, _s.Right);
            float leanF = Mathf.Clamp(ahead * 1.2f + vAhead * 0.12f + AccBias * 0.4f, -0.45f, 0.45f);
            float leanS = Mathf.Clamp(side * 1.5f + vSide * 0.15f, -0.5f, 0.5f);
            _p52 = Mathf.Lerp(_p52, leanS, 1f - Mathf.Exp(-dt * 8f));
            _p56 = Mathf.Lerp(_p56, leanF, 1f - Mathf.Exp(-dt * 8f));
            _ch.SetDesired((int)JointId.Lumbar, 1, Mathf.Clamp(_ch.Desired((int)JointId.Lumbar).y + _p52 * 0.5f, -0.5f, 0.5f));
            // Delirium applique l'inclinaison du bassin au rachis et à la cheville ; sans articulation de cheville
            // sur le ragdoll EFT, elle va au rachis seul pour ne pas combattre la cinématique inverse des jambes.
            _ch.SetDesired((int)JointId.Lumbar, 0, _ch.Desired((int)JointId.Lumbar).x + _p56 * 0.5f);
            // Ne réécrase pas la raideur des jambes qui se dérobent (LegBuckle / KneeScale).
            for (int sd = -1; sd <= 1; sd += 2)
            {
                int k = NmCharacter.Si(sd);
                float buckle = LegBuckle[k];
                float wk = (O.InjuredLeg == sd ? 0.7f : 1f) * Mathf.Lerp(1f, 0.35f, buckle);
                float knk = LowerK * wk * KneeScale[k] * Mathf.Lerp(1f, 0.22f, buckle);
                _ch.SetStiffness(AnatomyData.Hip(sd), LowerK * wk, 1f);
                _ch.SetStiffness(AnatomyData.Knee(sd), Mathf.Max(1.8f, knk), 1f);
            }

            // staggerFall : inclinaison dans le sens de la marche (leanInDirection) et flexion du rachis avec la vitesse.
            if (O.Stagger)
            {
                float vf = Vector3.Dot(velH, _s.Fwd);
                float max = vf >= 0f ? 0.10f : 0.30f;
                _lean = Mathf.Min(max, _lean + dt);
                if (velH.sqrMagnitude > 1e-4f) LeanVec = velH.normalized * _lean;
                float bend = Mathf.Clamp(0.4f * vf, -0.3f, 0.4f);
                _ch.SetDesired((int)JointId.Lumbar, 0, _ch.Desired((int)JointId.Lumbar).x + bend);
            }
        }

        private void UpdateBuckle(float t, float giveWay)
        {
            float elapsed = t - _start;
            float b0 = giveWay, b1 = giveWay;
            if (O.BuckleMode > 0 && elapsed >= O.BuckleDelay)
            {
                int firstSide = O.BuckleLeg != 0 ? O.BuckleLeg : (O.InjuredLeg != 0 ? O.InjuredLeg : 1);
                int firstIdx = NmCharacter.Si(firstSide);
                int secondIdx = 1 - firstIdx;
                float g1 = BMath.SmoothStep(O.BuckleDelay, O.BuckleDelay + 0.18f, elapsed);
                if (O.BuckleMode == 1)
                {
                    if (firstIdx == 0) b0 = Mathf.Max(b0, g1); else b1 = Mathf.Max(b1, g1);
                }
                else if (O.BuckleMode == 2)
                {
                    b0 = Mathf.Max(b0, g1);
                    b1 = Mathf.Max(b1, g1);
                }
                else if (O.BuckleMode == 3)
                {
                    float g2 = BMath.SmoothStep(O.BuckleDelay + O.BuckleStaggerDelay, O.BuckleDelay + O.BuckleStaggerDelay + 0.18f, elapsed);
                    if (firstIdx == 0) { b0 = Mathf.Max(b0, g1); b1 = Mathf.Max(b1, g2); }
                    else { b1 = Mathf.Max(b1, g1); b0 = Mathf.Max(b0, g2); }
                }
                if (!_buckleLogged && g1 > 0.1f)
                {
                    _buckleLogged = true;
                    _perf.Log(t, O.BuckleMode == 1 ? $"dérobement jambe {(firstSide < 0 ? "G" : "D")}"
                        : O.BuckleMode == 2 ? "dérobement deux jambes" : "dérobement asymétrique (2 temps)");
                }
            }
            LegBuckle[0] = Mathf.Max(_shotCollapsed[0] ? LegBuckle[0] : 0f, b0);
            LegBuckle[1] = Mathf.Max(_shotCollapsed[1] ? LegBuckle[1] : 0f, b1);
        }

        /// <summary>Delirium : reach() borne la cible du pas dans le cône d'amplitude de la hanche.</summary>
        private Vector3 ClampReach(int sd, Vector3 target, float clamp)
        {
            int hip = (int)AnatomyData.Hip(sd);
            Vector3 h = _b.JointPos((JointId)hip);
            Vector3 d = target - h;
            float legLen = _legLength * 0.95f;
            float maxH = legLen * clamp * 0.85f;
            Vector3 dh = DlBody.Horiz(d);
            if (dh.magnitude > maxH) dh = dh.normalized * maxH;
            float maxV = 0.12f;
            Vector3 dv = Vector3.up * Mathf.Clamp(d.y, -legLen, maxV);
            Vector3 r = dh + dv;
            if (r.magnitude > legLen) r = r.normalized * legLen;
            return h + r;
        }

        /// <summary>Delirium nmdbm.rs:806-810 : dépassement elliptique (flexion + abduction) de la hanche.</summary>
        private float HipOver(int sd)
        {
            var hip = AnatomyData.Hip(sd);
            int j = (int)hip;
            DlCfg.Range(j, 0, out float lo0, out float hi0);
            DlCfg.Range(j, 1, out float lo1, out float hi1);
            float mid0 = 0.5f * (lo0 + hi0), half0 = Mathf.Max(1e-4f, 0.5f * (hi0 - lo0));
            float mid1 = 0.5f * (lo1 + hi1), half1 = Mathf.Max(1e-4f, 0.5f * (hi1 - lo1));
            var d = _ch.Desired(j);
            float l1 = (d.x - mid0) / half0;
            float l2 = (d.y - mid1) / half1;
            float e = Mathf.Sqrt(l1 * l1 + l2 * l2);
            return Mathf.Max(0f, e - 1f) / (e + 1e-4f) * (d.y - mid1);
        }

        private void LegTo(int sd, Vector3 ankle, float hipH, float dt, float buckle)
        {
            var body = _b.Body;
            var thigh = body[sd < 0 ? Seg.LThigh : Seg.RThigh];
            var calf = body[sd < 0 ? Seg.LCalf : Seg.RCalf];
            var jm = AnatomyData.Hip(sd);
            var km = AnatomyData.Knee(sd);
            if (!Nm.LegIK(body, sd, thigh.Pos, ankle, _s.Right, Vector3.up, _s.Fwd, out float f, out float ab, out float kn))
                return;
            int k = NmCharacter.Si(sd);
            float wk = (O.InjuredLeg == sd ? 0.7f : 1f) * Mathf.Lerp(1f, 0.35f, buckle);
            float knk = LowerK * wk * KneeScale[k] * Mathf.Lerp(1f, 0.22f, buckle);
            _ch.SetStiffness(jm, LowerK * wk, 1f);
            _ch.SetStiffness(km, Mathf.Max(1.8f, knk), 1f);
            // Quand la jambe se dérobe, le genou se referme franchement et la hanche fléchit sous le poids.
            float extraKneeDeg = buckle * 52f;
            float extraHipDeg = buckle * 20f;
            _ch.SetDesired((int)jm, 0, (f + extraHipDeg) * BMath.Deg);
            _ch.SetDesired((int)jm, 1, ab * BMath.Deg);
            _ch.SetDesired((int)km, 0, Mathf.Max(kn + extraKneeDeg, 5f) * BMath.Deg);
            // Orientation du pied : la cheville suit la direction du déplacement (yaw) et la pente locale.
            Vector3 dir = DlBody.Horiz(_s.Vel);
            float yaw = 0f;
            if (dir.sqrMagnitude > 0.04f)
            {
                dir.Normalize();
                yaw = BMath.SignedAngleOnPlane(_s.Fwd, dir, Vector3.up);
            }
            float cur = k == 0 ? _yaw0 : _yaw1;
            cur = Mathf.Lerp(cur, Mathf.Clamp(yaw, -0.9f, 0.9f), 1f - Mathf.Exp(-8f * Mathf.Max(1e-3f, dt)));
            if (k == 0) _yaw0 = cur; else _yaw1 = cur;
            _ch.SetDesired((int)jm, 2, cur * 0.5f);
        }

        /// <summary>Point d'appui effectif : projection du CdM sur le segment entre les deux pieds.</summary>
        private Vector3 SupportPoint(Vector3 comH)
        {
            Vector3 a = DlBody.Horiz(_s.Ankle[0]), c = DlBody.Horiz(_s.Ankle[1]);
            if (!_s.Contact[0] && !_s.Contact[1]) return comH;
            if (_s.Contact[0] && !_s.Contact[1]) return a;
            if (!_s.Contact[0] && _s.Contact[1]) return c;
            Vector3 ac = c - a;
            float u = Mathf.Clamp01(Vector3.Dot(comH - a, ac) / Mathf.Max(1e-6f, ac.sqrMagnitude));
            return a + ac * u;
        }

        // ------------------------------------------------------------------ forces (pelvis_control + stabilise)

        /// <summary>
        /// Forces du balancier. Ce sont les jambes (drives + appuis) qui portent le corps : appliquer une poussée
        /// verticale de corps entier reviendrait à faire léviter le mourant, qui glisserait alors sur place au lieu
        /// de tomber. Le balancier ne fournit donc qu'un frein horizontal, au contact, et le redressement du tronc.
        /// </summary>
        public void Forces(float t, float dt)
        {
            if (!Active) return;
            float s = Strength(t);
            if (s <= 0.005f) return;
            var body = _b.Body;
            var pelvis = body[Seg.Pelvis];
            var thorax = body[Seg.Thorax];
            float g = Mathf.Max(1f, -Physics.gravity.y);
            float M = Mathf.Max(20f, body.TotalMass);

            // Vrille de pivot : le corps tourne sur lui-même avant que l'équilibre ne cède.
            if (O.YawAccel != 0f)
            {
                float x = t - _start;
                if (x >= 0f && x < 0.45f)
                    NmPhysics.BodyAngularAccel(body, Vector3.up * (O.YawAccel * Mathf.Sin(Mathf.PI * x / 0.45f) * s));
            }

            // Effondrement assis : le bassin descend sous le poids du corps pendant que les hanches plient.
            if (O.Sit)
            {
                float sit = Mathf.Clamp01((t - _start) / 0.5f);
                if (sit > 0.01f) pelvis.Rb.AddForce(Vector3.down * (0.35f * sit * M * g * s), ForceMode.Force);
            }

            // Dérobement d'une ou deux jambes (balle dans le dos, balle dans la jambe, ou effondrement des genoux) :
            // le quadriceps n'encaisse plus la charge, le bassin descend et bascule du côté qui cède si asymétrique.
            float bMax = Mathf.Max(LegBuckle[0], LegBuckle[1]);
            float bAvg = 0.5f * (LegBuckle[0] + LegBuckle[1]);
            if (bMax > 0.01f)
            {
                float drop = Mathf.Max(0f, _y0 - pelvis.Com.y);
                float load = 0.55f * bAvg * (1f - Mathf.Clamp01(drop / 0.28f)) * M * g * s;
                if (load > 0.1f)
                {
                    pelvis.Rb.AddForce(Vector3.down * load, ForceMode.Force);
                    thorax.Rb.AddForce(Vector3.down * (load * 0.35f), ForceMode.Force);
                }
                float asym = LegBuckle[1] - LegBuckle[0];
                if (Mathf.Abs(asym) > 0.1f)
                {
                    // Une seule jambe cède : le bassin s'affaisse de ce côté (roulis frontal naturel).
                    Vector3 sideRoll = -_s.Fwd * (asym * 28f * s);
                    pelvis.Rb.AddTorque(sideRoll, ForceMode.Force);
                }
            }

            // Delirium leanInDirection (applyAsForce) : poussée horizontale douce sur le bassin au contact.
            if (O.LeanAsForce && LeanVec.sqrMagnitude > 1e-5f && (_s.Contact[0] || _s.Contact[1]))
            {
                Vector3 uLean = (Vector3.up + DlBody.Horiz(LeanVec)).normalized;
                Vector3 gVec = Vector3.down * (g * M);
                Vector3 fLean = DlBody.Horiz(gVec - uLean * Vector3.Dot(uLean, gVec));
                pelvis.Rb.AddForce(Vector3.ClampMagnitude(fLean * s, 0.28f * M * g), ForceMode.Force);
            }

            // Stabilisation horizontale (Delirium nmdbm::stabilise) + frein borné, uniquement quand un pied appuie.
            if (_s.Contact[0] || _s.Contact[1])
            {
                Vector3 midFoot = 0.5f * (_s.Ankle[0] + _s.Ankle[1]);
                Vector3 dCom = _s.Com - midFoot;
                float cUp = Mathf.Max(0f, Vector3.Dot(dCom.normalized, Vector3.up));
                float c4 = cUp * cUp * cUp * cUp;
                float stabK = (1f - 0.7f * bAvg) * s * c4;
                Vector3 vH = NmCharacter.Flat(_s.Vel);
                Vector3 fStab = -DlBody.Horiz(dCom + vH * 0.12f) * (24f * stabK) - vH * (35f * s);
                Vector3 fH = Vector3.ClampMagnitude(fStab, 0.16f * M * g * s);
                pelvis.Rb.AddForce(fH * 0.7f, ForceMode.Force);
                thorax.Rb.AddForce(fH * 0.3f, ForceMode.Force);
            }

            // Redressement du tronc en couple réel : inertie du haut du corps autour des hanches. Le couple est borné
            // pour que le tronc puisse réellement basculer quand le balancier renonce.
            Vector3 upT = (Vector3.up + LeanVec).normalized;
            float Iub = 0.5f;
            foreach (var id in new[] { Seg.Pelvis, Seg.Thorax, Seg.Head, Seg.LUpperArm, Seg.RUpperArm, Seg.LForearm, Seg.RForearm })
                Iub += body[id].Mass * (body[id].Com - pelvis.Com).sqrMagnitude;
            float ls = _perf.LastStandUntil > t ? 1.6f : 1f;
            Upright(thorax, upT, Iub * 0.25f * s * ls, dt);
            Upright(pelvis, Vector3.Slerp(Vector3.up, upT, 0.5f), Iub * 0.2f * s * ls, dt);
        }

        private static void Upright(Segment sg, Vector3 target, float gain, float dt)
        {
            Vector3 err = Vector3.Cross(sg.U, target);
            Vector3 wH = sg.W - Vector3.Project(sg.W, Vector3.up);
            float iSeg = Mathf.Max(0.05f, (sg.InertiaDiag.x + sg.InertiaDiag.y + sg.InertiaDiag.z) / 3f);
            float kp = Mathf.Min(60f * gain, dt > 1e-4f ? 0.3f * iSeg / (dt * dt) : 60f * gain);
            float kd = Mathf.Min(14f * gain, dt > 1e-4f ? 0.9f * iSeg / dt : 14f * gain);
            Vector3 tq = Vector3.ClampMagnitude(kp * err - kd * wH, 90f);
            sg.Rb.AddTorque(tq, ForceMode.Force);
        }
    }
}
