using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Euphoria
{
    /// <summary>Paramètres du message <c>shot</c> (behaviours.xml, valeurs par défaut de NaturalMotion).</summary>
    internal sealed class DlShotParams
    {
        public float BodyStiffness = DlCfg.ShotBodyStiffness;
        public float ArmStiffness = DlCfg.ShotArmStiffness;
        public float NeckStiffness = DlCfg.ShotNeckStiffness;
        public float LoosenessAmount = 1f;
        public float GrabHoldTime = DlCfg.ShotGrabHoldTime;
        public float TimeBeforeReach = DlCfg.ShotTimeBeforeReach;
        public float SpinePainTime = DlCfg.ShotSpinePainTime;
        public float SpinePainMultiplier = 1f;
        public float SpinePainTwistMultiplier = 0.5f;
        public float TimeBeforeCollapseLeg = DlCfg.ShotTimeBeforeCollapseLeg;
        public float HeadLookAtWoundMin = 0.25f, HeadLookAtWoundMax = 0.8f;
        public float HeadLookAtHeadMin = 0.6f, HeadLookAtHeadMax = 1.7f;
        public float ShockSpinMin = 50f, ShockSpinMax = 90f, ShockSpinDecay = 4f, ShockSpinScalePerComponent = 0.5f;
        public float LegStiffness = DlCfg.BalanceLegStiffness;
        public float FallTime = 1.2f;
        public bool ReachForWound = true, Fling = true, AddShockSpin, UseCatchFallOnFall = true, UseHeadLook = true;
    }

    /// <summary>
    /// Comportement <c>shot</c>, porté depuis <c>nmshot::Shot</c> de Delirium.
    ///
    /// Delirium n'a pas une seule « réaction au tir » : c'est une machine à sous-comportements, chacun avec son
    /// minuteur et sa propre réécriture des effecteurs. Sont portés ici ceux qui comptent sur un ragdoll de mort
    /// (les autres — melee, chickenArms, crouch, blind fire — n'ont pas de sens sur un cadavre SPT) :
    ///
    ///   hit     — mémorise le point, le côté et l'orientation du tir, choisit le bras qui peut se porter sur la plaie ;
    ///   stiff   — relâchement initial (looseness) puis retour progressif de la raideur, oppose_gravity sur le tronc,
    ///             chevilles raidies la première moitié de la chute, genoux qui s'allègent à l'impact ;
    ///   react   — les bras réagissent à la vitesse relative du tronc (Delirium : react_tick) ;
    ///   fall    — la raideur des bras s'éteint, le tronc se relâche ;
    ///   reach   — la main va sur la plaie (cinématique inverse, grabHoldTime) ;
    ///   fling   — le bras est projeté dans l'axe du projectile ;
    ///   collapse — la jambe touchée se dérobe après timeBeforeCollapseWoundLeg ;
    ///   look    — le cou alterne plaie / tireur ;
    ///   spin    — vrille de choc autour de l'axe du tronc (shotShockSpin).
    /// </summary>
    internal sealed class DlShot
    {
        public readonly DlShotParams P;
        public NmHit Wound;
        public bool Active = true;
        public bool Fallen;
        public float FallT0 = -1f;
        public float Side, Behind;
        public int ReachSide, FlingSide;
        /// <summary>Bras qui se portent sur la plaie (0 = gauche, 1 = droite), comme Delirium REACH_L / REACH_R.</summary>
        public readonly bool[] ReachArms = new bool[2];
        /// <summary>Bras blessé qui lâche (Delirium : LOOSE_L / LOOSE_R).</summary>
        public readonly bool[] LooseArms = new bool[2];
        public int CollapseLeg;
        public readonly bool[] CollapseLegs = new bool[2];
        public bool ReachedOnce;
        /// <summary>Dernière magnitude de couple shockSpin calculée (N·m, pour tests et diagnostic).</summary>
        public float LastShockSpinTorque { get; private set; }

        private readonly DlBody _b;
        private readonly DlCharacter _ch;
        private readonly NmCharacter _s;
        private readonly NmPerformance _perf;
        private readonly System.Random _rng;
        private float _hitT, _since, _loose = 1f, _lookT, _lookDur;
        private readonly float[] _reachT = new float[2];
        private readonly Vector3[] _pullTarget = new Vector3[2];
        private readonly float[] _pullStrength = new float[2];
        private readonly bool[] _pullActive = new bool[2];
        private float _reactPhase, _reactElbow, _giveWayStagger;
        private int _giveWayFirst = 1;
        private bool _lookWound = true, _fallEntered;
        private Vector3 _spinAxis;
        private float _spin, _spinBlend;
        private bool _spinInit;

        public DlShot(NmPerformance perf, float t, DlShotParams p)
        {
            _perf = perf;
            _b = perf.Dl;
            _ch = perf.Eff;
            _s = perf.Ch;
            _rng = perf.Rng;
            P = p;
            _hitT = t;
            _lookT = t;
        }

        public void Deactivate() => Active = false;

        /// <summary>shotNewBullet : nouveau projectile, même si le comportement tourne déjà.</summary>
        public void NewHit(float t, NmHit hit, bool fallen)
        {
            Wound = hit;
            _hitT = t;
            _since = 0f;
            Fallen = fallen;
            Side = hit.Side;
            // Positif quand la balle vient du dos : Delirium calcule behind = -dot(bullet_vel, m.c) et son repère
            // porte m.c vers l'arrière ; avec le vecteur avant du ragdoll EFT, le signe s'inverse.
            Behind = Vector3.Dot(hit.Dir, _s.Fwd);
            _loose = fallen ? 0.35f : P.LoosenessAmount;
            ReachSide = 0;
            FlingSide = 0;
            ReachArms[0] = ReachArms[1] = false;
            _pullActive[0] = _pullActive[1] = false;
            _reactElbow = BMath.Range(_rng, 0.35f, 1.15f);
            _reactPhase = Behind > 0f ? BMath.Range(_rng, -2f, 2f) : BMath.Range(_rng, -0.5f, 0.5f);
            _giveWayFirst = BMath.Chance(_rng, 0.5f) ? -1 : 1;
            _giveWayStagger = BMath.Range(_rng, 0.06f, 0.14f);

            // Bras touché → LOOSE_L / LOOSE_R (Delirium nmshot.rs:611-616).
            if (hit.ArmSide < 0) LooseArms[0] = true;
            if (hit.ArmSide > 0) LooseArms[1] = true;

            // Choix des bras qui se portent sur la plaie (Delirium nmshot.rs:610-640) : 1 ou 2 mains selon la zone,
            // jamais le bras touché lui-même ni un bras détruit. Un tir axial au tronc ou à la cuisse peut mobiliser
            // les deux mains (la deuxième décalée de 0.18 s).
            bool thighHit = hit.Part != null && (hit.Part.Id == Seg.LThigh || hit.Part.Id == Seg.RThigh);
            if (P.ReachForWound && (hit.Reachable || thighHit) && Behind < 0.25f)
            {
                bool r7 = (hit.Side >= 0 || thighHit || hit.ArmSide > 0) && hit.ArmSide >= 0 && !LooseArms[0] && Nm.ElbowCap(_b.Body, -1) >= 0.25f;
                bool r8 = (hit.Side <= 0 || thighHit || hit.ArmSide < 0) && hit.ArmSide <= 0 && !LooseArms[1] && Nm.ElbowCap(_b.Body, 1) >= 0.25f;
                // Si la plaie est latérale, privilégier le bras controlatéral ou les deux mains sur tir central/ventral.
                if (r7 && r8 && hit.Side != 0 && !thighHit && BMath.Chance(_rng, 0.55f))
                {
                    if (hit.Side < 0) r7 = false; else r8 = false;
                }
                if (r7) { ReachArms[0] = true; _reachT[0] = 0f; ReachSide = -1; }
                if (r8) { ReachArms[1] = true; _reachT[1] = r7 ? -0.18f : 0f; if (ReachSide == 0) ReachSide = 1; }
            }
            if (P.Fling)
            {
                int side = ReachSide != 0 ? -ReachSide : (hit.Side != 0 ? hit.Side : (BMath.Chance(_rng, 0.5f) ? -1 : 1));
                if (Nm.ElbowCap(_b.Body, side) >= 0.25f && !ReachArms[NmCharacter.Si(side)]) FlingSide = side;
            }
            if (hit.LegSide != 0)
            {
                CollapseLeg = hit.LegSide;
                CollapseLegs[NmCharacter.Si(hit.LegSide)] = true;
            }

            // shotShockSpin (Delirium nmshot.rs:533-576) : couple en N·m proportionnel au bras de levier
            // horizontal cross(bullet_dir, hit - com) · up (et NON multiplié par Deg2Rad !).
            if (P.AddShockSpin)
            {
                _spinInit = true;
                _spinBlend = 1f;
                Vector3 d = DlBody.Horiz(hit.World - _s.Com);
                Vector3 x = Vector3.Cross(BMath.SafeNormalize(hit.Dir, _s.Fwd), d);
                float lever = -Vector3.Dot(x, Vector3.up);
                float mag = BMath.Range(_rng, P.ShockSpinMin, P.ShockSpinMax);
                // Un tir excentré (épaule, flanc) donne un bras de levier ~0.18-0.30 m → couple franc ;
                // un tir presque axial conserve une légère vrille minimale (±0.25).
                float scale = Mathf.Abs(lever) > 0.04f ? Mathf.Clamp(lever * 3.5f, -1f, 1f) : (lever >= 0f ? 0.25f : -0.25f);
                _spin = mag * scale;
                LastShockSpinTorque = _spin;
                _spinAxis = BMath.SafeNormalize(_b.JointPos(JointId.Lumbar) - _b.Part(Seg.Pelvis).Pos, Vector3.up);
            }
            _perf.Log(t, "shot (relâchement, réaction, plaie)");
        }

        public void Tick(float t, float dt, float weight)
        {
            if (!Active) return;
            _since += dt;
            _reachT[0] += dt;
            _reachT[1] += dt;
            _pullActive[0] = _pullActive[1] = false;
            float u = Mathf.Clamp01(weight);
            if (u <= 0.01f) return;

            // Minuteur de chute : après FallTime, le corps ne se retient plus (Delirium : sous-comportement fall).
            if (Fallen && FallT0 < 0f) FallT0 = t;
            float x = FallT0 >= 0f ? t - FallT0 : Mathf.Max(0f, _since - 2.5f);
            float ratio = Mathf.Clamp01(x / P.FallTime);
            float fall = 1f - Mathf.Clamp01(x / (P.FallTime + 4f));
            float fallHold = ratio <= 0.5f ? 1f : 0f;

            // Delirium nmshot.rs:483-493 : STIFF et REACT s'arrêtent dès que le personnage perd l'équilibre (failed),
            // et FALL prend le relais (au lieu que STIFF réécrase les raideurs de FALL à chaque pas).
            SpinePain(u);
            if (!Fallen)
            {
                Stiff(fall, fallHold, u, dt);
                React(t, dt, u);
            }
            else
            {
                Fall(t, ratio, u);
            }
            LooseArm(u);
            Fling(t, u);
            Reach(t, u);
            Collapse(t, u);
            GiveWay(u);
            Look(t, dt, u);
        }

        // ------------------------------------------------------------------ sous-comportements

        private void SpinePain(float u)
        {
            // Douleur du rachis (Delirium nmshot.rs:445-461) : le tronc se replie vers la plaie puis revient.
            if (Wound == null || P.SpinePainTime <= 0f) return;
            float k = (P.SpinePainTime - _since) / P.SpinePainTime;
            if (k <= 0f) return;
            float lean = P.SpinePainMultiplier * k;
            if (Behind > 0f) lean *= 0.5f;
            if (Wound != null && Wound.LegSide != 0) lean *= 0.65f;
            float tw = Side * P.SpinePainTwistMultiplier * k * -0.35f;
            Ch_SpineLean(lean, tw, u);
            _ch.BlendToZero("us", Mathf.Clamp01(1f - lean));
        }

        private void Stiff(float fall, float fallHold, float u, float dt)
        {
            float og = (fall + 1f) * 0.5f;
            for (int k = 0; k < 2; k++)
            {
                int sd = k == 0 ? -1 : 1;
                if (!_ch.Busy[k])
                {
                    _ch.SetOpposeGravity((int)AnatomyData.Shoulder(sd), og);
                    _ch.SetOpposeGravity((int)AnatomyData.Elbow(sd), og);
                }
                _ch.SetOpposeGravity((int)AnatomyData.Hip(sd), og);
                _ch.SetOpposeGravity((int)AnatomyData.Knee(sd), og);
            }
            // Le rachis n'est pas soulagé : ce serait annuler la gravité du tronc et empêcher la chute.
            _ch.SetOpposeGravity((int)JointId.Lumbar, 0f);
            _ch.SetOpposeGravity((int)JointId.Neck, 0f);

            float loose = Mathf.Clamp01(P.LoosenessAmount * _loose + (1f - P.LoosenessAmount));
            _loose = Mathf.Min(1f, _loose + 0.35f * dt);
            float ms = Mathf.Lerp(0.5f, 1f, loose);

            _ch.SetStiffness("us", P.BodyStiffness * og * u, 0.5f * loose, ms * loose);
            if (!_ch.Busy[0]) _ch.SetStiffness("ul", P.ArmStiffness * og * u, 1f * loose, Mathf.Max(DlCfg.ShotArmLooseness, ms * 0.5f));
            if (!_ch.Busy[1]) _ch.SetStiffness("ur", P.ArmStiffness * og * u, 1f * loose, Mathf.Max(DlCfg.ShotArmLooseness, ms * 0.5f));
            _ch.SetStiffness("un", P.NeckStiffness * og * u, 0.5f * loose, ms * 0.5f);
            // Une balle dans le dos ne raidit pas les jambes : c'est justement ce qui faisait basculer le corps en bloc.
            float legHold = Wound != null && Wound.Torso && Behind > 0.2f ? fallHold * 0.25f : fallHold;
            for (int k = 0; k < 2; k++)
            {
                if (CollapseLegs[k]) continue; // ne réécrase pas une jambe qui se dérobe
                int sd = k == 0 ? -1 : 1;
                _ch.SetStiffness(AnatomyData.Hip(sd), Mathf.Lerp(4f, P.LegStiffness, legHold), (legHold + 1f) * 0.5f);
                _ch.SetStiffness(AnatomyData.Knee(sd), Mathf.Lerp(4f, P.LegStiffness, legHold), (legHold + 1f) * 0.5f);
                _ch.SetOpposeGravity((int)AnatomyData.Hip(sd), legHold);
                _ch.SetOpposeGravity((int)AnatomyData.Knee(sd), legHold);
            }

            // Genoux qui s'allègent à l'impact (Delirium : muscle_stiffness = fall·2 + 0.5 quand fall < 0.5).
            if (fall < 0.5f)
            {
                float km = fall * 2f + 0.5f;
                _ch.SetMuscleStiffness((int)AnatomyData.Knee(-1), km);
                _ch.SetMuscleStiffness((int)AnatomyData.Knee(1), km);
            }
        }

        /// <summary>
        /// Jambes qui cèdent sous une balle dans le dos : le bassin descend sur les genoux au lieu de basculer sur des
        /// jambes tendues (Delirium : shotFromBehind). Décalage léger entre les deux genoux pour un effondrement organique.
        /// </summary>
        private void GiveWay(float u)
        {
            if (Wound == null || !Wound.Torso || Behind < 0.2f) return;
            for (int sd = -1; sd <= 1; sd += 2)
            {
                float delay = sd == _giveWayFirst ? 0.06f : 0.06f + _giveWayStagger;
                float x = _since - delay;
                if (x <= 0f) continue;
                float w = Mathf.Clamp01(x / 0.22f) * u;
                int hip = (int)AnatomyData.Hip(sd), knee = (int)AnatomyData.Knee(sd);
                _ch.SetRelaxation(hip, 1f - 0.55f * w);
                _ch.SetRelaxation(knee, 1f - 0.85f * w);
                _ch.BlendDesired(hip, 0, Mathf.Max(_ch.Desired(hip).x, 0.24f * w), w);
                _ch.BlendDesired(knee, 0, Mathf.Max(_ch.Desired(knee).x, 0.72f * w), w);
                _perf.Balance?.CollapseLeg(sd, w * 0.9f);
            }
        }

        private void Ch_SpineLean(float lean, float tw, float u)
        {
            int lu = (int)JointId.Lumbar;
            DlCfg.Range(lu, 0, out float lo0, out float hi0);
            DlCfg.Range(lu, 1, out float lo1, out float hi1);
            DlCfg.Range(lu, 2, out float lo2, out float hi2);
            _ch.BlendDesired(lu, 0, 0.5f * (lo0 + hi0) + lean * 0.6f, u);
            _ch.BlendDesired(lu, 1, 0.5f * (lo1 + hi1) + tw * 0.5f, u);
            _ch.BlendDesired(lu, 2, 0.5f * (lo2 + hi2) + tw, u);
        }

        /// <summary>react_tick de Delirium (nmshot.rs:727-773) : sursaut sin/cos des bras sur la vitesse du thorax.</summary>
        private void React(float t, float dt, float u)
        {
            if (_since > 0.55f) return;
            float env = 1f - Mathf.Clamp01(_since / 0.55f);
            var sp = _b.Part(Seg.Thorax);
            float va = Vector3.Dot(sp.V, _s.Right);
            float vc = Vector3.Dot(sp.V, _s.Fwd);
            float f28 = Deadzone(va - vc, 0.12f);
            float f27 = Deadzone(-vc - va, 0.12f);
            float f25 = Mathf.Min(1f, Mathf.Abs(f28) * 0.55f + 0.22f * env);
            float f23 = Mathf.Min(1f, Mathf.Abs(f27) * 0.55f + 0.22f * env);
            float ph = _since * 4.5f + _reactPhase + (Behind < 0f ? 0.5f : -0.6f);
            float f24 = -Mathf.Sin(ph) * 0.85f;
            float s = -0.85f * Mathf.Cos(ph);
            float f22 = f28 < 0f ? -s : s;
            float f12 = f27 < 0f ? -s : s;

            for (int k = 0; k < 2; k++)
            {
                if (_ch.Busy[k] || LooseArms[k]) continue;
                int sd = k == 0 ? -1 : 1;
                int sh = (int)AnatomyData.Shoulder(sd), el = (int)AnatomyData.Elbow(sd);
                float fk = k == 0 ? f22 : f12;
                float amp = (k == 0 ? f25 : f23) * env * u;
                if (amp <= 0.02f) continue;
                _ch.BlendDesired(sh, 0, _ch.Desired(sh).x + fk * amp * 0.85f, amp);
                _ch.BlendDesired(sh, 1, _ch.Desired(sh).y + (0.45f + 0.3f * Mathf.Abs(f24)) * amp, amp);
                _ch.BlendDesired(sh, 2, (f24 * 0.4f - 0.25f) * Motor.InternalRotSign(sd) * amp, amp);
                _ch.BlendDesired(el, 0, Mathf.Clamp(_reactElbow + (fk + 1f) * 0.35f * amp, 0.15f, 2.2f), amp);
                // Légère pronation réflexe pendant le sursaut.
                _ch.BlendDesired(el, 2, 0.45f * Motor.InternalRotSign(sd) * amp, amp * 0.7f);
                _ch.SetOpposeGravity(sh, 0f);
                _ch.SetOpposeGravity(el, 0f);
            }
        }

        /// <summary>LOOSE_L / LOOSE_R de Delirium (nmshot.rs:775-781) : le bras touché devient mou et pend.</summary>
        private void LooseArm(float u)
        {
            if (_since < 0.25f) return;
            for (int k = 0; k < 2; k++)
            {
                if (!LooseArms[k] || _ch.Busy[k]) continue;
                int sd = k == 0 ? -1 : 1;
                int sh = (int)AnatomyData.Shoulder(sd), el = (int)AnatomyData.Elbow(sd);
                _ch.SetRelaxation(sh, 0.22f);
                _ch.SetRelaxation(el, 0.18f);
                _ch.SetOpposeGravity(sh, 0f);
                _ch.SetOpposeGravity(el, 0f);
                _ch.BlendDesired(sh, 0, 0.15f, 0.6f * u);
                _ch.BlendDesired(sh, 1, 0.08f, 0.6f * u);
                _ch.BlendDesired(el, 0, 0.45f, 0.6f * u);
                // Pronation naturelle d'un bras pendant relâché.
                _ch.BlendDesired(el, 2, 0.55f * Motor.InternalRotSign(sd), 0.5f * u);
            }
        }

        /// <summary>fall_enter + fall_tick de Delirium (nmshot.rs:783-815) : capture la pose puis relâche bras et tronc.</summary>
        private void Fall(float t, float ratio, float u)
        {
            if (!_fallEntered)
            {
                _fallEntered = true;
                for (int j = 0; j < DlCharacter.NJ; j++)
                    _ch.SetDesired(j, 0, _ch.Actual(j, _b.Body).x);
                _ch.SetStiffness("lb", 5f, 0.7f);
                _ch.SetStiffness("ub", 9f, 0.7f);
            }
            if (ratio <= 0f) return;
            float k = Mathf.Clamp01(ratio) * u;
            for (int i = 0; i < 2; i++)
            {
                if (_ch.Busy[i]) continue;
                int sd = i == 0 ? -1 : 1;
                _ch.SetRelaxation((int)AnatomyData.Shoulder(sd), 1f - 0.75f * k);
                _ch.SetRelaxation((int)AnatomyData.Elbow(sd), 1f - 0.75f * k);
            }
            _ch.BlendToZero("us", 0.5f * k);
        }

        /// <summary>
        /// reach_tick de Delirium (nmshot.rs:1018-1109) : 1 ou 2 mains vers la plaie par IK, pronation de l'avant-bras
        /// face à la normale de la plaie (Shot::wrist) et force d'agrippement réelle (Shot::pull) appliquée en Forces().
        /// </summary>
        private void Reach(float t, float u)
        {
            if (Wound == null) return;
            var th = _b.Part(Seg.Thorax);
            Vector3 woundWorld = Wound.World;
            Vector3 woundNormal = BMath.SafeNormalize(woundWorld - th.Com, th.F);
            for (int i = 0; i < 2; i++)
            {
                if (!ReachArms[i] || _ch.Busy[i]) continue;
                float rt = _reachT[i];
                if (rt < P.TimeBeforeReach || rt > P.TimeBeforeReach + P.GrabHoldTime) continue;
                ReachedOnce = true;
                int sd = i == 0 ? -1 : 1;
                Vector3 target = woundWorld + woundNormal * 0.045f + th.R * (sd * 0.03f);
                if (Wound.Torso)
                {
                    target = JointKinematics.ClampOutsideThorax(target, th.Pos, th.R, th.U, th.F,
                        _b.Body.ThoraxChestZ, _b.Body.ThoraxYBot, _b.Body.ThoraxYTop, _b.Body.ThoraxRx, BodyModel.ThoraxRyCap, _b.Body.ThoraxRz, sd);
                }
                float env = BMath.SmoothStep(0f, 0.14f, rt - P.TimeBeforeReach)
                          * (1f - BMath.SmoothStep(P.GrabHoldTime - 0.3f, P.GrabHoldTime, rt - P.TimeBeforeReach));
                float w = env * u;
                if (w <= 0.01f) continue;
                ArmTo(sd, target, w, P.ArmStiffness, 1f);
                // Pronation de l'avant-bras (Shot::wrist) : oriente la paume contre la plaie.
                float pron = DlPhys.ForearmPronationForNormal(_b.Body, sd, woundNormal);
                _ch.BlendDesired((int)AnatomyData.Elbow(sd), 2, pron, w * 0.85f);
                _ch.SetOpposeGravity((int)AnatomyData.Shoulder(sd), 1f);
                _ch.SetOpposeGravity((int)AnatomyData.Elbow(sd), 1f);
                // Prépare l'impulsion d'agrippement Delirium Shot::pull (appliquée dans Forces).
                _pullActive[i] = true;
                _pullTarget[i] = target;
                _pullStrength[i] = (Fallen ? 0.55f : 1.0f) * w;
            }
        }

        private void Fling(float t, float u)
        {
            if (FlingSide == 0 || Wound == null) return;
            int k = NmCharacter.Si(FlingSide);
            if (_ch.Busy[k] || LooseArms[k]) return;
            float env = DlPhys.Envelope(_since, 0.08f, 0.06f, 0.32f, 0.3f) * u;
            if (env <= 0.01f) return;
            int sd = FlingSide;
            int sh = (int)AnatomyData.Shoulder(sd), el = (int)AnatomyData.Elbow(sd);
            Vector3 d = BMath.SafeNormalize(Wound.Dir, _s.Fwd);
            float fwd = Mathf.Atan2(Vector3.Dot(d, _s.Fwd), Vector3.Dot(d, Vector3.up)) * Mathf.Rad2Deg;
            float abd = Mathf.Clamp(90f + fwd, 35f, 130f);
            _ch.SetStiffness(AnatomyData.Shoulder(sd), P.ArmStiffness, 1f);
            _ch.SetStiffness(AnatomyData.Elbow(sd), P.ArmStiffness, 1f);
            _ch.BlendDesired(sh, 0, fwd * BMath.Deg, env);
            _ch.BlendDesired(sh, 1, abd * BMath.Deg, env);
            _ch.BlendDesired(sh, 2, -0.45f * Motor.InternalRotSign(sd), env * 0.7f);
            _ch.BlendDesired(el, 0, 22f * BMath.Deg, env);
            // Pronation/supination dynamique sur le bras projeté.
            _ch.BlendDesired(el, 2, -0.5f * Motor.InternalRotSign(sd), env * 0.8f);
            _ch.SetMuscleStiffness(sh, 0.95f);
        }

        private void Collapse(float t, float u)
        {
            if (_since < P.TimeBeforeCollapseLeg) return;
            float w = Mathf.Clamp01((_since - P.TimeBeforeCollapseLeg) / 0.25f) * u;
            for (int k = 0; k < 2; k++)
            {
                if (!CollapseLegs[k]) continue;
                int sd = k == 0 ? -1 : 1;
                int hip = (int)AnatomyData.Hip(sd), knee = (int)AnatomyData.Knee(sd);
                // Delirium nmshot.rs:1111-1115 : chute franche de raideur sur la jambe blessée + dbm_knee_scale.
                float sHip = Mathf.Lerp(5.5f, 2.5f, w);
                float sKnee = Mathf.Lerp(4.2f, 1.8f, w);
                _ch.SetStiffness((JointId)hip, sHip, 1f);
                _ch.SetStiffness((JointId)knee, sKnee, 1f);
                _ch.BlendDesired(hip, 0, _ch.Desired(hip).x + 0.55f * w, Mathf.Max(0.35f, w));
                _ch.BlendDesired(knee, 0, _ch.Desired(knee).x + 1.05f * w, Mathf.Max(0.35f, w));
                _perf.Balance?.CollapseLeg(sd, Mathf.Max(0.35f, w));
            }
        }

        private void Look(float t, float dt, float u)
        {
            if (!P.UseHeadLook) return;
            // Une plaie sur la tête ne se regarde pas : viser sa propre tête depuis le centre de masse mettait la nuque
            // en extension (tête renversée en arrière) quelle que soit la direction de la balle. Le chef part dans
            // l'axe du projectile : c'est l'impulsion de tête (NmPerformance.OnBullet) qui s'en charge.
            if (Wound != null && Wound.Part != null && Wound.Part.Id == Seg.Head) return;
            if (t - _lookT > _lookDur)
            {
                _lookT = t;
                _lookWound = !_lookWound;
                _lookDur = _lookWound
                    ? BMath.Range(_rng, P.HeadLookAtWoundMin, P.HeadLookAtWoundMax)
                    : BMath.Range(_rng, P.HeadLookAtHeadMin, P.HeadLookAtHeadMax);
            }
            Vector3 target = _lookWound && Wound != null ? Wound.World : _b.Part(Seg.Head).Com + _s.Fwd;
            Vector3 rel = target - _s.Com;
            float yaw = Mathf.Atan2(Vector3.Dot(rel, _s.Right), Vector3.Dot(rel, _s.Fwd));
            float pitch = Mathf.Atan2(Vector3.Dot(rel, Vector3.up), DlBody.Horiz(rel).magnitude) - 0.25f;
            int nk = (int)JointId.Neck;
            _ch.SetStiffness(JointId.Neck, P.NeckStiffness, DlCfg.ShotSpineDamping);
            _ch.BlendDesired(nk, 0, -pitch, 0.6f * u);
            _ch.BlendDesired(nk, 2, Mathf.Clamp(yaw, -1f, 1f), 0.6f * u);
        }

        // ------------------------------------------------------------------ forces

        /// <summary>
        /// Forces du comportement shot :
        ///  - shotShockSpin : couple de vrille en N·m autour du rachis, décroissance exponentielle (nmshot.rs:559-575) ;
        ///  - Shot::pull : force d'agrippement réelle main ↔ plaie (action + réaction, nmshot.rs:1092-1109).
        /// </summary>
        public void Forces(float t, float dt, float weight)
        {
            float w = Mathf.Clamp01(weight);
            if (w <= 0.01f) return;

            if (_spinInit && _spinBlend > 0.01f)
            {
                Vector3 tq = _spinAxis * (_spin * _spinBlend * w);
                float k = P.ShockSpinScalePerComponent;
                _b.Torque(Seg.Thorax, tq);
                _b.Torque(Seg.Pelvis, tq * k);
                _b.Torque(Seg.Head, tq * k * 0.5f);
                _spinBlend -= (_spinBlend + 1f) * P.ShockSpinDecay * dt;
                if (_spinBlend <= 0.01f) _spinInit = false;
            }

            // Delirium Shot::pull (nmshot.rs:1092-1109) : maintient la main agrippée sur la plaie par force action/réaction.
            if (Wound != null && Wound.Part != null)
            {
                const float pullDist = 0.25f;
                Vector3 wv = Wound.Part.V;
                for (int i = 0; i < 2; i++)
                {
                    if (!_pullActive[i]) continue;
                    int sd = i == 0 ? -1 : 1;
                    Seg foreSeg = sd < 0 ? Seg.LForearm : Seg.RForearm;
                    Vector3 hand = _b.HandPos(sd);
                    Vector3 diff = _pullTarget[i] - hand;
                    float d = diff.magnitude;
                    if (d >= 3f * pullDist) continue;
                    float k = Mathf.Clamp01((3f * pullDist - d) / (2f * pullDist)) * _pullStrength[i] * w;
                    Vector3 force = Vector3.ClampMagnitude(diff * (110f * k) + (wv - _b.HandVel(sd)) * (14f * k), 55f);
                    _b.ForceAt(foreSeg, hand, force);
                    _b.ForceAt(Wound.Part.Id, _pullTarget[i], -force);
                }
            }
        }

        private static float Deadzone(float x, float d) => x > d ? x - d : x < -d ? x + d : 0f;

        private void ArmTo(int side, Vector3 target, float w, float stiffness, float damping)
        {
            if (w <= 0.01f) return;
            var body = _b.Body;
            var th = body[Seg.Thorax];
            var up = body[side < 0 ? Seg.LUpperArm : Seg.RUpperArm];
            var fo = body[side < 0 ? Seg.LForearm : Seg.RForearm];
            if (!JointKinematics.ArmIK(up.Pos, up.Length, fo.Length + 0.06f, target, side, th.R, th.U, th.F,
                    out float f, out float a, out float e, out _)) return;
            var sh = AnatomyData.Shoulder(side);
            var el = AnatomyData.Elbow(side);
            _ch.SetStiffness(sh, stiffness, damping);
            _ch.SetStiffness(el, stiffness, damping);
            _ch.BlendDesired((int)sh, 0, f * BMath.Deg, w);
            _ch.BlendDesired((int)sh, 1, a * BMath.Deg, w);
            _ch.BlendDesired((int)el, 0, e * BMath.Deg, w);
        }
    }
}
