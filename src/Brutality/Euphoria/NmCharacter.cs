using Brutality.Anatomy;
using Brutality.Core;
using UnityEngine;

namespace Brutality.Euphoria
{
    /// <summary>
    /// Character sensors, like those used by NaturalMotion behaviors: center of mass and velocity,
    /// foot and hand contacts, ground height (raycast), orientation, angular momentum around the CoM.
    /// </summary>
    internal sealed class NmCharacter
    {
        public Vector3 Com, Vel, L;
        public readonly Vector3[] Ankle = new Vector3[2], Foot = new Vector3[2], Hand = new Vector3[2], Knee = new Vector3[2];
        public readonly bool[] Contact = new bool[2], HandContact = new bool[2];
        public float GroundY, FootMinY, H = 0.95f, Hip2HeadUp = 1f, IBody = 10f;
        public Vector3 Fwd = Vector3.forward, Right = Vector3.right;
        public bool Lying, Airborne;

        private static int _mask;   // 0 = mask not yet resolved
        private float _groundT = -1f;
        private readonly BodyModel _body;

        public NmCharacter(BodyModel body) { _body = body; }

        public static int Si(int side) => side < 0 ? 0 : 1;

        public static int EnvironmentMask
        {
            get
            {
                if (_mask != 0) return _mask;
                try { _mask = LayersMaskController.HighPolyWithTerrainMask; }
                catch { }
                // The fallback is never cached: EFT masks may only become available after
                // raid loading, and a frozen fallback would disable the ground raycast for the session.
                return _mask != 0 ? _mask : Physics.DefaultRaycastLayers;
            }
        }

        public void Sense(float t)
        {
            var b = _body;
            Vector3 com = Vector3.zero, vel = Vector3.zero;
            float m = 0f;
            foreach (var s in b.Segs) { com += s.Com * s.Mass; vel += s.V * s.Mass; m += s.Mass; }
            if (m <= 0f) return;
            Com = com / m;
            Vel = vel / m;

            // Ground under the center of mass (raycast every 0.1 s: sufficient for continuous ground).
            if (t - _groundT > 0.1f || _groundT < 0f)
            {
                _groundT = t;
                float lowest = float.MaxValue;
                foreach (var s in b.Segs) lowest = Mathf.Min(lowest, s.Com.y);
                if (Physics.Raycast(new Vector3(Com.x, Com.y + 0.3f, Com.z), Vector3.down, out RaycastHit hit, 3.5f, EnvironmentMask, QueryTriggerInteraction.Ignore)
                    && BrutalityRagdoll.OwnerOf(hit.rigidbody) == null)
                    GroundY = hit.point.y;
                else
                    GroundY = lowest - 0.08f;
            }

            for (int k = 0; k < 2; k++)
            {
                int side = k == 0 ? -1 : 1;
                var calf = b[side < 0 ? Seg.LCalf : Seg.RCalf];
                var fore = b[side < 0 ? Seg.LForearm : Seg.RForearm];
                Ankle[k] = calf.DistalWorld;
                Knee[k] = calf.Pos;
                Foot[k] = Ankle[k] + Flat(calf.F) * 0.06f;
                Hand[k] = fore.DistalWorld - fore.U * 0.07f;
                Contact[k] = Mathf.Min(Ankle[k].y, Foot[k].y) - GroundY < 0.13f;
                HandContact[k] = Hand[k].y - GroundY < 0.08f;
            }
            FootMinY = Mathf.Min(Ankle[0].y, Ankle[1].y) - 0.07f;
            H = Mathf.Max(0.3f, Com.y - FootMinY);

            var pelvis = b[Seg.Pelvis];
            var thorax = b[Seg.Thorax];
            var head = b[Seg.Head];
            Vector3 f = Flat(pelvis.F);
            if (f.sqrMagnitude < 0.04f) f = -Flat(pelvis.U);
            Fwd = BMath.SafeNormalize(f, Vector3.forward);
            Right = Vector3.Cross(Vector3.up, Fwd).normalized;   // Unity left-handed frame: up × forward = right
            Hip2HeadUp = Vector3.Dot(BMath.SafeNormalize(head.Com - pelvis.Com, Vector3.up), Vector3.up);
            Lying = thorax.Com.y - GroundY < 0.42f && pelvis.Com.y - GroundY < 0.38f;
            float minY = float.MaxValue;
            foreach (var s in b.Segs) minY = Mathf.Min(minY, s.Com.y);
            Airborne = minY - GroundY > 0.3f && !Contact[0] && !Contact[1];

            // Angular momentum around the CoM and scalar body inertia (setCharacterDamping).
            Vector3 Lsum = Vector3.zero;
            float I = 0f;
            foreach (var s in b.Segs)
            {
                Vector3 r = s.Com - Com;
                Lsum += s.Mass * Vector3.Cross(r, s.V - Vel);
                Quaternion qI = s.Q * s.InertiaRotLocal;
                Vector3 wl = Quaternion.Inverse(qI) * s.W;
                Lsum += qI * Vector3.Scale(s.InertiaDiag, wl);
                I += s.Mass * r.sqrMagnitude + (s.InertiaDiag.x + s.InertiaDiag.y + s.InertiaDiag.z) / 3f;
            }
            L = Lsum;
            IBody = Mathf.Max(2f, I);
        }

        public static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
