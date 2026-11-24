using System;
using System.Collections.Generic;
using Brutality.Anatomy;
using Brutality.Euphoria;
using Brutality.Neural;
using EFT;
using EFT.CameraControl;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>
    /// Composant attaché au cadavre pendant sa simulation. Pilote, avant chaque pas PhysX, le système nerveux,
    /// les muscles et les tissus. Après la phase active (10 s au plus), le cadavre n'est plus figé : il reste simulé
    /// (sommeil PhysX au repos, quasi gratuit) et réagit aux balles et aux chocs ; au-delà du plafond de cadavres
    /// simulés, les plus anciens sont rendus au jeu, qui les fige comme en vanilla.
    /// </summary>
    internal sealed class BrutalityRagdoll : MonoBehaviour
    {
        public static readonly List<BrutalityRagdoll> Active = new List<BrutalityRagdoll>(16);
        private static readonly List<BrutalityRagdoll> Corpses = new List<BrutalityRagdoll>(32);
        private static readonly Action NoOp = () => { };

        private struct PendingHit
        {
            public Segment Seg;
            public Vector3 Point, Dir;
            public float P;
        }
        private static readonly Dictionary<Rigidbody, BrutalityRagdoll> RbOwners = new Dictionary<Rigidbody, BrutalityRagdoll>(128);

        private static readonly AccessTools.FieldRef<ObservedCorpse, bool> ObservedIsStill =
            AccessTools.FieldRefAccess<ObservedCorpse, bool>("_isStill");
        private static readonly AccessTools.FieldRef<ObservedCorpse, bool> ObservedStillForced =
            AccessTools.FieldRefAccess<ObservedCorpse, bool>("_stillForced");

        private Corpse _corpse;
        private CorpseRagdoll _ragdoll;
        private DeathContext _ctx;
        private BodyModel _body;
        private NeuralController _neural;
        private Func<bool, float, bool> _originalCheck;

        private float _t0;
        private float _maxDuration;
        private float _pelvisY0 = float.NaN;
        private float _fade;
        private float _stillTimer;
        private bool _light;
        private bool _impulsesApplied;
        private bool _finished;
        private bool _released;
        private bool _errorLogged;
        private bool _useFallback;
        private int _steps;
        private NmCharacter _ch;
        private NmImpulseQueue _impulses;
        private bool _permanent, _dormant, _stopInvoked, _frozenByCap, _restApplied;
        private bool _dropSoundPlayed;
        private float _dropPeak;
        private float _wakeUntil;
        private readonly List<PendingHit> _hits = new List<PendingHit>(4);

        public float Age => Time.time - _t0;
        /// <summary>Tant que Brutality gère le corps, la mise en sommeil d'EFT (qui rendort le corps à chaque image) est bloquée :
        /// le sommeil naturel de PhysX suffit et une balle peut réveiller le corps.</summary>
        public bool KeepAwake => !_released;

        public static BrutalityRagdoll OwnerOf(Rigidbody rb)
        {
            if (rb == null) return null;
            return RbOwners.TryGetValue(rb, out var owner) && owner != null ? owner : null;
        }

        // ------------------------------------------------------------------ initialisation

        public bool Initialize(Player player, Corpse corpse, CorpseRagdoll ragdoll, bool observed)
        {
            _corpse = corpse;
            _ragdoll = ragdoll;
            _t0 = Time.time;
            _maxDuration = Mathf.Min(10f, Settings.MaxMotionSeconds.Value);

            _ctx = DeathContext.Build(player, observed);
            _body = BodyModel.TryBuild(ragdoll, player, _ctx, out string error);
            if (_body == null)
            {
                Plugin.Log.LogWarning("Brutality : ragdoll non pris en charge (" + error + ") — comportement vanilla conservé.");
                return false;
            }

            int max = Settings.MaxSimultaneousCorpses.Value;
            float dist = CameraDistance(_body[Seg.Pelvis].T.position);
            // Seuls les corps en phase active comptent : les cadavres endormis (simulation permanente) ne coûtent presque rien.
            int simulating = 0;
            foreach (var r in Active) if (r != null && !r._dormant) simulating++;
            _light = simulating >= max || dist > Settings.FullSimulationDistance.Value;

            // Initialiser le contrôleur moteur AVANT de remplacer les CharacterJoint d'EFT (si Init échoue,
            // les joints d'origine n'ont pas encore été détruits et le ragdoll reste 100 % vanilla).
            try
            {
                // État courant des segments (orientation, CdM) : la performance Euphoria choisit son plan dès l'initialisation.
                _body.ReadState();
                _neural = new NeuralController();
                _neural.Init(_body, _ctx, _light, _maxDuration);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Brutality : initialisation du contrôle moteur impossible — comportement vanilla conservé. " + e);
                try { _body.Restore(); } catch { }
                _body = null;
                return false;
            }

            _ch = _neural.Nm?.Ch ?? new NmCharacter(_body);
            _impulses = _neural.Nm?.Impulses ?? new NmImpulseQueue(_body, _ch);
            _permanent = Settings.PermanentSimulation.Value && !observed;

            // Joints anatomiques PhysX (butées exactes + muscles en drives implicites).
            int converted = JointRig.Build(_body);
            if (converted < AnatomyData.JointCount)
                Plugin.Log.LogWarning($"Brutality : {converted}/{AnatomyData.JointCount} articulations converties en joints anatomiques.");


            // Les muscles sont déjà actifs à l'instant de la mort : pas d'affaissement artificiel au premier pas.
            foreach (var jm in _body.Joints)
                for (int d = 0; d < 3; d++)
                {
                    jm.Activation[d] = _neural.InitialTone * jm.Capacity[d];
                    jm.Ref[d] = jm.Phi[d];   // la trajectoire de référence part de la pose de la mort
                    jm.RefV[d] = 0f;
                }

            // L'arrêt du ragdoll passe par le délégué du jeu : on l'enveloppe (compatible Smoothie, HollywoodFX, Fika).
            _originalCheck = ragdoll._checkCorpseIsStill;
            ragdoll._checkCorpseIsStill = CheckCorpseIsStill;
            // L'impulsion vanilla (HIT_FORCE = 150 N·s, irréaliste) est remplacée par l'impulsion balistique réelle.
            ragdoll._hasImpulse = false;

            foreach (var s in _body.Segs) RbOwners[s.Rb] = this;
            Active.Add(this);

            Plugin.Log.LogInfo(string.Format(
                "Brutality : {0} mort — {1}, zone {2}/{3}, {4}, {5:0} J, {6:0.0} N·s, masse {7:0} kg (+{8:0.0} kg équipement), posture {9}{10}",
                player.Profile?.Nickname ?? player.ProfileId, _ctx.Mechanism, _ctx.BodyPart, _ctx.ColliderType,
                (_ctx.Ammo?.Caliber ?? _ctx.DamageType.ToString()) + (_ctx.Ammo != null ? $" ({_ctx.KickClass}, poussée ×{_ctx.KickScale:0.00})" : string.Empty),
                _ctx.ImpactEnergy, _ctx.ImpactMomentum * _ctx.KickScale, _ctx.BodyMassKg,
                _ctx.GearThoraxKg + _ctx.GearBackKg + _ctx.GearHeadKg, _ctx.Pose,
                (_neural.Nm != null ? ", Euphoria " + _neural.Nm.Plan.Kind : ", tissus passifs")
                + (_light ? " [simulation allégée]" : string.Empty)));
            if (Settings.DebugLogging.Value && _neural.Journal.Length > 0)
                Plugin.Log.LogInfo("  " + _neural.Journal);
            return true;
        }

        private static float CameraDistance(Vector3 p)
        {
            try
            {
                var cm = CameraManager.Instance;
                if (cm != null) return Mathf.Sqrt(cm.SqrDistance(p));
            }
            catch { }
            var cam = Camera.main;
            return cam != null ? Vector3.Distance(cam.transform.position, p) : 0f;
        }

        // ------------------------------------------------------------------ arrêt

        private bool CheckCorpseIsStill(bool sleeping, float timePass)
        {
            bool original = false;
            try
            {
                if (_originalCheck != null) original = _originalCheck(sleeping, timePass);
            }
            catch (Exception e)
            {
                Plugin.LogDebug("délégué d'arrêt d'origine : " + e.Message);
            }
            if (this == null || _released) return original;
            if (_frozenByCap) return true;

            bool stop = _finished || Age >= _maxDuration;
            if (_permanent)
            {
                // Jamais figé : le jeu reçoit une seule fois son signal « corps arrêté » à la fin de la phase active.
                if (stop && !_stopInvoked) InvokeStopped();
                return false;
            }
            if (_corpse is ObservedCorpse oc)
            {
                try
                {
                    if (ObservedStillForced(oc)) return true; // l'hôte Fika a figé le corps
                    ObservedIsStill(oc) = stop;
                }
                catch { }
            }
            return stop;
        }

        /// <summary>
        /// Rappel « corps arrêté » d'EFT (Corpse.OnRigidbodyStopped : rendu et culling du loot ; un cadavre n'a pas de
        /// renderer de loot en cache) appelé une seule fois, puis remplacé par un rappel vide pour qu'un gel ultérieur
        /// (plafond de cadavres) ne le rejoue pas.
        /// </summary>
        private void InvokeStopped()
        {
            _stopInvoked = true;
            var cb = _ragdoll._onRigidbodyStopped;
            _ragdoll._onRigidbodyStopped = NoOp;
            try { cb?.Invoke(); }
            catch (Exception e) { Plugin.LogDebug("rappel d'arrêt du cadavre : " + e.Message); }
        }

        // ------------------------------------------------------------------ balles sur le corps

        /// <summary>Balle reçue par le corps (délégué de tir du jeu) : traitée au pas physique suivant.</summary>
        public void OnBullet(Rigidbody rb, Vector3 point, Vector3 dir, float p)
        {
            if (_released || _body == null || _hits.Count >= 16) return;
            var seg = _body.SegmentForRigidbody(rb);
            if (seg == null || p <= 0f) return;
            _hits.Add(new PendingHit { Seg = seg, Point = point, Dir = dir, P = p });
            NmPhysics.WakeAll(_body);
        }

        /// <summary>
        /// Mourant encore conscient (performance Euphoria) : nouvelle balle → réaction shot (relâchement, main sur la plaie,
        /// dernier carré debout). Sinon (cadavre) : impulsion étalée, rebond contre le sol, contraction idiomusculaire.
        /// </summary>
        private void ProcessHits(float t)
        {
            if (_hits.Count == 0) return;
            var nm = _neural?.Nm;
            float pm = Settings.PostMortemReaction.Value;
            foreach (var h in _hits)
            {
                var hit = NmHit.Make(_body, h.Seg, h.Point, h.Dir, h.P);
                if (!_dormant && nm != null && nm.U(t) > 0.05f)
                {
                    nm.OnBullet(t, hit);
                    continue;
                }
                _ch.Sense(t);
                Vector3 J = hit.Dir * (h.P * (1f + 2f * pm));
                if (_ch.Lying && J.y < 0f) J.y *= -0.7f;   // tissus comprimés contre le sol : rebond
                Vector3 rb = BMath.SafeNormalize(NmCharacter.Flat(hit.Dir), _ch.Fwd) * (_body.TotalMass * 0.12f * Mathf.Clamp(h.P / 3f, 0.5f, 2f) * pm)
                           + Vector3.up * (_body.TotalMass * 0.05f * pm);
                _impulses.Queue(t, h.Seg, h.Point, J, rb);
                if (pm > 0f && _neural != null)
                    _neural.AddProgram(new IdiomuscularTwitch(_body, h.Seg.Id, t, pm * (t < _neural.AgonyEnd ? 0.9f : 0.65f) * Mathf.Clamp(h.P / 4f, 0.5f, 1.4f), _ctx.Rng));
                if (nm != null && nm.Phase == NmPhase.Ground) nm.Wounds.Add(hit);
            }
            _hits.Clear();
        }

        // ------------------------------------------------------------------ simulation

        /// <summary>Appelé par l'horloge physique (préfixe de Physics.Simulate).</summary>
        public void PhysicsStep(float step)
        {
            if (_useFallback) return;
            SafeStep(step, fromPhysicsSimulate: true);
        }

        private void SafeStep(float rawStep, bool fromPhysicsSimulate)
        {
            if (_released) return;
            try
            {
                Step(rawStep, fromPhysicsSimulate);
            }
            catch (Exception e)
            {
                if (!_errorLogged)
                {
                    _errorLogged = true;
                    Plugin.Log.LogError("Brutality : erreur de simulation, retour au ragdoll passif. " + e);
                }
                Release("erreur");
            }
        }

        private void Step(float rawStep, bool fromPhysicsSimulate)
        {
            if (rawStep <= 1e-5f) return;
            if (_dormant) { CorpseStep(rawStep); return; }
            float dt = Mathf.Min(rawStep, 0.1f);
            if (_body == null || !_body.IsAlive)
            {
                Release("ragdoll arrêté par le jeu");
                return;
            }
            float t = Age;
            _steps++;

            _body.ReadState();
            _body.ComputeKinematics(dt);

            if (!_impulsesApplied)
            {
                _impulsesApplied = true;
                // Utilise le pas PhysX réel (non borné) : sinon un lag >100 ms sur-annule les impulsions
                // étrangères et propulse le corps en arrière. Ignoré en mode repli (pas PhysX inconnu).
                if (fromPhysicsSimulate && Settings.CancelForeignImpulses.Value) CancelPendingForces(rawStep);
                var nm = _neural.Nm;
                if (nm != null)
                {
                    // Le tir mortel devient le premier shotNewBullet de la performance Euphoria.
                    Impacts.Apply(_body, _ctx, (seg, point, dir, p, lethal) =>
                    {
                        if (lethal) nm.OnBullet(t, NmHit.Make(_body, seg, point, dir, p));
                        else _impulses.Queue(t, seg, point, dir * p, Vector3.zero);
                    });
                    if (nm.Wounds.Count == 0 && _ctx.Mechanism != DeathMechanism.Blast && _ctx.Mechanism != DeathMechanism.FallTrauma
                        && _ctx.Direction.sqrMagnitude > 1e-4f)
                    {
                        var seg = _body.SegmentForCollider(_ctx.ColliderType) ?? _body[Seg.Thorax];
                        Vector3 point = _ctx.HitPoint.sqrMagnitude > 1e-6f && (_ctx.HitPoint - seg.Com).sqrMagnitude < 1f ? _ctx.HitPoint : seg.Com;
                        nm.OnBullet(t, NmHit.Make(_body, seg, point, _ctx.Direction, Mathf.Max(1f, _ctx.ImpactMomentum) * Settings.BulletMomentumMultiplier.Value));
                    }
                }
                else Impacts.Apply(_body, _ctx);
            }
            ProcessHits(t);

            var pelvis = _body[Seg.Pelvis];
            bool grounded = t > 0.5f && pelvis.V.magnitude < 1.0f;

            _neural.Update(t, dt, grounded);
            TrackDropSound(t);

            // Fin de toute commande musculaire : garantit une vraie phase de relâchement flasque sous gravité
            // avant toute dissipation terminale ou figement cinématique.
            float musclesEnd = _neural.ActiveEnd;
            bool muscles = t < musclesEnd || _neural.HasActiveTwitch(t);
            // Le tonus passif (rappel vers NeutralDeg) s'éteint en ~0,45 s après la coupure musculaire
            // afin que les membres retombent complètement sous leur poids au lieu de se figer fléchis.
            float passiveTone = muscles ? 1f : 1f - BMath.SmoothStep(musclesEnd, musclesEnd + 0.45f, t);
            // Dissipation terminale légère uniquement dans les derniers instants (et au moins 1,2 s après musclesEnd)
            // pour ne pas ralentir en « mélasse » la retombée des membres.
            float settleStart = Mathf.Min(Mathf.Max(musclesEnd + 1.2f, _maxDuration - 0.85f), _maxDuration - 0.45f);
            _fade = BMath.SmoothStep(settleStart, _maxDuration - 0.1f, t);

            DriveController.Step(_body, _neural.Buffer, dt, 1f + 1.5f * _fade, muscles, passiveTone);
            _impulses.Step(t, dt);
            _neural.Nm?.Forces(t, dt);
            ApplyThoraxArmClearance(dt);
            ApplySettling();

            // Arrêt anticipé uniquement APRÈS coupure musculaire, désactivation complète des muscles
            // et au moins 1,0 s de chute libre relâchée sous gravité.
            bool relaxed = !muscles && t > musclesEnd + 1.0f && MaxMuscleActivation() < 0.02f;
            if (relaxed && _body.MaxSpeed < 0.045f && _body.MaxAngularSpeed < 0.18f) _stillTimer += dt;
            else _stillTimer = 0f;
            if (t >= _maxDuration || (relaxed && _stillTimer > 0.65f))
            {
                _finished = true;
                if (_permanent) EnterCorpse(t);
            }
        }

        /// <summary>
        /// Son de chute du cadavre. EFT ne le joue qu'à la création du ragdoll (<c>Corpse.TryToCreateRagdoll</c>
        /// appelle <c>PlayCorpseDropSound</c>), donc au moment de la mort et pas à l'impact : sous Brutality, le corps
        /// tombe sans qu'aucun son ne marque le contact avec le sol. On rejoue le même son du jeu une seule fois, quand
        /// le corps arrive réellement au sol, et seulement s'il a vraiment chu (vitesse verticale acquise).
        /// </summary>
        private void TrackDropSound(float t)
        {
            if (_dropSoundPlayed) return;
            // Mort allongée : le corps est déjà au sol, il n'y a pas de chute à sonoriser.
            if (_ctx.Pose == EPlayerPose.Prone) { _dropSoundPlayed = true; return; }
            var pelvis = _body[Seg.Pelvis];
            if (float.IsNaN(_pelvisY0)) _pelvisY0 = pelvis.Pos.y;
            // Vitesse verticale descendante acquise depuis la mort : un corps déjà au sol (mort allongée) n'en a pas.
            _dropPeak = Mathf.Max(_dropPeak, -pelvis.V.y);
            if (t < 0.12f) return;
            var nm = _neural.Nm;
            bool landed;
            if (nm != null) landed = nm.Phase == NmPhase.Ground && nm.GroundT >= 0f;
            else landed = _pelvisY0 - pelvis.Pos.y > 0.35f && pelvis.V.magnitude < 0.7f;
            if (!landed) return;
            _dropSoundPlayed = true;
            if (_dropPeak < 0.8f || _corpse == null) return;
            try
            {
                _corpse.PlayCorpseDropSound();
                Plugin.LogDebug($"  son de chute au sol à t={t:0.00}s (impact {_dropPeak:0.0} m/s)");
            }
            catch (Exception e)
            {
                Plugin.LogDebug("son de chute : " + e.Message);
            }
        }

        /// <summary>Fin de la phase active : le corps reste simulé (amortissement léger, sommeil PhysX d'origine).</summary>
        private void EnterCorpse(float t)
        {
            if (_dormant) return;
            _dormant = true;
            foreach (var s in _body.Segs)
            {
                var rb = s.Rb;
                if (rb == null) continue;
                rb.drag = Mathf.Max(Mathf.Min(s.OrigDrag, 0.05f), 0.06f);
                rb.angularDrag = Mathf.Max(Mathf.Min(s.OrigAngularDrag, 0.05f), 0.25f);
                rb.sleepThreshold = s.OrigSleepThreshold > 0f ? s.OrigSleepThreshold : 0.005f;
            }
            if (!_stopInvoked) InvokeStopped();
            _wakeUntil = t + 1.5f;
            Corpses.Add(this);
            int max = Settings.MaxPermanentCorpses.Value;
            while (Corpses.Count > max)
            {
                var old = Corpses[0];
                Corpses.RemoveAt(0);
                if (old != null && old != this) old.FreezeByCap();
            }
            if (Settings.DebugLogging.Value)
            {
                Plugin.Log.LogInfo($"Brutality : fin de la phase active après {t:0.00} s ({_steps} pas) — cadavre simulé en permanence ({Corpses.Count}).");
                if (_neural.Journal.Length > 0) Plugin.Log.LogInfo("  journal moteur : " + _neural.Journal);
                if (_neural.Nm != null && _neural.Nm.Journal.Length > 0) Plugin.Log.LogInfo("  journal Euphoria :" + _neural.Nm.Journal);
            }
        }

        /// <summary>Plafond de cadavres simulés atteint : le corps est rendu au jeu, qui le fige comme en vanilla.</summary>
        private void FreezeByCap()
        {
            _frozenByCap = true;
            Release("plafond de cadavres simulés");
        }

        /// <summary>
        /// Cadavre au repos : rien n'est calculé tant que PhysX le garde endormi. Réveillé (balle, choc, explosion), il est
        /// piloté par les tissus passifs, les impulsions étalées et les contractions idiomusculaires.
        /// </summary>
        private void CorpseStep(float rawStep)
        {
            if (_body == null || !_body.IsAlive)
            {
                Release("ragdoll arrêté par le jeu");
                return;
            }
            float t = Age;
            // Le corps n'est piloté que pendant ~2,5 s après une balle ou une contraction. Le reste du temps, on ne touche
            // plus à rien : réécrire les cibles des drives et appliquer des forces réveille les rigidbodies à chaque pas,
            // empêche PhysX de les endormir et fait vibrer les segments légers (bras).
            if (_hits.Count > 0 || _impulses.Pending || _neural.HasActiveTwitch(t)) _wakeUntil = Mathf.Max(_wakeUntil, t + 2.5f);
            if (t > _wakeUntil)
            {
                if (!_restApplied) ApplyRestDrives();
                return;
            }
            _restApplied = false;
            float dt = Mathf.Min(rawStep, 0.1f);
            _steps++;
            _body.ReadState();
            _body.ComputeKinematics(dt);
            ProcessHits(t);
            _neural.Update(t, dt, true);
            // Tonus passif nul : il s'est déjà éteint en fin de phase active ; le rétablir ferait retomber les membres à chaque passage au repos.
            DriveController.Step(_body, _neural.Buffer, dt, 1.3f, _neural.HasActiveTwitch(t), 0f);
            _impulses.Step(t, dt);
            ApplyThoraxArmClearance(dt);
        }

        /// <summary>
        /// Cadavre au repos : drives réduits à une viscosité articulaire (aucun ressort, aucune cible), écrits une seule fois.
        /// Le corps reste passif et dynamique ; PhysX peut l'endormir, un choc le réveille.
        /// </summary>
        private void ApplyRestDrives()
        {
            _restApplied = true;
            foreach (var jm in _body.Joints)
            {
                var cj = jm.Driven?.Joint;
                if (cj == null) continue;
                cj.angularXDrive = new JointDrive { positionSpring = 0f, positionDamper = jm.Ieff.x * 1.5f, maximumForce = 40f, useAcceleration = false };
                cj.angularYZDrive = new JointDrive { positionSpring = 0f, positionDamper = Mathf.Max(jm.Ieff.y, jm.Ieff.z) * 1.5f, maximumForce = 40f, useAcceleration = false };
            }
        }

        private float MaxMuscleActivation()
        {
            float max = 0f;
            foreach (var jm in _body.Joints)
                for (int d = 0; d < 3; d++)
                    if (jm.Activation[d] > max) max = jm.Activation[d];
            return max;
        }

        /// <summary>
        /// Barrière tissus mous entre le thorax et les bras / avant-bras (les colliders du cadavre ne se touchent
        /// pas entre eux) : repousse de manière critique-amortie (stable quel que soit dt) tout point du bras ou de
        /// l'avant-bras qui pénètre dans la cage thoracique, avec réaction égale et opposée sur le thorax.
        /// </summary>
        private void ApplyThoraxArmClearance(float dt)
        {
            var thorax = _body[Seg.Thorax];
            if (thorax?.Rb == null) return;
            Vector3 tPos = thorax.Pos;
            Vector3 tR = thorax.R, tU = thorax.U, tF = thorax.F;
            float chestZ = _body.ThoraxChestZ;
            float yBot = _body.ThoraxYBot, yTop = _body.ThoraxYTop;
            float maxUpperRx = Mathf.Max(0.10f, _body.ThoraxHalfShoulderW - 0.005f);
            float upperRx = Mathf.Min(_body.ThoraxRx + 0.036f, maxUpperRx);
            float upperRz = _body.ThoraxRz + 0.036f;
            float foreRx = _body.ThoraxRx + 0.030f;
            float foreRz = _body.ThoraxRz + 0.030f;

            float c = Mathf.Min(40f, dt > 1e-4f ? 0.6f / dt : 40f);
            float k = Mathf.Min(600f, dt > 1e-4f ? 0.12f / (dt * dt) : 600f);

            PushArmSegment(thorax, _body[Seg.LUpperArm], -1, tPos, tR, tU, tF, chestZ, yBot, yTop, upperRx, upperRz, k, c, isForearm: false);
            PushArmSegment(thorax, _body[Seg.RUpperArm],  1, tPos, tR, tU, tF, chestZ, yBot, yTop, upperRx, upperRz, k, c, isForearm: false);
            PushArmSegment(thorax, _body[Seg.LForearm],  -1, tPos, tR, tU, tF, chestZ, yBot, yTop, foreRx, foreRz, k, c, isForearm: true);
            PushArmSegment(thorax, _body[Seg.RForearm],   1, tPos, tR, tU, tF, chestZ, yBot, yTop, foreRx, foreRz, k, c, isForearm: true);
        }

        private static void PushArmSegment(
            Segment thorax, Segment arm, int side,
            Vector3 tPos, Vector3 tR, Vector3 tU, Vector3 tF,
            float chestZ, float yBot, float yTop, float rx, float rz,
            float k, float c, bool isForearm)
        {
            if (arm?.Rb == null) return;
            Vector3 p0 = arm.Pos;
            Vector3 distalDir = -arm.U;
            float len = arm.Length;
            float bestDepth = 0f;
            Vector3 bestPt = Vector3.zero;
            Vector3 bestN = Vector3.zero;

            // Point le plus enfoncé le long du segment (sans tester l'emboîture proximale de l'épaule).
            int count = isForearm ? 3 : 2;
            for (int i = 0; i < count; i++)
            {
                float frac = isForearm ? (i == 0 ? 0.35f : (i == 1 ? 0.68f : 1.0f)) : (i == 0 ? 0.55f : 1.0f);
                Vector3 pt = p0 + distalDir * (len * frac);
                if (JointKinematics.ThoraxPenetration(pt, tPos, tR, tU, tF, chestZ, yBot, yTop, rx, BodyModel.ThoraxRyCap, rz, side, out float depth, out Vector3 n)
                    && depth > bestDepth)
                {
                    bestDepth = depth;
                    bestPt = pt;
                    bestN = n;
                }
            }
            // Zone morte de 4 mm : un bras posé contre le buste ne reçoit plus une force qui oscille d'un pas à l'autre.
            if (bestDepth <= 0.004f) return;
            bestDepth -= 0.004f;

            Vector3 rArm = bestPt - arm.Com;
            Vector3 rTho = bestPt - thorax.Com;
            Vector3 vArm = arm.V + Vector3.Cross(arm.W, rArm);
            Vector3 vTho = thorax.V + Vector3.Cross(thorax.W, rTho);
            float vIn = -Vector3.Dot(vArm - vTho, bestN);
            float accel = k * bestDepth + c * vIn;
            if (accel <= 0f) return;

            // Masse effective au point de contact (translation + rotation) pour une stabilité exacte en Euler symplectique.
            float invM = 1f / Mathf.Max(0.2f, arm.Mass) + 1f / Mathf.Max(2f, thorax.Mass);
            Vector3 cArm = Vector3.Cross(rArm, bestN);
            float cArm2 = cArm.sqrMagnitude;
            if (cArm2 > 1e-6f)
            {
                float iArm = Mathf.Max(0.005f, BMath.MomentAboutAxis(arm.Q * arm.InertiaRotLocal, arm.InertiaDiag, cArm / Mathf.Sqrt(cArm2)));
                invM += cArm2 / iArm;
            }
            Vector3 cTho = Vector3.Cross(rTho, bestN);
            float cTho2 = cTho.sqrMagnitude;
            if (cTho2 > 1e-6f)
            {
                float iTho = Mathf.Max(0.05f, BMath.MomentAboutAxis(thorax.Q * thorax.InertiaRotLocal, thorax.InertiaDiag, cTho / Mathf.Sqrt(cTho2)));
                invM += cTho2 / iTho;
            }
            float fMag = Mathf.Min(accel / invM, 120f);
            Vector3 force = bestN * fMag;
            arm.Rb.AddForceAtPosition(force, bestPt, ForceMode.Force);
            thorax.Rb.AddForceAtPosition(-force, bestPt, ForceMode.Force);
        }

        /// <summary>
        /// Annule les forces et impulsions accumulées depuis la mort (autres mods) : la force accumulée est exprimée
        /// pour le pas à venir, on lui oppose exactement la force inverse sur ce même pas.
        /// </summary>
        private void CancelPendingForces(float step)
        {
            float totalImpulse = 0f;
            foreach (var s in _body.Segs)
            {
                var rb = s.Rb;
                if (rb == null) continue;
                Vector3 f = rb.GetAccumulatedForce(step);
                Vector3 tq = rb.GetAccumulatedTorque(step);
                if (f.sqrMagnitude > 1e-8f && !float.IsNaN(f.x))
                {
                    rb.AddForce(-f, ForceMode.Force);
                    totalImpulse += f.magnitude * step;
                }
                if (tq.sqrMagnitude > 1e-8f && !float.IsNaN(tq.x)) rb.AddTorque(-tq, ForceMode.Force);
            }
            if (totalImpulse > 0.5f)
                Plugin.LogDebug($"  impulsions étrangères neutralisées : {totalImpulse:0.0} N·s");
        }

        /// <summary>Phase terminale : dissipation progressive pour que le corps soit au repos avant l'arrêt forcé.</summary>
        private void ApplySettling()
        {
            float f = _fade;
            foreach (var s in _body.Segs)
            {
                var rb = s.Rb;
                if (rb == null) continue;
                rb.drag = Mathf.Lerp(Mathf.Min(s.OrigDrag, 0.05f), 0.15f, f);
                rb.angularDrag = Mathf.Lerp(Mathf.Min(s.OrigAngularDrag, 0.05f), 0.45f, f);
                if (f > 0.9f && rb.sleepThreshold <= 0f) rb.sleepThreshold = s.OrigSleepThreshold > 0f ? s.OrigSleepThreshold : 0.005f;
            }
        }

        private void FixedUpdate()
        {
            if (_released || !_useFallback) return;
            SafeStep(Time.fixedDeltaTime, fromPhysicsSimulate: false);
        }

        private void Update()
        {
            if (_released) return;
            // Repli si le patch Physics.Simulate n'a pas pu être installé ou n'est jamais appelé
            // (méthode inlinée par le JIT) : pilotage par FixedUpdate (cadence fixe indépendante du framerate).
            if (!_useFallback && (!PhysicsClock.PatchInstalled || (Age > 0.4f && _steps == 0)))
            {
                _useFallback = true;
                Plugin.Log.LogWarning("Brutality : aucun pas Physics.Simulate intercepté, pilotage par FixedUpdate (repli).");
            }
            // Le jeu cesse de simuler dès que les rigidbodies sont kinématiques : on libère ici.
            if (_body == null || !_body.IsAlive || (!_permanent && Age > _maxDuration + 3f))
                Release(_body != null && _body.IsAlive ? "délai dépassé" : "ragdoll arrêté par le jeu");
        }

        private void Release(string reason)
        {
            if (_released) return;
            _released = true;
            Active.Remove(this);
            Corpses.Remove(this);
            if (_body != null)
            {
                foreach (var s in _body.Segs)
                    if (s?.Rb != null && RbOwners.TryGetValue(s.Rb, out var o) && o == this) RbOwners.Remove(s.Rb);
                try { JointRig.Relax(_body); } catch { }
                try { _body.Restore(); } catch (Exception e) { Plugin.LogDebug("restauration : " + e.Message); }
            }
            if (Settings.DebugLogging.Value)
            {
                Plugin.Log.LogInfo($"Brutality : fin de simulation ({reason}) après {Age:0.00} s, {_steps} pas physiques.");
                if (_neural != null && _neural.Journal.Length > 0) Plugin.Log.LogInfo("  journal moteur : " + _neural.Journal);
                if (_neural?.Nm != null && _neural.Nm.Journal.Length > 0) Plugin.Log.LogInfo("  journal Euphoria :" + _neural.Nm.Journal);
            }
            if (this != null) Destroy(this);
        }

        private void OnDestroy()
        {
            if (_released) return;
            _released = true;
            Active.Remove(this);
            Corpses.Remove(this);
            if (_body != null)
            {
                foreach (var s in _body.Segs)
                    if (s?.Rb != null && RbOwners.TryGetValue(s.Rb, out var o) && o == this) RbOwners.Remove(s.Rb);
                try { JointRig.Relax(_body); } catch { }
                try { _body.Restore(); } catch { }
            }
        }

        public static void ClearAll()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var r = Active[i];
                if (r != null) r.Release("fin de raid");
            }
            Active.Clear();
            Corpses.Clear();
            RbOwners.Clear();
        }
    }

    /// <summary>Horloge physique : Brutality s'exécute juste avant chaque Physics.Simulate (mode Script d'EFT).</summary>
    internal static class PhysicsClock
    {
        public static bool PatchInstalled;

        public static void PreStep(float step)
        {
            PhysxProbe.OnStep();
            var list = BrutalityRagdoll.Active;
            // Parcours arrière + sortie anticipée : PhysicsStep peut libérer un autre corps (plafond de cadavres
            // simulés), ce qui décale la liste. On ignore simplement l'index devenu hors bornes.
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (i >= list.Count) continue;
                var r = list[i];
                if (r == null)
                {
                    list.RemoveAt(i);
                    continue;
                }
                r.PhysicsStep(step);
            }
        }
    }
}
