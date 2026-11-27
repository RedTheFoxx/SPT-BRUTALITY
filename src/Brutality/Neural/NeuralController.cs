using System.Collections.Generic;
using System.Text;
using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Euphoria;
using EFT;
using UnityEngine;

namespace Brutality.Neural
{
    /// <summary>
    /// "Nervous system" of the dying character: builds the motor timeline from the death context
    /// (mechanism, zone, caliber, side, posture, destroyed limbs) then schedules agony randomly.
    /// </summary>
    internal sealed class NeuralController
    {
        private readonly List<MotorProgram> _programs = new List<MotorProgram>(16);
        private readonly MotorCommandBuffer _buffer = new MotorCommandBuffer();
        private readonly NeuralState _ns = new NeuralState();
        private MechanismProfile _profile;
        private DeathContext _ctx;
        private BodyModel _body;
        private bool _light;
        private float _maxDuration;

        public float AgonyStart { get; private set; }
        public float AgonyEnd { get; private set; }
        public float ActiveEnd { get; private set; }
        private float _musclesCap;
        private float _agonyI0;
        private float _vigor = 1f;
        private bool _agonyStarted;
        private bool _continuousSpawned;
        private float _variety = 1f;
        private float _lastBreathAt = -1f;
        private bool _lastBreathSpawned;
        private AgonyEvent _lastEvent = AgonyEvent.Count;
        private float _agonyDur;
        private DeathSignature _sig;

        /// <summary>Euphoria performance (NaturalMotion behaviors). Null on a lightweight-simulated body.</summary>
        public NmPerformance Nm { get; private set; }
        public float MusclesCap => _musclesCap;

        public MotorCommandBuffer Buffer => _buffer;
        public float InitialTone { get; private set; }
        public readonly StringBuilder Journal = new StringBuilder();

        public void Init(BodyModel body, DeathContext ctx, bool light, float maxDuration)
        {
            _body = body;
            _ctx = ctx;
            _light = light;
            _maxDuration = maxDuration;
            _profile = MechanismProfile.For(ctx.Mechanism);
            var rng = ctx.Rng;
            float reflex = Settings.ReflexIntensity.Value;
            _vigor = Settings.SpasmVigor.Value;
            _variety = Settings.GestureVariety.Value;
            // Signature of this death: gesture palette, tempo, asymmetry. No two deaths share the same one.
            _sig = VarietyDirector.Draw(rng, _variety);
            VarietyDirector.BeginDeath();

            _ns.Body = body;
            _ns.Ctx = ctx;
            _ns.Rng = rng;
            _ns.Buffer = _buffer;
            _ns.Reflex = reflex;
            _ns.Posturing = Settings.PosturingIntensity.Value;

            ApplyCapacities();

            // Residual postural tone (depends on posture at death). A lightweight-simulated body
            // has no programs: it collapses limp, with passive tissues only.
            float u0 = ctx.Pose == EPlayerPose.Prone ? 0.25f : (ctx.Pose == EPlayerPose.Duck ? 0.4f : 0.55f);
            InitialTone = u0;
            if (!_light)
                Add(new PosturalTone(u0, _profile.ToneDelay, _profile.ToneTau, rng, armSag: ctx.Pose != EPlayerPose.Prone));

            if (!_light && reflex > 0.01f)
            {
                // Posturing / fencing response only on cerebral lesions; no JointLock when Euphoria
                // drives the body so frozen gun-holding poses never fight procedural reactions.
                if (ctx.Mechanism == DeathMechanism.Cerebral && _ns.Posturing > 0.01f)
                {
                    float roll = (float)rng.NextDouble();
                    float pz = Mathf.Min(1f, _ns.Posturing) * 0.5f;
                    float pCor = _profile.DecorticateProb * pz;
                    if (roll < pCor)
                        Add(new Posturing(BMath.Range(rng, 0.05f, 0.12f), BMath.Range(rng, 0.6f, 1.5f), Mathf.Clamp01(0.55f * reflex), rng));
                    else if (roll < pCor + _profile.FencingProb * pz)
                        Add(new Fencing(BMath.Chance(rng, 0.5f) ? 1 : -1, BMath.Range(rng, 0.05f, 0.15f), BMath.Range(rng, 1.5f, 4f), Mathf.Clamp01(0.7f * reflex)));
                }
            }

            // Reserve a flaccid relaxation window before forced cutoff (at least ~2.4 s over 10 s,
            // proportional if the user reduces MaxMotionSeconds) so all limbs fall under their own weight.
            float relaxReserve = Mathf.Clamp(0.25f * _maxDuration, 1.1f, 2.5f);
            _musclesCap = Mathf.Max(0.5f, _maxDuration - relaxReserve);

            // Ground agony is triggered by the Euphoria performance (OnNmGround) once the body has settled:
            // until then, only the fall is played and muscles remain available. A lightweight-simulated body
            // (too far, or corpse cap reached) has neither performance nor agony: passive tissues only.
            _agonyI0 = _light ? 0f : BMath.Range(rng, _profile.AgonyI0) * Settings.AgonyIntensity.Value;
            _agonyDur = _agonyI0 > 0f ? BMath.Range(rng, _profile.AgonyDuration) : 0f;
            if (ctx.Mechanism == DeathMechanism.Blast || ctx.Mechanism == DeathMechanism.FallTrauma)
            {
                _agonyI0 = Mathf.Min(_agonyI0, 0.22f);
                _agonyDur = Mathf.Min(_agonyDur, 1.5f);
            }
            AgonyStart = float.PositiveInfinity;
            AgonyEnd = _musclesCap;
            // Lightweight simulation: muscles release immediately (no residual tone to maintain).
            ActiveEnd = _light ? 0.35f : _musclesCap;

            // No initial program (fencing, lock, clutch) may exceed the muscle cutoff.
            foreach (var p in _programs)
                p.Duration = Mathf.Max(0.1f, Mathf.Min(p.Duration, _musclesCap - p.Start));

            if (!_light) Nm = new NmPerformance(this, body, ctx, rng);

            if (Settings.DebugLogging.Value)
            {
                Journal.Append("initial programs: ");
                foreach (var p in _programs) Journal.Append(p.Name).Append(" @").Append(p.Start.ToString("0.00")).Append(", ");
                Journal.Append("agony deferred until ground, I0=").Append(_agonyI0.ToString("0.00"))
                       .Append(" end=").Append(ActiveEnd.ToString("0.0")).Append("s")
                       .Append("; signature: tempo ").Append(_sig.Tempo.ToString("0.00"))
                       .Append(", asymmetry ").Append(_sig.Asymmetry.ToString("0.00"))
                       .Append(", palette ").Append(PaletteSummary());
            }
        }

        /// <summary>Favored gestures for this death, for the diagnostic log.</summary>
        private string PaletteSummary()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _sig.Palette.Length; i++)
            {
                float m = _sig.Palette[i];
                if (m > 1.35f) sb.Append(m > 2.2f ? "++" : "+").Append((AgonyEvent)i).Append(' ');
                else if (m < 0.7f) sb.Append("-").Append((AgonyEvent)i).Append(' ');
            }
            return sb.Length == 0 ? "neutral" : sb.ToString().TrimEnd();
        }

        private void TryLock(JointId j, float weight)
        {
            var rng = _ctx.Rng;
            if (!BMath.Chance(rng, _profile.LockProb * weight * 0.5f)) return;
            if (_body.Joints[(int)j].Capacity[0] < 0.2f) return;
            Add(new JointLock(j, BMath.Range(rng, 0f, 0.08f), BMath.Range(rng, 0.4f, 2.5f), Mathf.Clamp01(0.85f * _ns.Reflex)));
        }

        /// <summary>Paralysis (spinal level), destroyed or injured limbs, limb hit by the fatal blow.</summary>
        private void ApplyCapacities()
        {
            var J = _body.Joints;
            void Set(JointId j, float v) { for (int d = 0; d < 3; d++) J[(int)j].Capacity[d] = Mathf.Min(J[(int)j].Capacity[d], v); }

            for (int side = -1; side <= 1; side += 2)
            {
                float arm = _ctx.ArmDestroyed(side) ? 0.03f : Mathf.Lerp(0.3f, 1f, _ctx.ArmHealth(side));
                float leg = _ctx.LegDestroyed(side) ? 0.03f : Mathf.Lerp(0.3f, 1f, _ctx.LegHealth(side));
                Set(AnatomyData.Shoulder(side), Mathf.Max(arm, 0.2f));
                Set(AnatomyData.Elbow(side), arm);
                Set(AnatomyData.Hip(side), Mathf.Max(leg, 0.2f));
                Set(AnatomyData.Knee(side), leg);
            }

            switch (_ctx.Mechanism)
            {
                case DeathMechanism.CervicalCord:
                    foreach (var jm in J) if (jm.Index != (int)JointId.Neck) for (int d = 0; d < 3; d++) jm.Capacity[d] = 0f;
                    Set(JointId.Neck, 0.4f);
                    break;
                case DeathMechanism.ThoracicCord:
                    Set(JointId.Lumbar, 0f);
                    Set(JointId.LHip, 0f); Set(JointId.RHip, 0f); Set(JointId.LKnee, 0f); Set(JointId.RKnee, 0f);
                    break;
                case DeathMechanism.LumbarCord:
                    Set(JointId.Lumbar, 0.6f);
                    Set(JointId.LHip, 0f); Set(JointId.RHip, 0f); Set(JointId.LKnee, 0f); Set(JointId.RKnee, 0f);
                    break;
                case DeathMechanism.Limb:
                    switch (_ctx.ColliderType)
                    {
                        case EBodyPartColliderType.LeftUpperArm: case EBodyPartColliderType.LeftForearm:
                            Set(JointId.LElbow, 0.05f); Set(JointId.LShoulder, 0.15f); break;
                        case EBodyPartColliderType.RightUpperArm: case EBodyPartColliderType.RightForearm:
                            Set(JointId.RElbow, 0.05f); Set(JointId.RShoulder, 0.15f); break;
                        case EBodyPartColliderType.LeftThigh: case EBodyPartColliderType.LeftCalf:
                            Set(JointId.LKnee, 0.05f); Set(JointId.LHip, 0.15f); break;
                        case EBodyPartColliderType.RightThigh: case EBodyPartColliderType.RightCalf:
                            Set(JointId.RKnee, 0.05f); Set(JointId.RHip, 0.15f); break;
                    }
                    break;
            }
        }

        private void Add(MotorProgram p)
        {
            if (p != null) _programs.Add(p);
        }

        private WoundRecord MainWound()
        {
            WoundRecord best = null;
            float bestScore = -1f;
            foreach (var w in _ctx.AllWounds)
            {
                if (w.Bone == null) continue;
                float recency = Mathf.Exp(-(_ctx.DeathTime - w.Time) / 6f);
                float score = w.Damage * (0.3f + recency);
                if (score > bestScore) { bestScore = score; best = w; }
            }
            return best;
        }

        private MotorProgram MakeClutch(float start, float duration, float u)
        {
            var rng = _ctx.Rng;
            var wound = MainWound();
            Seg fallback = Seg.Thorax;
            bool abdominal = false;
            int woundSide = _ctx.WoundSide;
            switch (_ctx.Mechanism)
            {
                case DeathMechanism.Abdominal:
                case DeathMechanism.LumbarCord:
                    fallback = Seg.Pelvis; abdominal = true; break;
                case DeathMechanism.NeckVascular:
                case DeathMechanism.CervicalCord:
                    fallback = Seg.Head; break;
            }
            if (wound != null)
            {
                var seg = _body.SegmentForBone(wound.Bone);
                if (seg != null)
                {
                    switch (seg.Id)
                    {
                        case Seg.LUpperArm: case Seg.LForearm: woundSide = -1; break;
                        case Seg.RUpperArm: case Seg.RForearm: woundSide = 1; break;
                        case Seg.LThigh: case Seg.LCalf: woundSide = -1; break;
                        case Seg.RThigh: case Seg.RCalf: woundSide = 1; break;
                    }
                }
            }

            // Available arms: not a destroyed arm, and not the injured arm trying to reach its own wound.
            var sides = new List<int>(2);
            for (int s = -1; s <= 1; s += 2)
            {
                if (_body.Joints[(int)AnatomyData.Elbow(s)].Capacity[0] < 0.25f) continue;
                if (wound != null)
                {
                    var seg = _body.SegmentForBone(wound.Bone);
                    if (seg != null && ((s < 0 && (seg.Id == Seg.LUpperArm || seg.Id == Seg.LForearm)) ||
                                        (s > 0 && (seg.Id == Seg.RUpperArm || seg.Id == Seg.RForearm)))) continue;
                }
                sides.Add(s);
            }
            if (sides.Count == 0) return null;
            if (sides.Count == 2 && !abdominal && fallback != Seg.Head && BMath.Chance(rng, 0.55f))
            {
                // Single hand: preferably contralateral to the wound, otherwise random.
                int keep = woundSide != 0 ? -woundSide : (BMath.Chance(rng, 0.5f) ? -1 : 1);
                sides.Clear();
                sides.Add(keep);
            }
            return new ClutchWound(wound, fallback, sides.ToArray(), start, duration, u, abdominal);
        }

        /// <summary>No gesture may spill into the calming phase preceding forced cutoff.</summary>
        private void ClampToAgony(MotorProgram p)
        {
            float end = Mathf.Min(AgonyEnd, _musclesCap);
            if (p.Start + p.Duration > end) p.Duration = Mathf.Max(0.15f, end - p.Start);
        }

        /// <summary>Arms capable of moving (one at random, or both).</summary>
        private int[] AvailableArms(bool both)
        {
            bool l = _body.Joints[(int)JointId.LElbow].Capacity[0] > 0.25f;
            bool r = _body.Joints[(int)JointId.RElbow].Capacity[0] > 0.25f;
            if (l && r) return both ? new[] { -1, 1 } : new[] { BMath.Chance(_ctx.Rng, 0.5f) ? -1 : 1 };
            if (l) return new[] { -1 };
            if (r) return new[] { 1 };
            return null;
        }

        private int[] AvailableLegs(bool both)
        {
            bool l = _body.Joints[(int)JointId.LKnee].Capacity[0] > 0.25f;
            bool r = _body.Joints[(int)JointId.RKnee].Capacity[0] > 0.25f;
            if (l && r) return both ? new[] { -1, 1 } : new[] { BMath.Chance(_ctx.Rng, 0.5f) ? -1 : 1 };
            if (l) return new[] { -1 };
            if (r) return new[] { 1 };
            return null;
        }

        // ------------------------------------------------------------------ loop

        /// <summary>Euphoria performance has settled the body on the ground: Brutality agony begins.</summary>
        public void OnNmGround(float t)
        {
            if (AgonyStart < 1e8f) return;
            float agonyCap = Mathf.Max(0.5f, _musclesCap - 0.1f);
            AgonyStart = t + BMath.Range(_ctx.Rng, 0.1f, 0.35f);
            float programsEnd = t + 0.8f;
            foreach (var p in _programs)
                if (!(p is PosturalTone)) programsEnd = Mathf.Max(programsEnd, p.Start + p.Duration);
            if (_agonyI0 > 0f && AgonyStart < agonyCap - 0.3f)
            {
                AgonyEnd = Mathf.Min(AgonyStart + Mathf.Max(1.5f, _agonyDur), agonyCap);
                if (_ctx.Mechanism == DeathMechanism.Cerebral)
                    AgonyEnd = Mathf.Min(AgonyEnd, AgonyStart + 1.6f);
                ActiveEnd = Mathf.Min(Mathf.Max(programsEnd, AgonyEnd), _musclesCap);
            }
            else
            {
                _agonyI0 = 0f;
                AgonyEnd = t + 0.5f;
                ActiveEnd = Mathf.Min(programsEnd, _musclesCap);
            }
        }

        public void AddProgram(MotorProgram p) => Add(p);

        public bool HasActiveTwitch(float t)
        {
            foreach (var p in _programs)
                if (p is IdiomuscularTwitch && !p.Done(t) && t >= p.Start) return true;
            return false;
        }

        public float AgonyIntensity(float t)
        {
            if (_agonyI0 <= 0f || t < AgonyStart || t > AgonyEnd) return 0f;
            float ramp = BMath.SmoothStep(AgonyStart, AgonyStart + 0.35f, t);
            // Plateau over ~45% of the window, then progressive decay.
            float decay = 1f - BMath.SmoothStep(AgonyStart + 0.45f * (AgonyEnd - AgonyStart), AgonyEnd, t);
            return _agonyI0 * ramp * Mathf.Pow(Mathf.Max(0f, decay), 0.8f);
        }

        public void Update(float t, float dt, bool grounded)
        {
            _ns.Time = t;
            _ns.Dt = dt;
            _ns.Agony = AgonyIntensity(t);
            _ns.RigidityPrev = _ns.Rigidity;
            _ns.Rigidity = 0f;
            _buffer.Reset();

            if (!_agonyStarted && _agonyI0 > 0f && t >= AgonyStart && (grounded || t >= AgonyStart + 1.2f))
            {
                _agonyStarted = true;
                // If the body took a while to settle, shift the agony window (without cutting into relaxation).
                float shift = t - AgonyStart;
                AgonyStart = t;
                AgonyEnd = Mathf.Min(AgonyEnd + shift * 0.5f, Mathf.Max(0.5f, _musclesCap - 0.1f));
                ActiveEnd = Mathf.Min(Mathf.Max(ActiveEnd, AgonyEnd), _musclesCap);
            }

            if (_agonyStarted && !_continuousSpawned)
            {
                _continuousSpawned = true;
                float len = Mathf.Max(0.2f, AgonyEnd - t);
                var rng = _ctx.Rng;
                if (BMath.Chance(rng, _profile.GaspProb) && _body.Joints[(int)JointId.Lumbar].Capacity[0] > 0.2f)
                {
                    float gs = t + BMath.Range(rng, 0f, Mathf.Min(1f, len * 0.4f));
                    Add(new AgonalGasp(gs, Mathf.Max(0.2f, AgonyEnd - gs), rng));
                }
                if (_profile.HeadLoll) Add(new HeadLoll(t, len, rng));
                // Postural rigidity (decortication) between gestures: extends the fall posture.
                if (_ns.Posturing > 0.01f && BMath.Chance(rng, Mathf.Clamp01(_profile.RigidityProb * Mathf.Max(0.5f, _ns.Posturing))))
                {
                    Add(new PosturalRigidity(t, len, BMath.Range(rng, 0.3f, 0.5f), rng));
                    if (Settings.DebugLogging.Value) Journal.Append("\n  t=").Append(t.ToString("0.00")).Append("s decorticate rigidity");
                }
                if (Settings.VisibleBreathing.Value && BMath.Chance(rng, _profile.BreathingProb) && _body.Joints[(int)JointId.Lumbar].Capacity[0] > 0.2f)
                    Add(new LaboredBreathing(t, len, rng));
                if (Settings.VisibleBreathing.Value && len > 2.5f && BMath.Chance(rng, _profile.LastBreathProb))
                    _lastBreathAt = AgonyEnd - BMath.Range(rng, 1.1f, 1.5f);
                if (_profile.Writhing > 0f) Add(new Writhing(t, len, _profile.Writhing * _vigor * _sig.Writhing, rng));
                var firstEv = PickEvent(MotorGroup.None);
                var first = Make(firstEv, t + BMath.Range(rng, 0f, 0.2f), _agonyI0);
                _lastEvent = firstEv;
                VarietyDirector.RecordGesture(firstEv);
                if (first != null)
                {
                    ClampToAgony(first);
                    Add(first);
                    if (Settings.DebugLogging.Value) Journal.Append("\n  t=").Append(t.ToString("0.00")).Append("s ").Append(first.Name).Append(" (first gesture)");
                }
            }

            if (_agonyStarted) ScheduleAgony(t, dt);
            if (_agonyStarted && !_lastBreathSpawned && _lastBreathAt > 0f && t >= _lastBreathAt && AgonyEnd - t > 0.6f)
            {
                _lastBreathSpawned = true;
                Add(new LastBreath(t, AgonyEnd - t, _ctx.Rng));
                if (Settings.DebugLogging.Value) Journal.Append("\n  t=").Append(t.ToString("0.00")).Append("s last breath");
            }

            Nm?.Update(_ns, t, dt);
            for (int i = 0; i < _programs.Count; i++) _programs[i].Run(_ns);
            for (int i = _programs.Count - 1; i >= 0; i--)
                if (_programs[i].Done(t)) _programs.RemoveAt(i);
        }

        private void ScheduleAgony(float t, float dt)
        {
            float I = _ns.Agony;
            // Do not launch a new gesture in the final moments of agony (avoids a spasm right before relaxation).
            if (I <= 0.08f || t >= AgonyEnd - 0.45f || _lastBreathSpawned) return;
            int active = 0;
            MotorGroup busy = MotorGroup.None;
            foreach (var p in _programs)
            {
                if (!p.IsAgonyEvent || p.Done(t) || t < p.Start) continue;
                active++;
                busy |= p.Groups;
            }
            if (active >= 1) return;
            // Euphoria behaviors (DlInjuredOnGround, DlWrithe) already occupy the body: cap to 1 subtle Brutality
            // gesture at a time so agony stays natural without chaotic overlapping spasms.
            float rate = _profile.EventRate * _vigor * _sig.Tempo * Mathf.Clamp01(I) * 0.55f;
            if (!BMath.Chance(_ctx.Rng, rate * dt)) return;

            var ev = PickEvent(busy);
            if (ev == AgonyEvent.Count) return;
            var prog = Make(ev, t, I);
            if (prog == null || (prog.Groups & busy) != 0) return;
            ClampToAgony(prog);
            Add(prog);
            _lastEvent = ev;
            VarietyDirector.RecordGesture(ev);
            if (Settings.DebugLogging.Value)
                Journal.Append("\n  t=").Append(t.ToString("0.00")).Append("s ").Append(prog.Name).Append(" I=").Append(I.ToString("0.00"));
        }

        private AgonyEvent PickEvent(MotorGroup busy)
        {
            float supine = _body.SupineScore;
            float sideUp = _body.SideUpScore;
            var w = (float[])_profile.Weights.Clone();
            // Palette specific to this death: some gestures are favored, others nearly absent.
            for (int i = 0; i < w.Length && i < _sig.Palette.Length; i++) w[i] *= _sig.Palette[i];
            float W(AgonyEvent e) => w[(int)e];
            void Mul(AgonyEvent e, float x) => w[(int)e] *= x;
            void Zero(AgonyEvent e) => w[(int)e] = 0f;

            if (supine > 0.5f) { Mul(AgonyEvent.Roll, 1.5f); Mul(AgonyEvent.Curl, 1.2f); }
            else if (supine < -0.5f)
            {
                Mul(AgonyEvent.Roll, 0.8f); Mul(AgonyEvent.Pedal, 0.5f);
                Mul(AgonyEvent.Clutch, 0.6f); Mul(AgonyEvent.Opisthotonus, 0.5f);
            }
            else Mul(AgonyEvent.Curl, 1.5f);

            // Ground orientation: crawl and push-up when prone, sit-up when supine.
            bool prone = supine < -0.45f, onBack = supine > 0.35f;
            if (!prone) { Zero(AgonyEvent.Crawl); Zero(AgonyEvent.PushUp); Zero(AgonyEvent.ElbowDrag); }
            else
            {
                Mul(AgonyEvent.Crawl, 1.5f); Mul(AgonyEvent.PushUp, 1.3f); Mul(AgonyEvent.KneeRaise, 0.4f); Mul(AgonyEvent.HeelDrum, 0.5f);
                Mul(AgonyEvent.ElbowDrag, 1.6f); Mul(AgonyEvent.HeadBang, 1.2f);
            }
            if (!onBack) Zero(AgonyEvent.SitUp);
            else { Mul(AgonyEvent.HipBridge, 1.5f); Mul(AgonyEvent.ArmsOverhead, 0.5f); Mul(AgonyEvent.ChestClutch, 1.2f); }
            if (Mathf.Abs(sideUp) > 0.6f) { Mul(AgonyEvent.Rocking, 0.5f); Mul(AgonyEvent.KneeRaise, 1.3f); Mul(AgonyEvent.LegCross, 1.3f); }

            // Elaborate gestures (beyond basic reflexes): "Gesture variety" setting.
            for (int i = (int)AgonyEvent.Lazarus; i < (int)AgonyEvent.Count; i++) w[i] *= _variety;
            float pz = _ns.Posturing;
            Mul(AgonyEvent.Decorticate, pz); Mul(AgonyEvent.Lazarus, pz);

            float legsCap = _body.Joints[(int)JointId.LHip].Capacity[0] + _body.Joints[(int)JointId.RHip].Capacity[0];
            if (legsCap < 0.3f)
            {
                Zero(AgonyEvent.Pedal); Zero(AgonyEvent.Kick); Zero(AgonyEvent.LegJerk);
                Zero(AgonyEvent.KneeRaise); Zero(AgonyEvent.HeelDrum); Zero(AgonyEvent.Scissor);
                Zero(AgonyEvent.HipBridge); Zero(AgonyEvent.LegCross); Zero(AgonyEvent.HeelSlide);
            }
            float armsCap = _body.Joints[(int)JointId.LElbow].Capacity[0] + _body.Joints[(int)JointId.RElbow].Capacity[0];
            if (armsCap < 0.3f)
            {
                Zero(AgonyEvent.Flail); Zero(AgonyEvent.Clutch); Zero(AgonyEvent.ArmThrust); Zero(AgonyEvent.Reach);
                Zero(AgonyEvent.GroundSlap); Zero(AgonyEvent.Shield); Zero(AgonyEvent.Lazarus); Zero(AgonyEvent.HandsToFace);
                Zero(AgonyEvent.PushUp); Zero(AgonyEvent.Crawl);
                Zero(AgonyEvent.ArmsOverhead); Zero(AgonyEvent.ChestClutch); Zero(AgonyEvent.ElbowDrag); Zero(AgonyEvent.ShoulderHunch);
            }
            if (_body.Joints[(int)JointId.Lumbar].Capacity[0] < 0.2f)
            {
                Zero(AgonyEvent.SitUp); Zero(AgonyEvent.Rocking);
                Zero(AgonyEvent.HipBridge); Zero(AgonyEvent.TwistArch);
            }
            if (_body.Joints[(int)JointId.Neck].Capacity[0] < 0.2f)
            {
                Zero(AgonyEvent.HeadLift); Zero(AgonyEvent.HeadBang); Zero(AgonyEvent.NeckArch); Zero(AgonyEvent.ShoulderHunch);
            }

            if ((busy & MotorGroup.Legs) != 0)
            {
                Zero(AgonyEvent.Pedal); Zero(AgonyEvent.Kick); Zero(AgonyEvent.LegJerk);
                Zero(AgonyEvent.KneeRaise); Zero(AgonyEvent.HeelDrum); Zero(AgonyEvent.Scissor);
                Zero(AgonyEvent.HipBridge); Zero(AgonyEvent.LegCross); Zero(AgonyEvent.HeelSlide);
            }
            if ((busy & MotorGroup.Arms) != 0)
            {
                Zero(AgonyEvent.Shield); Zero(AgonyEvent.Lazarus); Zero(AgonyEvent.HandsToFace); Zero(AgonyEvent.PushUp);
                Zero(AgonyEvent.ArmsOverhead); Zero(AgonyEvent.ChestClutch); Zero(AgonyEvent.ElbowDrag); Zero(AgonyEvent.ShoulderHunch);
                if ((busy & MotorGroup.Arms) == MotorGroup.Arms)
                {
                    Zero(AgonyEvent.Flail); Zero(AgonyEvent.Clutch); Zero(AgonyEvent.ArmThrust);
                    Zero(AgonyEvent.Reach); Zero(AgonyEvent.GroundSlap);
                }
            }
            if ((busy & MotorGroup.Neck) != 0)
            {
                Zero(AgonyEvent.HeadTurn); Zero(AgonyEvent.HeadLift); Zero(AgonyEvent.Reach);
                Zero(AgonyEvent.Shield); Zero(AgonyEvent.Lazarus); Zero(AgonyEvent.HandsToFace);
                Zero(AgonyEvent.HeadBang); Zero(AgonyEvent.NeckArch); Zero(AgonyEvent.TwistArch);
            }
            if (busy != MotorGroup.None)
            {
                Zero(AgonyEvent.Curl); Zero(AgonyEvent.Opisthotonus); Zero(AgonyEvent.Roll); Zero(AgonyEvent.Spasm);
                Zero(AgonyEvent.Decorticate); Zero(AgonyEvent.Seizure);
                Zero(AgonyEvent.Crawl); Zero(AgonyEvent.SitUp); Zero(AgonyEvent.Rocking);
                Zero(AgonyEvent.HipBridge); Zero(AgonyEvent.TwistArch); Zero(AgonyEvent.Shudder);
            }
            // Variety: the same gesture twice in a row is rare, and a gesture already seen in the previous death
            // is even less likely (cross-death memory).
            if (_lastEvent != AgonyEvent.Count && W(_lastEvent) > 0f) Mul(_lastEvent, 0.22f);
            for (int i = 0; i < w.Length; i++)
                if (w[i] > 0f) w[i] *= VarietyDirector.GestureMultiplier((AgonyEvent)i);

            float sum = 0f;
            foreach (var x in w) sum += x;
            if (sum <= 0f) return AgonyEvent.Count;
            float r = (float)_ctx.Rng.NextDouble() * sum;
            for (int i = 0; i < w.Length; i++)
            {
                r -= w[i];
                if (r <= 0f && w[i] > 0f) return (AgonyEvent)i;
            }
            return AgonyEvent.Count;
        }

        private MotorProgram Make(AgonyEvent ev, float t, float I)
        {
            var rng = _ctx.Rng;
            float k = Mathf.Clamp01(I);
            float g = 0.25f + 0.75f * k;
            float U(float lo, float hi) => Mathf.Clamp01(BMath.Range(rng, lo, hi) * g * _vigor);
            switch (ev)
            {
                case AgonyEvent.Curl:
                    return new Curl(t, BMath.Range(rng, 1.2f, 2.6f), U(0.6f, 0.9f));
                case AgonyEvent.Opisthotonus:
                    return new Opisthotonus(t, BMath.Range(rng, 0.5f, 1.3f), U(0.75f, 1f));
                case AgonyEvent.Clonic:
                    return new ClonicBurst(t, rng.Next(4, 10), BMath.Range(rng, 2.5f, 4.5f), U(0.8f, 1f), rng);
                case AgonyEvent.Pedal:
                {
                    int only = BMath.Chance(rng, 0.4f) ? (BMath.Chance(rng, 0.5f) ? -1 : 1) : 0;
                    return new Pedal(t, BMath.Range(rng, 1f, 2.2f), BMath.Range(rng, 1.1f, 2f), U(0.6f, 0.85f), only);
                }
                case AgonyEvent.Kick:
                {
                    int side = BMath.Chance(rng, 0.5f) ? -1 : 1;
                    if (_body.Joints[(int)AnatomyData.Knee(side)].Capacity[0] < 0.3f) side = -side;
                    return new Kick(side, t, BMath.Range(rng, 0.4f, 0.7f), U(0.85f, 1f));
                }
                case AgonyEvent.Flail:
                {
                    var sides = AvailableArms(BMath.Chance(rng, 0.35f));
                    return sides == null ? null : new ArmFlail(sides, t, BMath.Range(rng, 0.5f, 1.2f), U(0.65f, 0.9f), rng);
                }
                case AgonyEvent.Roll:
                {
                    int toward;
                    float sideUp = _body.SideUpScore;
                    if (Mathf.Abs(sideUp) > 0.5f) toward = sideUp > 0f ? -1 : 1; // already on side: roll back toward back or stomach
                    else toward = BMath.Chance(rng, 0.5f) ? -1 : 1;
                    return new Roll(toward, t, BMath.Range(rng, 0.8f, 1.5f), U(0.7f, 0.95f));
                }
                case AgonyEvent.Clutch:
                    return MakeClutch(t, BMath.Range(rng, 1.2f, 2.8f), U(0.55f, 0.8f));
                case AgonyEvent.HeadTurn:
                    return new HeadTurn(t, BMath.Range(rng, 0.5f, 1.3f), U(0.45f, 0.7f), rng);
                case AgonyEvent.ArmThrust:
                {
                    var sides = AvailableArms(BMath.Chance(rng, 0.45f));
                    return sides == null ? null : new ArmThrust(sides, rng.Next(1, 4), t, BMath.Range(rng, 0.5f, 0.9f), U(0.6f, 0.85f), rng);
                }
                case AgonyEvent.LegJerk:
                {
                    var sides = AvailableLegs(BMath.Chance(rng, 0.55f));
                    return sides == null ? null : new LegJerk(sides, rng.Next(2, 6), t, BMath.Range(rng, 1.2f, 2.4f), U(0.85f, 1f), rng);
                }
                case AgonyEvent.Spasm:
                    return new Spasm(BMath.Chance(rng, 0.55f), t, BMath.Range(rng, 0.25f, 0.45f), U(0.9f, 1f));

                case AgonyEvent.Decorticate:
                    return new PosturingEpisode(t, BMath.Range(rng, 1.3f, 3f), U(0.7f, 0.9f), rng);
                case AgonyEvent.Lazarus:
                    return new LazarusSign(t, BMath.Range(rng, 3f, 4.8f), U(0.55f, 0.75f), rng);
                case AgonyEvent.Seizure:
                    return new TonicClonicSeizure(t, U(0.85f, 1f), rng);
                case AgonyEvent.Reach:
                {
                    var sides = AvailableArms(false);
                    return sides == null ? null
                        : new AgonalReach(sides[0], _body.SupineScore > 0.3f, t, BMath.Range(rng, 1.6f, 2.8f), U(0.6f, 0.8f), rng);
                }
                case AgonyEvent.Crawl:
                    return new CrawlAttempt(rng.Next(2, 4), t, BMath.Range(rng, 1.1f, 1.5f), U(0.7f, 0.9f), rng);
                case AgonyEvent.PushUp:
                    return new PushUpAttempt(t, BMath.Range(rng, 1.5f, 2.4f), U(0.8f, 1f), rng);
                case AgonyEvent.SitUp:
                    return new SitUpAttempt(t, BMath.Range(rng, 1.3f, 2.2f), U(0.7f, 0.9f));
                case AgonyEvent.HeadLift:
                    return new HeadLift(_body.SupineScore, _body.SideUpScore, t, BMath.Range(rng, 1f, 2f), U(0.55f, 0.75f), rng);
                case AgonyEvent.KneeRaise:
                {
                    var sides = AvailableLegs(BMath.Chance(rng, 0.3f));
                    return sides == null ? null : new KneeRaise(sides, t, BMath.Range(rng, 1.6f, 2.6f), U(0.55f, 0.75f), rng);
                }
                case AgonyEvent.HeelDrum:
                    return new HeelDrum(t, BMath.Range(rng, 0.8f, 1.5f), BMath.Range(rng, 3f, 4.5f), U(0.6f, 0.8f));
                case AgonyEvent.GroundSlap:
                {
                    var sides = AvailableArms(BMath.Chance(rng, 0.25f));
                    return sides == null ? null : new GroundSlap(sides, rng.Next(2, 5), t, BMath.Range(rng, 0.35f, 0.5f), U(0.75f, 0.95f));
                }
                case AgonyEvent.Shield:
                {
                    var sides = AvailableArms(true);
                    return sides == null ? null : new ShieldArms(sides, BMath.Chance(rng, 0.5f), t, BMath.Range(rng, 1f, 2f), U(0.65f, 0.85f), rng);
                }
                case AgonyEvent.Scissor:
                    return new Scissor(rng.Next(3, 6), t, BMath.Range(rng, 0.35f, 0.5f), U(0.6f, 0.8f), rng);
                case AgonyEvent.Rocking:
                    return new Rocking(rng.Next(2, 4), t, BMath.Range(rng, 0.8f, 1.1f), U(0.65f, 0.85f), rng);
                case AgonyEvent.HandsToFace:
                {
                    var sides = AvailableArms(BMath.Chance(rng, 0.7f));
                    return sides == null ? null : new HandsToFace(sides, t, BMath.Range(rng, 1.2f, 2.2f), U(0.55f, 0.75f));
                }
                case AgonyEvent.HipBridge:
                    return new HipBridge(t, BMath.Range(rng, 0.9f, 1.6f), rng.Next(2, 4), U(0.7f, 0.95f));
                case AgonyEvent.ElbowDrag:
                    return new ElbowDrag(t, BMath.Range(rng, 1.2f, 2.2f), rng.Next(2, 4), U(0.55f, 0.8f), rng);
                case AgonyEvent.HeadBang:
                    return new HeadBang(t, BMath.Range(rng, 1f, 2f), BMath.Range(rng, 0.5f, 0.85f), U(0.5f, 0.78f), rng);
                case AgonyEvent.LegCross:
                {
                    int cross = _sig.Asymmetry != 0f ? (_sig.Asymmetry > 0f ? 1 : -1) : (BMath.Chance(rng, 0.5f) ? -1 : 1);
                    return new LegCross(cross, t, BMath.Range(rng, 1.4f, 2.6f), U(0.55f, 0.8f), rng);
                }
                case AgonyEvent.ArmsOverhead:
                    return new ArmsOverhead(t, BMath.Range(rng, 1.6f, 3.2f), U(0.5f, 0.75f), rng);
                case AgonyEvent.ChestClutch:
                {
                    var sides = AvailableArms(BMath.Chance(rng, 0.45f));
                    return sides == null ? null : new ChestClutch(sides, t, BMath.Range(rng, 1.4f, 2.6f), U(0.5f, 0.72f), rng);
                }
                case AgonyEvent.TwistArch:
                    return new TwistArch(t, BMath.Range(rng, 1.6f, 3f), U(0.5f, 0.78f), rng);
                case AgonyEvent.HeelSlide:
                {
                    var sides = AvailableLegs(false);
                    return sides == null ? null : new HeelSlide(sides[0], t, BMath.Range(rng, 1.2f, 2.2f), U(0.55f, 0.8f), rng);
                }
                case AgonyEvent.ShoulderHunch:
                    return new ShoulderHunch(t, BMath.Range(rng, 2f, 3.6f), BMath.Range(rng, 1.1f, 1.9f), U(0.45f, 0.65f), rng);
                case AgonyEvent.NeckArch:
                    return new NeckArch(t, BMath.Range(rng, 0.9f, 1.9f), U(0.55f, 0.8f), rng);
                case AgonyEvent.Shudder:
                    return new Shudder(t, BMath.Range(rng, 0.5f, 0.95f), BMath.Range(rng, 8f, 12f), U(0.35f, 0.6f), rng);
            }
            return null;
        }
    }
}
