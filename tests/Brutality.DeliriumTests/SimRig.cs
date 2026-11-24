using System;
using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Euphoria;
using Brutality.Neural;
using EFT;
using UnityEngine;

namespace DeliriumTests
{
    /// <summary>
    /// Articulated 3D mannequin (11 segments, 10 joints) with forward kinematics, force/torque integration,
    /// gravity, ground contact, and tracking of motor commands from <see cref="MotorCommandBuffer"/>.
    /// Enables end-to-end closed-loop testing of the real Delirium behaviours at 60 Hz.
    /// </summary>
    internal sealed class SimRagdoll
    {
        public readonly BodyModel Body;
        public readonly NeuralController Neural;
        public readonly NeuralState State;
        public readonly MotorCommandBuffer Buffer;
        public readonly NmPerformance Perf;

        public Vector3 RootPos = new Vector3(0f, 0.95f, 0f);
        public Quaternion RootRot = Quaternion.identity;
        public Vector3 RootVel;
        public Vector3 RootAngVel;
        public readonly Vector3[] JointPhi = new Vector3[AnatomyData.JointCount];
        public readonly Vector3[] JointVel = new Vector3[AnatomyData.JointCount];

        // Telemetry recorded during the simulation.
        public float PeakPronationRad;
        public float PeakShockSpinTorque;
        public float PeakRollAngVel;
        public float PeakLeftKneeFlex, PeakRightKneeFlex;
        public float MinPelvisY = 10f;
        public bool AnyGrabActivated, AnyGrabGripped, AnyGrabSnapped;
        public bool AnyFlinchActivated, AnyRollActivated, AnyWindmillActivated;
        public bool AnyWallBrace, AnyWallSlump, AnyFallOverWall;
        public bool AnyHighFall, AnyHighFallLand, AnyOnFire, AnyMeleeGuard;
        public int PeakSteps;
        public bool AnyCatchFallHitFloor;
        public bool AnyWoundReach;
        public bool AnyNaN;

        public SimRagdoll(DeathContext ctx, bool riflePose = false)
        {
            Body = BuildStandingBody(ctx.Velocity, riflePose);
            Neural = new NeuralController();
            Buffer = new MotorCommandBuffer();
            State = new NeuralState { Body = Body, Ctx = ctx, Rng = ctx.Rng, Buffer = Buffer, Agony = 0f, Reflex = 1f, Posturing = 1f };
            for (int j = 0; j < AnatomyData.JointCount; j++) JointPhi[j] = Body.Joints[j].Phi0;
            RootVel = ctx.Velocity;
            SyncKinematics(0f);
            Perf = new NmPerformance(Neural, Body, ctx, ctx.Rng);
        }

        private static BodyModel BuildStandingBody(Vector3 initVel, bool riflePose)
        {
            var body = new BodyModel();
            float M = 80f;
            float[] lengths = { 0.18f, 0.36f, 0.22f, 0.30f, 0.28f, 0.30f, 0.28f, 0.44f, 0.44f, 0.44f, 0.44f };
            for (int i = 0; i < AnatomyData.SegCount; i++)
            {
                float m = M * AnatomyData.Inertia[i].MassFraction;
                float r = MathF.Max(0.06f, lengths[i] * 0.3f);
                var rb = new Rigidbody { mass = m, inertiaTensor = Vector3.one * (m * r * r), velocity = initVel };
                body.Segs[i] = new Segment
                {
                    Index = i,
                    Id = (Seg)i,
                    Rb = rb,
                    Parent = AnatomyData.Parent[i],
                    RL = Vector3.right,
                    UL = Vector3.up,
                    FL = Vector3.forward,
                    Length = lengths[i],
                    Mass = m,
                    InertiaDiag = rb.inertiaTensor,
                    V = initVel,
                };
                body.TotalMass += m;
            }
            for (int j = 0; j < AnatomyData.JointCount; j++)
            {
                var kind = AnatomyData.KindOf[j];
                var spec = AnatomyData.SpecOf(j);
                int side = AnatomyData.SideOf[j];
                var frame = new JointFrame
                {
                    Kind = kind,
                    Side = side,
                    PR = Vector3.right, PU = Vector3.up, PF = Vector3.forward,
                    CR = Vector3.right, CU = Vector3.up,
                    RelRest = Quaternion.identity,
                    FlexMid = 0.5f * (spec.Dofs[0].MinDeg + spec.Dofs[0].MaxDeg) * BMath.Deg,
                    HingeAxisP = kind == JointKind.Knee ? Vector3.right : -Vector3.right,
                };
                Vector3 phi0 = Vector3.zero;
                if (riflePose && (j == (int)JointId.LShoulder || j == (int)JointId.RShoulder))
                    phi0 = new Vector3(70f * BMath.Deg, 15f * BMath.Deg, 0f);
                if (riflePose && (j == (int)JointId.LElbow || j == (int)JointId.RElbow))
                    phi0 = new Vector3(95f * BMath.Deg, 0f, 0f);
                if (j == (int)JointId.LKnee || j == (int)JointId.RKnee)
                    phi0 = new Vector3(8f * BMath.Deg, 0f, 0f);

                var jm = new JointModel
                {
                    Index = j,
                    Kind = kind,
                    Spec = spec,
                    Side = side,
                    P = AnatomyData.ParentSeg(j),
                    C = AnatomyData.ChildSeg(j),
                    Frame = frame,
                    Phi = phi0,
                    Phi0 = phi0,
                    Driven = new DrivenJoint
                    {
                        Anchor = Vector3.zero,
                        Axis = frame.HingeAxisP,
                        Secondary = Vector3.forward,
                        Third = Vector3.up,
                    },
                };
                for (int s = 0; s < AnatomyData.SegCount; s++)
                {
                    int k = s;
                    while (k >= 0 && k != jm.C) k = AnatomyData.Parent[k];
                    jm.InSubtree[s] = k == jm.C;
                }
                body.Joints[j] = jm;
            }
            return body;
        }

        public void SyncKinematics(float dt)
        {
            var b = Body;
            // Pelvis (root)
            var pv = b[Seg.Pelvis];
            pv.Pos = RootPos;
            pv.Q = RootRot;
            pv.Com = pv.Pos + pv.U * (pv.Length * 0.5f);
            pv.V = RootVel;
            pv.W = RootAngVel;

            // Helpers to apply parent→child anatomical rotations
            Quaternion JointRot(int j)
            {
                var p = JointPhi[j];
                var k = AnatomyData.KindOf[j];
                int sd = AnatomyData.SideOf[j];
                if (k == JointKind.Lumbar || k == JointKind.Neck)
                    return Quaternion.AngleAxis(p.z / BMath.Deg, Vector3.up)
                         * Quaternion.AngleAxis(-p.y / BMath.Deg, Vector3.forward)
                         * Quaternion.AngleAxis(p.x / BMath.Deg, Vector3.right);
                if (k == JointKind.Hip || k == JointKind.Shoulder)
                    return Quaternion.AngleAxis(-p.x / BMath.Deg, Vector3.right)
                         * Quaternion.AngleAxis(sd * p.y / BMath.Deg, Vector3.forward)
                         * Quaternion.AngleAxis(p.z / BMath.Deg, Vector3.up);
                if (k == JointKind.Knee)
                    return Quaternion.AngleAxis(p.x / BMath.Deg, Vector3.right);
                // Elbow: flexion (-X) + pronation-supination (+Y)
                return Quaternion.AngleAxis(-p.x / BMath.Deg, Vector3.right)
                     * Quaternion.AngleAxis(p.z / BMath.Deg, Vector3.up);
            }

            void Place(Seg s, Vector3 attachWorld, Quaternion parentQ, int jointIdx, bool upward)
            {
                var seg = b[s];
                seg.Pos = attachWorld;
                seg.Q = BMath.Normalize(parentQ * JointRot(jointIdx));
                seg.Com = upward ? seg.Pos + seg.U * (seg.Length * 0.5f) : seg.Pos - seg.U * (seg.Length * 0.5f);
                seg.V = RootVel + Vector3.Cross(RootAngVel, seg.Com - RootPos);
                seg.W = RootAngVel;
                if (seg.Rb != null) { seg.Rb.velocity = seg.V; seg.Rb.angularVelocity = seg.W; }
            }

            Place(Seg.Thorax, pv.Pos + pv.U * pv.Length, pv.Q, (int)JointId.Lumbar, true);
            var th = b[Seg.Thorax];
            Place(Seg.Head, th.Pos + th.U * th.Length, th.Q, (int)JointId.Neck, true);

            Vector3 shL = th.Pos + th.U * (th.Length * 0.88f) - th.R * 0.18f;
            Vector3 shR = th.Pos + th.U * (th.Length * 0.88f) + th.R * 0.18f;
            Place(Seg.LUpperArm, shL, th.Q, (int)JointId.LShoulder, false);
            Place(Seg.RUpperArm, shR, th.Q, (int)JointId.RShoulder, false);
            Place(Seg.LForearm, b[Seg.LUpperArm].DistalWorld, b[Seg.LUpperArm].Q, (int)JointId.LElbow, false);
            Place(Seg.RForearm, b[Seg.RUpperArm].DistalWorld, b[Seg.RUpperArm].Q, (int)JointId.RElbow, false);

            Vector3 hipL = pv.Pos - pv.R * 0.10f;
            Vector3 hipR = pv.Pos + pv.R * 0.10f;
            Place(Seg.LThigh, hipL, pv.Q, (int)JointId.LHip, false);
            Place(Seg.RThigh, hipR, pv.Q, (int)JointId.RHip, false);
            Place(Seg.LCalf, b[Seg.LThigh].DistalWorld, b[Seg.LThigh].Q, (int)JointId.LKnee, false);
            Place(Seg.RCalf, b[Seg.RThigh].DistalWorld, b[Seg.RThigh].Q, (int)JointId.RKnee, false);

            float maxSpd = 0f;
            for (int i = 0; i < b.Segs.Length; i++) maxSpd = MathF.Max(maxSpd, b.Segs[i].V.magnitude);
            b.MaxSpeed = maxSpd;
            for (int j = 0; j < AnatomyData.JointCount; j++)
            {
                b.Joints[j].Phi = JointPhi[j];
                b.Joints[j].PhiDot = JointVel[j];
            }
        }

        public void Step(float t, float dt)
        {
            foreach (var s in Body.Segs) s.Rb.ClearAccum();
            Buffer.Reset();
            State.Time = t;
            State.Dt = dt;
            State.Agony = Neural.AgonyIntensity(t);

            Perf.Update(State, t, dt);
            Perf.Impulses.Step(t, dt);
            Perf.Forces(t, dt);

            // Integration of joint motor commands (exact ω, ζ, target written to MotorCommandBuffer).
            for (int j = 0; j < AnatomyData.JointCount; j++)
            {
                var spec = AnatomyData.SpecOf(j);
                for (int d = 0; d < 3; d++)
                {
                    if (Buffer.Resolve(j, d, out MotorCommand mc) && mc.U > 0.01f)
                    {
                        float k = mc.Omega * mc.Omega * mc.U * MathF.Max(0.25f, mc.MuscleStiffness);
                        float c = 2f * mc.Zeta * mc.Omega * MathF.Sqrt(mc.U);
                        float acc = k * (mc.Target - JointPhi[j][d]) - c * JointVel[j][d];
                        JointVel[j][d] = Math.Clamp(JointVel[j][d] + acc * dt, -14f, 14f);
                    }
                    else
                    {
                        JointVel[j][d] *= MathF.Exp(-4f * dt);
                    }
                    float lo = spec.Dofs[d].MinDeg * BMath.Deg, hi = spec.Dofs[d].MaxDeg * BMath.Deg;
                    JointPhi[j][d] = Math.Clamp(JointPhi[j][d] + JointVel[j][d] * dt, lo, hi);
                }
            }

            // Aggregation of whole-body forces and torques on the mannequin.
            Vector3 netForce = Physics.gravity * Body.TotalMass;
            Vector3 netTorque = Vector3.zero;
            Vector3 com = Perf.Ch.Com;
            float Ibody = MathF.Max(4f, Perf.Ch.IBody);
            for (int i = 0; i < Body.Segs.Length; i++)
            {
                var s = Body.Segs[i];
                var rb = s.Rb;
                Vector3 f = rb.ForceAccum + rb.AccelAccum * s.Mass;
                Vector3 tq = rb.TorqueAccum + rb.AngAccelAccum * ((s.InertiaDiag.x + s.InertiaDiag.y + s.InertiaDiag.z) / 3f);
                netForce += f;
                netTorque += tq + Vector3.Cross(s.Com - com, f);
                // Transfer of impulses applied directly to rigidbodies.
                RootVel += rb.ImpulseAccum / Body.TotalMass;
                RootAngVel += rb.AngImpulseAccum / Ibody;
            }

            RootVel += (netForce / Body.TotalMass) * dt;
            RootAngVel += (netTorque / Ibody) * dt;
            RootAngVel *= MathF.Exp(-1.2f * dt);
            RootPos += RootVel * dt;

            // Ground contact: prevents sinking below GroundY + horizontal friction.
            float minY = float.MaxValue;
            foreach (var s in Body.Segs) minY = MathF.Min(minY, MathF.Min(s.Com.y, s.DistalWorld.y));
            float pen = (Physics.GroundY + 0.04f) - minY;
            if (pen > 0f)
            {
                RootPos.y += pen;
                if (RootVel.y < 0f) RootVel.y = 0f;
                RootVel.x *= MathF.Exp(-5f * dt);
                RootVel.z *= MathF.Exp(-5f * dt);
            }
            if (RootPos.y < Physics.GroundY + 0.14f)
            {
                RootPos.y = Physics.GroundY + 0.14f;
                if (RootVel.y < 0f) RootVel.y = 0f;
            }

            float wMag = RootAngVel.magnitude;
            if (wMag > 1e-5f)
                RootRot = BMath.Normalize(Quaternion.AngleAxis(wMag * dt * Mathf.Rad2Deg, RootAngVel / wMag) * RootRot);

            SyncKinematics(dt);
            RecordTelemetry();
        }

        private void RecordTelemetry()
        {
            if (float.IsNaN(RootPos.x) || float.IsNaN(RootPos.y) || float.IsNaN(RootRot.w)) AnyNaN = true;
            MinPelvisY = MathF.Min(MinPelvisY, Body[Seg.Pelvis].Com.y);
            PeakLeftKneeFlex = MathF.Max(PeakLeftKneeFlex, JointPhi[(int)JointId.LKnee].x);
            PeakRightKneeFlex = MathF.Max(PeakRightKneeFlex, JointPhi[(int)JointId.RKnee].x);
            PeakPronationRad = MathF.Max(PeakPronationRad, MathF.Max(MathF.Abs(JointPhi[(int)JointId.LElbow].z), MathF.Abs(JointPhi[(int)JointId.RElbow].z)));
            if (Perf.Shot != null)
            {
                PeakShockSpinTorque = MathF.Max(PeakShockSpinTorque, MathF.Abs(Perf.Shot.LastShockSpinTorque));
                if (Perf.Shot.ReachedOnce) AnyWoundReach = true;
            }
            if (Perf.Grab != null)
            {
                AnyGrabActivated = true;
                if (Perf.Grab.EverGripped) AnyGrabGripped = true;
                if (Perf.Grab.SnapCount > 0) AnyGrabSnapped = true;
            }
            if (Perf.Flinch != null) AnyFlinchActivated = true;
            if (Perf.RollDownStairs != null)
            {
                AnyRollActivated = true;
                PeakRollAngVel = MathF.Max(PeakRollAngVel, RootAngVel.magnitude);
            }
            if (Perf.Windmill != null) AnyWindmillActivated = true;
            if (Perf.CatchFall != null && (Perf.CatchFall.ArmHitSurface[0] || Perf.CatchFall.ArmHitSurface[1]))
                AnyCatchFallHitFloor = true;
            if (Perf.WallBrace != null && Perf.WallBrace.Active)
            {
                AnyWallBrace = true;
                if (Perf.WallBrace.Slumping) AnyWallSlump = true;
                if (Perf.WallBrace.FallingOverWall) AnyFallOverWall = true;
            }
            if (Perf.HighFall != null && Perf.HighFall.Active)
            {
                AnyHighFall = true;
                if (Perf.HighFall.PreparingToLand) AnyHighFallLand = true;
            }
            if (Perf.OnFire != null && Perf.OnFire.Active) AnyOnFire = true;
            if (Perf.MeleeGuard != null && Perf.MeleeGuard.Active) AnyMeleeGuard = true;
            if (Perf.Balance != null) PeakSteps = Math.Max(PeakSteps, Perf.Balance.Steps);
        }
    }
}
