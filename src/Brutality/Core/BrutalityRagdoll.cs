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
    /// Component attached to the corpse during its simulation. Drives the nervous system, muscles, and tissues
    /// before each PhysX step. After the active phase (at most 10 s), the corpse is no longer frozen: it remains
    /// simulated (PhysX sleep at rest, virtually free) and reacts to bullets and impacts; beyond the simulated
    /// corpse cap, the oldest ones are handed back to the game, which freezes them like vanilla.
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
        private bool _weaponDetached;
        private float _dropPeak;
        private float _wakeUntil;
        private readonly List<PendingHit> _hits = new List<PendingHit>(4);

        public float Age => Time.time - _t0;
        /// <summary>As long as Brutality manages the body, EFT's sleep check (which puts the body back to sleep every frame)
        /// is blocked: PhysX's natural sleep suffices and a bullet can wake the body.</summary>
        public bool KeepAwake => !_released;

        public static BrutalityRagdoll OwnerOf(Rigidbody rb)
        {
            if (rb == null) return null;
            return RbOwners.TryGetValue(rb, out var owner) && owner != null ? owner : null;
        }

        public static BrutalityRagdoll ForRagdoll(CorpseRagdoll ragdoll)
        {
            if (ragdoll == null) return null;
            for (int i = 0; i < Active.Count; i++)
            {
                var r = Active[i];
                if (r != null && !r._released && ReferenceEquals(r._ragdoll, ragdoll)) return r;
            }
            return null;
        }

        // ------------------------------------------------------------------ initialization

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
                Plugin.Log.LogWarning("Brutality: unsupported ragdoll (" + error + ") — keeping vanilla behavior.");
                return false;
            }

            int max = Settings.MaxSimultaneousCorpses.Value;
            float dist = CameraDistance(_body[Seg.Pelvis].T.position);
            // Only active-phase bodies count: sleeping corpses (permanent simulation) cost almost nothing.
            int simulating = 0;
            foreach (var r in Active) if (r != null && !r._dormant) simulating++;
            _light = simulating >= max || dist > Settings.FullSimulationDistance.Value;

            // Initialize the motor controller BEFORE replacing EFT's CharacterJoints (if Init fails,
            // the original joints have not been destroyed yet and the ragdoll stays 100% vanilla).
            try
            {
                // Current segment state (orientation, CoM): the Euphoria performance chooses its plan at init.
                _body.ReadState();
                _neural = new NeuralController();
                _neural.Init(_body, _ctx, _light, _maxDuration);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Brutality: motor controller initialization failed — keeping vanilla behavior. " + e);
                try { _body.Restore(); } catch { }
                _body = null;
                return false;
            }

            _ch = _neural.Nm?.Ch ?? new NmCharacter(_body);
            _impulses = _neural.Nm?.Impulses ?? new NmImpulseQueue(_body, _ch);
            _permanent = Settings.PermanentSimulation.Value && !observed;

            // Anatomical PhysX joints (exact limits + implicit drive muscles).
            int converted = JointRig.Build(_body);
            if (converted < AnatomyData.JointCount)
                Plugin.Log.LogWarning($"Brutality: {converted}/{AnatomyData.JointCount} joints converted to anatomical joints.");


            // Muscles are already active at the moment of death: no artificial slump on the first step.
            foreach (var jm in _body.Joints)
                for (int d = 0; d < 3; d++)
                {
                    jm.Activation[d] = _neural.InitialTone * jm.Capacity[d];
                    jm.Ref[d] = jm.Phi[d];   // reference trajectory starts from the death pose
                    jm.RefV[d] = 0f;
                }

            // Ragdoll stopping goes through the game's delegate: wrap it (compatible with Smoothie, HollywoodFX, Fika).
            _originalCheck = ragdoll._checkCorpseIsStill;
            ragdoll._checkCorpseIsStill = CheckCorpseIsStill;
            // Vanilla impulse (HIT_FORCE = 150 N·s, unrealistic) is replaced by real ballistic impulse.
            ragdoll._hasImpulse = false;

            foreach (var s in _body.Segs) RbOwners[s.Rb] = this;
            Active.Add(this);
            if (_ragdoll._weaponJoint != null || _ragdoll._weaponRigidbody != null)
                DetachWeapon();

            Plugin.Log.LogInfo(string.Format(
                "Brutality: {0} died — {1}, zone {2}/{3}, {4}, {5:0} J, {6:0.0} N·s, mass {7:0} kg (+{8:0.0} kg gear), pose {9}{10}",
                player.Profile?.Nickname ?? player.ProfileId, _ctx.Mechanism, _ctx.BodyPart, _ctx.ColliderType,
                (_ctx.Ammo?.Caliber ?? _ctx.DamageType.ToString()) + (_ctx.Ammo != null ? $" ({_ctx.KickClass}, kick ×{_ctx.KickScale:0.00})" : string.Empty),
                _ctx.ImpactEnergy, _ctx.ImpactMomentum * _ctx.KickScale, _ctx.BodyMassKg,
                _ctx.GearThoraxKg + _ctx.GearBackKg + _ctx.GearHeadKg, _ctx.Pose,
                (_neural.Nm != null ? ", Euphoria " + _neural.Nm.Plan.Kind : ", passive tissues")
                + (_light ? " [lightweight simulation]" : string.Empty)));
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

        /// <summary>
        /// Detaches EFT's corpse weapon joint (FixedJoint / SpringJoint) so the weapon drops freely to the ground
        /// instead of vibrating against the dynamic Brutality ragdoll. Keeps <c>_weaponRigidbody</c> intact so
        /// looting (<c>Corpse.RemoveLootItem</c> → <c>ClearWeapon</c>) and corpse cleanup work normally.
        /// </summary>
        public void DetachWeapon()
        {
            if (_released || _ragdoll == null || !Settings.DropCorpseWeapon.Value) return;
            if (_weaponDetached && _ragdoll._weaponJoint == null) return;
            if (_ragdoll._weaponJoint != null)
            {
                UnityEngine.Object.Destroy(_ragdoll._weaponJoint);
                _ragdoll._weaponJoint = null;
            }
            var wrb = _ragdoll._weaponRigidbody;
            if (wrb == null) return;
            _weaponDetached = true;
            wrb.drag = Mathf.Max(wrb.drag, 0.12f);
            wrb.angularDrag = Mathf.Max(wrb.angularDrag, 0.5f);
            wrb.maxDepenetrationVelocity = Mathf.Min(wrb.maxDepenetrationVelocity > 0f ? wrb.maxDepenetrationVelocity : 2f, 2f);
            if (!wrb.isKinematic) wrb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            if (_body != null)
            {
                var rFore = _body[Seg.RForearm];
                Vector3 releaseVel = rFore != null ? rFore.V * 0.45f : wrb.velocity * 0.5f;
                var thorax = _body[Seg.Thorax];
                Vector3 outward = thorax != null
                    ? BMath.SafeNormalize(NmCharacter.Flat(wrb.position - thorax.Com), thorax.R)
                    : Vector3.right;
                wrb.velocity = Vector3.ClampMagnitude(releaseVel + outward * 0.35f, 3.5f);
                var wCols = wrb.GetComponentsInChildren<Collider>(true);
                foreach (var wc in wCols)
                {
                    if (wc == null || wc.isTrigger) continue;
                    foreach (var seg in _body.Segs)
                    {
                        if (seg == null) continue;
                        foreach (var sc in seg.Colliders)
                            if (sc != null) Physics.IgnoreCollision(wc, sc, true);
                    }
                }
            }
            Plugin.LogDebug("  corpse weapon detached to drop freely to the ground");
        }

        // ------------------------------------------------------------------ stopping

        private bool CheckCorpseIsStill(bool sleeping, float timePass)
        {
            bool original = false;
            try
            {
                if (_originalCheck != null) original = _originalCheck(sleeping, timePass);
            }
            catch (Exception e)
            {
                Plugin.LogDebug("original stop delegate: " + e.Message);
            }
            if (this == null || _released) return original;
            if (_frozenByCap) return true;

            bool stop = _finished || Age >= _maxDuration;
            if (_permanent)
            {
                // Never frozen: the game receives its "corpse stopped" signal once at the end of the active phase.
                if (stop && !_stopInvoked) InvokeStopped();
                return false;
            }
            if (_corpse is ObservedCorpse oc)
            {
                try
                {
                    if (ObservedStillForced(oc)) return true; // Fika host froze the body
                    ObservedIsStill(oc) = stop;
                }
                catch { }
            }
            return stop;
        }

        /// <summary>
        /// EFT's "corpse stopped" callback (Corpse.OnRigidbodyStopped: loot rendering and culling; a corpse has no
        /// cached loot renderer) called once, then replaced with a no-op callback so a later freeze
        /// (corpse cap) does not replay it.
        /// </summary>
        private void InvokeStopped()
        {
            _stopInvoked = true;
            var cb = _ragdoll._onRigidbodyStopped;
            _ragdoll._onRigidbodyStopped = NoOp;
            try { cb?.Invoke(); }
            catch (Exception e) { Plugin.LogDebug("corpse stop callback: " + e.Message); }
        }

        // ------------------------------------------------------------------ bullets on corpse

        /// <summary>Bullet received by the body (game shot delegate): processed on the next physics step.</summary>
        public void OnBullet(Rigidbody rb, Vector3 point, Vector3 dir, float p)
        {
            if (_released || _body == null || _hits.Count >= 16) return;
            var seg = _body.SegmentForRigidbody(rb);
            if (seg == null || p <= 0f) return;
            _hits.Add(new PendingHit { Seg = seg, Point = point, Dir = dir, P = p });
            NmPhysics.WakeAll(_body);
        }

        /// <summary>
        /// Dying while still conscious (Euphoria performance): new bullet → shot reaction (relaxation, hand on wound,
        /// last stand). Otherwise (corpse): spread impulse, ground bounce, idiomuscular twitch.
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
                if (_ch.Lying && J.y < 0f) J.y *= -0.7f;   // tissues compressed against ground: bounce
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

        /// <summary>Called by the physics clock (Physics.Simulate prefix).</summary>
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
                    Plugin.Log.LogError("Brutality: simulation error, reverting to passive ragdoll. " + e);
                }
                Release("error");
            }
        }

        private void Step(float rawStep, bool fromPhysicsSimulate)
        {
            if (rawStep <= 1e-5f) return;
            if (_dormant) { CorpseStep(rawStep); return; }
            float dt = Mathf.Min(rawStep, 0.1f);
            if (_body == null || !_body.IsAlive)
            {
                Release("ragdoll stopped by game");
                return;
            }
            float t = Age;
            _steps++;

            _body.ReadState();
            _body.ComputeKinematics(dt);

            if (!_impulsesApplied)
            {
                _impulsesApplied = true;
                // Uses the actual (unclamped) PhysX step: otherwise a >100 ms lag over-cancels foreign
                // impulses and flings the body backward. Skipped in fallback mode (PhysX step unknown).
                if (fromPhysicsSimulate && Settings.CancelForeignImpulses.Value) CancelPendingForces(rawStep);
                var nm = _neural.Nm;
                if (nm != null)
                {
                    // The fatal shot becomes the first shotNewBullet of the Euphoria performance.
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

            // End of all muscle command: guarantees a true flaccid relaxation phase under gravity
            // before any terminal dissipation or kinematic freeze.
            float musclesEnd = _neural.ActiveEnd;
            bool muscles = t < musclesEnd || _neural.HasActiveTwitch(t);
            // Passive tone (pull toward NeutralDeg) fades out in ~0.45 s after muscle cutoff
            // so limbs drop completely under their own weight instead of freezing flexed.
            float passiveTone = muscles ? 1f : 1f - BMath.SmoothStep(musclesEnd, musclesEnd + 0.45f, t);
            // Light terminal dissipation only in the final moments (and at least 1.2 s after musclesEnd)
            // so limbs do not fall in slow-motion "molasses".
            float settleStart = Mathf.Min(Mathf.Max(musclesEnd + 1.2f, _maxDuration - 0.85f), _maxDuration - 0.45f);
            _fade = BMath.SmoothStep(settleStart, _maxDuration - 0.1f, t);

            DriveController.Step(_body, _neural.Buffer, dt, 1f + 1.5f * _fade, muscles, passiveTone);
            _impulses.Step(t, dt);
            _neural.Nm?.Forces(t, dt);
            ApplyThoraxArmClearance(dt);
            ApplySettling();

            // Early stop ONLY AFTER muscle cutoff, full muscle deactivation,
            // and at least 1.0 s of relaxed free fall under gravity.
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
        /// Corpse drop sound. EFT only plays it at ragdoll creation (<c>Corpse.TryToCreateRagdoll</c>
        /// calls <c>PlayCorpseDropSound</c>), i.e. at death rather than on impact: under Brutality, the body
        /// falls without any sound marking ground contact. We play the same game sound once, when
        /// the body actually hits the ground, and only if it truly fell (acquired vertical speed).
        /// </summary>
        private void TrackDropSound(float t)
        {
            if (_dropSoundPlayed) return;
            // Prone death: body is already on the ground, no fall to sonorize.
            if (_ctx.Pose == EPlayerPose.Prone) { _dropSoundPlayed = true; return; }
            var pelvis = _body[Seg.Pelvis];
            if (float.IsNaN(_pelvisY0)) _pelvisY0 = pelvis.Pos.y;
            // Downward vertical speed acquired since death: a body already on the ground (prone death) has none.
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
                Plugin.LogDebug($"  ground drop sound at t={t:0.00}s (impact {_dropPeak:0.0} m/s)");
            }
            catch (Exception e)
            {
                Plugin.LogDebug("drop sound: " + e.Message);
            }
        }

        /// <summary>End of active phase: body remains simulated (light damping, original PhysX sleep).</summary>
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
                Plugin.Log.LogInfo($"Brutality: active phase ended after {t:0.00} s ({_steps} steps) — permanently simulated corpse ({Corpses.Count}).");
                if (_neural.Journal.Length > 0) Plugin.Log.LogInfo("  motor log: " + _neural.Journal);
                if (_neural.Nm != null && _neural.Nm.Journal.Length > 0) Plugin.Log.LogInfo("  Euphoria log:" + _neural.Nm.Journal);
            }
        }

        /// <summary>Simulated corpse cap reached: body is handed back to the game, which freezes it like vanilla.</summary>
        private void FreezeByCap()
        {
            _frozenByCap = true;
            Release("simulated corpse cap");
        }

        /// <summary>
        /// Resting corpse: nothing is computed while PhysX keeps it asleep. When woken (bullet, impact, explosion),
        /// it is driven by passive tissues, spread impulses, and idiomuscular twitches.
        /// </summary>
        private void CorpseStep(float rawStep)
        {
            if (_body == null || !_body.IsAlive)
            {
                Release("ragdoll stopped by game");
                return;
            }
            float t = Age;
            // The body is only driven for ~2.5 s after a bullet or twitch. The rest of the time, we touch
            // nothing: rewriting drive targets and applying forces wakes rigidbodies every step,
            // prevents PhysX from putting them to sleep, and makes light segments (arms) vibrate.
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
            // Zero passive tone: it already faded out at the end of the active phase; restoring it would make limbs drop again on every rest transition.
            DriveController.Step(_body, _neural.Buffer, dt, 1.3f, _neural.HasActiveTwitch(t), 0f);
            _impulses.Step(t, dt);
            ApplyThoraxArmClearance(dt);
        }

        /// <summary>
        /// Resting corpse: drives reduced to joint viscosity (no spring, no target), written once.
        /// The body remains passive and dynamic; PhysX can put it to sleep, an impact wakes it.
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
        /// Soft-tissue barrier between the thorax and upper arms / forearms (corpse colliders do not collide
        /// with each other): critically-damped pushback (stable for any dt) on any arm or forearm point
        /// penetrating the ribcage, with equal and opposite reaction on the thorax.
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

            // Deepest point along the segment (without testing the proximal shoulder socket).
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
            // 4 mm deadzone: an arm resting against the torso no longer receives a force oscillating step to step.
            if (bestDepth <= 0.004f) return;
            bestDepth -= 0.004f;

            Vector3 rArm = bestPt - arm.Com;
            Vector3 rTho = bestPt - thorax.Com;
            Vector3 vArm = arm.V + Vector3.Cross(arm.W, rArm);
            Vector3 vTho = thorax.V + Vector3.Cross(thorax.W, rTho);
            float vIn = -Vector3.Dot(vArm - vTho, bestN);
            float accel = k * bestDepth + c * vIn;
            if (accel <= 0f) return;

            // Effective mass at the contact point (translation + rotation) for exact stability under symplectic Euler.
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
        /// Cancels forces and impulses accumulated since death (other mods): accumulated force is expressed
        /// for the upcoming step, so we apply the exact opposite force on that same step.
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
                Plugin.LogDebug($"  foreign impulses neutralized: {totalImpulse:0.0} N·s");
        }

        /// <summary>Terminal phase: progressive dissipation so the body comes to rest before forced stop.</summary>
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
            // Fallback if the Physics.Simulate patch could not be installed or is never called
            // (method inlined by JIT): drive via FixedUpdate (fixed rate independent of framerate).
            if (!_useFallback && (!PhysicsClock.PatchInstalled || (Age > 0.4f && _steps == 0)))
            {
                _useFallback = true;
                Plugin.Log.LogWarning("Brutality: no Physics.Simulate step intercepted, driving via FixedUpdate (fallback).");
            }
            // The game stops simulating as soon as rigidbodies are kinematic: release here.
            if (_body == null || !_body.IsAlive || (!_permanent && Age > _maxDuration + 3f))
                Release(_body != null && _body.IsAlive ? "timeout exceeded" : "ragdoll stopped by game");
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
                try { _body.Restore(); } catch (Exception e) { Plugin.LogDebug("restore: " + e.Message); }
            }
            if (Settings.DebugLogging.Value)
            {
                Plugin.Log.LogInfo($"Brutality: simulation ended ({reason}) after {Age:0.00} s, {_steps} physics steps.");
                if (_neural != null && _neural.Journal.Length > 0) Plugin.Log.LogInfo("  motor log: " + _neural.Journal);
                if (_neural?.Nm != null && _neural.Nm.Journal.Length > 0) Plugin.Log.LogInfo("  Euphoria log:" + _neural.Nm.Journal);
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
                if (r != null) r.Release("end of raid");
            }
            Active.Clear();
            Corpses.Clear();
            RbOwners.Clear();
        }
    }

    /// <summary>Physics clock: Brutality executes just before each Physics.Simulate (EFT Script mode).</summary>
    internal static class PhysicsClock
    {
        public static bool PatchInstalled;

        public static void PreStep(float step)
        {
            PhysxProbe.OnStep();
            var list = BrutalityRagdoll.Active;
            // Reverse iteration + bounds check: PhysicsStep may release another body (simulated corpse
            // cap), which shifts the list. Simply skip any index that went out of bounds.
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
