using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Euphoria
{
    /// <summary>
    /// Idiomuscular contraction (Zsako phenomenon): after death, skeletal muscle remains mechanically
    /// excitable for several hours; a shock to the muscle triggers a brief local contraction
    /// (Madea, "Estimation of the Time Since Death", 3rd ed., CRC 2016, supravital reactions). Here: the
    /// joints adjacent to the hit segment contract briefly, then relax.
    /// </summary>
    internal sealed class IdiomuscularTwitch : MotorProgram
    {
        private readonly JointId[] _joints;
        private readonly float[] _flex0, _side0;
        private readonly float _u, _dir;
        private readonly BodyModel _body;

        public IdiomuscularTwitch(BodyModel body, Seg seg, float t, float gain, System.Random rng)
        {
            Name = "idiomuscular twitch";
            Start = t;
            Duration = BMath.Range(rng, 0.35f, 0.6f);
            Attack = 0.03f;
            Release = 0.3f;
            _body = body;
            _u = Mathf.Clamp01(gain);
            _dir = BMath.Chance(rng, 0.5f) ? 1f : -1f;
            _joints = JointsFor(seg);
            _flex0 = new float[_joints.Length];
            _side0 = new float[_joints.Length];
            for (int i = 0; i < _joints.Length; i++)
            {
                var jm = body.Joints[(int)_joints[i]];
                _flex0[i] = jm.Phi.x;
                _side0[i] = jm.Phi.y;
            }
        }

        private static JointId[] JointsFor(Seg s)
        {
            switch (s)
            {
                case Seg.Pelvis: return new[] { JointId.LHip, JointId.RHip, JointId.Lumbar };
                case Seg.Thorax: return new[] { JointId.Lumbar, JointId.Neck, JointId.LShoulder, JointId.RShoulder };
                case Seg.Head: return new[] { JointId.Neck };
                case Seg.LUpperArm: return new[] { JointId.LShoulder, JointId.LElbow };
                case Seg.LForearm: return new[] { JointId.LElbow };
                case Seg.RUpperArm: return new[] { JointId.RShoulder, JointId.RElbow };
                case Seg.RForearm: return new[] { JointId.RElbow };
                case Seg.LThigh: return new[] { JointId.LHip, JointId.LKnee };
                case Seg.LCalf: return new[] { JointId.LKnee };
                case Seg.RThigh: return new[] { JointId.RHip, JointId.RKnee };
                case Seg.RCalf: return new[] { JointId.RKnee };
            }
            return new JointId[0];
        }

        protected override void Emit(NeuralState ns, float k)
        {
            float u = _u * k;
            for (int i = 0; i < _joints.Length; i++)
            {
                var kind = _body.Joints[(int)_joints[i]].Kind;
                bool hinge = kind == JointKind.Elbow || kind == JointKind.Knee;
                float df = hinge ? 40f : kind == JointKind.Lumbar ? 18f : kind == JointKind.Neck ? 25f : 35f;
                ns.Buffer.AddNm((int)_joints[i], 0, _flex0[i] + df * BMath.Deg * (hinge ? 1f : _dir), u, 18f, 0.6f, 1f);
                if (kind == JointKind.Shoulder || kind == JointKind.Hip)
                    ns.Buffer.AddNm((int)_joints[i], 1, _side0[i] + 20f * BMath.Deg * _dir, u, 18f, 0.6f, 1f);
            }
        }
    }
}
