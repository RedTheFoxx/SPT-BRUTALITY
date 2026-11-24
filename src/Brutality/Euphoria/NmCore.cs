using System.Collections.Generic;
using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Euphoria
{
    /// <summary>Impact d'un projectile tel que le voient les comportements (shotNewBullet).</summary>
    internal sealed class NmHit
    {
        public Segment Part;
        public Vector3 Local;      // point d'impact dans le repère du segment
        public Vector3 Dir;        // direction du projectile (unitaire)
        public float P;            // quantité de mouvement transmise (N·s)
        public int Side;           // -1 gauche, +1 droite, 0 axial
        public int ArmSide;        // bras touché (-1/+1) ou 0
        public int LegSide;        // jambe touchée (-1/+1) ou 0
        public bool Torso;
        public bool Reachable;     // une main peut s'y porter (tronc, bras)

        public Vector3 World => Part.Pos + Part.Q * Local;

        public static NmHit Make(BodyModel body, Segment seg, Vector3 point, Vector3 dir, float p)
        {
            var h = new NmHit { Part = seg, Local = Quaternion.Inverse(seg.Q) * (point - seg.Pos), Dir = BMath.SafeNormalize(dir, Vector3.forward), P = p };
            switch (seg.Id)
            {
                case Seg.LUpperArm: case Seg.LForearm: h.Side = -1; h.ArmSide = -1; break;
                case Seg.RUpperArm: case Seg.RForearm: h.Side = 1; h.ArmSide = 1; break;
                case Seg.LThigh: case Seg.LCalf: h.Side = -1; h.LegSide = -1; break;
                case Seg.RThigh: case Seg.RCalf: h.Side = 1; h.LegSide = 1; break;
                default:
                {
                    var th = body[Seg.Thorax];
                    float lat = Vector3.Dot(point - th.Com, th.R);
                    h.Side = Mathf.Abs(lat) > 0.04f ? (lat > 0f ? 1 : -1) : 0;
                    break;
                }
            }
            h.Torso = seg.Id == Seg.Thorax || seg.Id == Seg.Pelvis;
            h.Reachable = h.Torso || h.ArmSide != 0;
            return h;
        }
    }

    /// <summary>Cinématique et capacité articulaire partagées par les comportements Delirium.</summary>
    internal static class Nm
    {
        /// <summary>Cheville vers un point du monde ; repère du bassin (R, U, F) et position de hanche fournis.</summary>
        public static bool LegIK(BodyModel b, int side, Vector3 hip, Vector3 ankleTarget, Vector3 R, Vector3 U, Vector3 F, out float flex, out float abd, out float knee)
        {
            var thigh = b[side < 0 ? Seg.LThigh : Seg.RThigh];
            var calf = b[side < 0 ? Seg.LCalf : Seg.RCalf];
            return JointKinematics.LegIK(hip, thigh.Length, calf.Length, ankleTarget, side, R, U, F, out flex, out abd, out knee);
        }

        public static float ElbowCap(BodyModel b, int side) => b.Joints[(int)AnatomyData.Elbow(side)].Capacity[0];
    }

    /// <summary>Forces de corps entier partagées par les comportements.</summary>
    internal static class NmPhysics
    {
        /// <summary>Accélération angulaire du corps entier autour du CdM : m·α×r sur chaque segment, plus α.</summary>
        public static void BodyAngularAccel(BodyModel body, Vector3 alpha)
        {
            Vector3 com = Vector3.zero;
            float m = 0f;
            foreach (var s in body.Segs) { com += s.Com * s.Mass; m += s.Mass; }
            if (m <= 0f) return;
            com /= m;
            foreach (var s in body.Segs)
            {
                s.Rb.AddForce(Vector3.Cross(alpha, s.Com - com), ForceMode.Acceleration);
                s.Rb.AddTorque(alpha, ForceMode.Acceleration);
            }
        }

        public static void WakeAll(BodyModel body)
        {
            foreach (var s in body.Segs) if (s.Rb != null) s.Rb.WakeUp();
        }
    }

    /// <summary>
    /// Impulsions balistiques (configureBullets / applyBulletImpulse) : chaque impulsion est étalée sur impulsePeriod
    /// (profil triangulaire), appliquée au point d'impact (force au CdM + couple × impulseTorqueScale). La part « corps
    /// rigide » (rbRatio) est répartie sur tout le corps (Δv commune, rbLowerShare = 0,5 → au prorata des masses) avec un
    /// moment borné par rbMaxTwistMomentArm (torsion autour de la verticale) et rbMaxBroomMomentArm (bascule).
    /// </summary>
    internal sealed class NmImpulseQueue
    {
        private struct Entry
        {
            public Segment Seg;
            public Vector3 Local, J, Rb;
            public float T0;
            public bool CounterDone;
        }

        private readonly List<Entry> _q = new List<Entry>(8);
        private readonly BodyModel _body;
        private readonly NmCharacter _ch;

        public NmImpulseQueue(BodyModel body, NmCharacter ch) { _body = body; _ch = ch; }

        public bool Pending => _q.Count > 0;

        public void Queue(float t, Segment seg, Vector3 point, Vector3 J, Vector3 rbImpulse)
        {
            _q.Add(new Entry { Seg = seg, Local = Quaternion.Inverse(seg.Q) * (point - seg.Pos), J = J, Rb = rbImpulse, T0 = t, CounterDone = !NmBullets.DoCounterImpulse });
            NmPhysics.WakeAll(_body);
        }

        private static float Tri(float x) => x <= 0f ? 0f : x >= 1f ? 1f : x < 0.5f ? 2f * x * x : 1f - 2f * (1f - x) * (1f - x);

        public void Step(float t, float dt)
        {
            for (int i = _q.Count - 1; i >= 0; i--)
            {
                var e = _q[i];
                float x0 = (t - e.T0) / NmBullets.ImpulsePeriod, x1 = (t + dt - e.T0) / NmBullets.ImpulsePeriod;
                if (x1 <= 0f) continue;
                float frac = Tri(x1) - Tri(x0);
                if (frac > 0f && e.Seg.Rb != null)
                {
                    Vector3 p = e.Seg.Pos + e.Seg.Q * e.Local;
                    Vector3 Jf = e.J * frac;
                    e.Seg.Rb.AddForce(Jf, ForceMode.Impulse);
                    e.Seg.Rb.AddTorque(Vector3.Cross(p - e.Seg.Com, Jf) * NmBullets.ImpulseTorqueScale, ForceMode.Impulse);
                    if (e.Rb.sqrMagnitude > 1e-8f) RigidBodyImpulse(e.Rb * frac, p);
                }
                if (!e.CounterDone && t - e.T0 > NmBullets.CounterImpulseDelay)
                {
                    e.CounterDone = true;
                    _body[Seg.Pelvis].Rb.AddForce(-e.J * (NmBullets.CounterImpulseMag * NmBullets.CounterImpulse2Hips), ForceMode.Impulse);
                }
                _q[i] = e;
                if (x1 >= 1f && e.CounterDone) _q.RemoveAt(i);
            }
        }

        private void RigidBodyImpulse(Vector3 J, Vector3 point)
        {
            float M = Mathf.Max(20f, _body.TotalMass);
            Vector3 dv = J / M;
            Vector3 r = point - _ch.Com;
            Vector3 mom = Vector3.Cross(r, J) * NmBullets.RbMoment;
            Vector3 twist = Vector3.up * Vector3.Dot(mom, Vector3.up), broom = mom - twist;
            float jh = Mathf.Max(1e-6f, NmCharacter.Flat(J).magnitude), jt = Mathf.Max(1e-6f, J.magnitude);
            twist = Vector3.ClampMagnitude(twist, NmBullets.RbMaxTwistMomentArm * jh);
            broom = Vector3.ClampMagnitude(broom, NmBullets.RbMaxBroomMomentArm * jt);
            Vector3 dw = (twist + broom) / _ch.IBody;
            foreach (var s in _body.Segs)
            {
                if (s.Rb == null) continue;
                s.Rb.AddForce(dv + Vector3.Cross(dw, s.Com - _ch.Com), ForceMode.VelocityChange);
                s.Rb.AddTorque(dw, ForceMode.VelocityChange);
            }
        }
    }
}
