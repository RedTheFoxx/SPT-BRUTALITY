using System;
using System.Collections.Generic;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>
    /// Death signature: what distinguishes this agony from the previous one. Drawn once at death,
    /// then read by the neural controller (gesture palette, tempo, asymmetry) and by the Euphoria performance
    /// (topple, spin).
    /// </summary>
    internal sealed class DeathSignature
    {
        /// <summary>Cadence of involuntary gestures (0.8 to 1.28).</summary>
        public float Tempo = 1f;
        /// <summary>Tremor amplitude (0.7 to 1.4).</summary>
        public float Tremor = 1f;
        /// <summary>Continuous writhing amplitude (0.7 to 1.35).</summary>
        public float Writhing = 1f;
        /// <summary>Body asymmetry (-1 to 1): one side is more active than the other.</summary>
        public float Asymmetry;
        /// <summary>Fall topple modulation (0.8 to 1.25).</summary>
        public float ToppleJitter = 1f;
        /// <summary>Weight multiplier per gesture: the "palette" of this death.</summary>
        public float[] Palette = Array.Empty<float>();
    }

    /// <summary>
    /// Memory of recent deaths. No two deaths should ever look alike: this director remembers the fall plans
    /// and gestures of recent deaths in the session, penalizes their recurrence, and draws a palette of
    /// favored / attenuated gestures for each death. The result is combinatorial: a few plans × a per-death
    /// palette × continuous parameters (tempo, asymmetry, topple) yield a huge number of distinct kinematics.
    /// </summary>
    internal static class VarietyDirector
    {
        private const int PlanMemory = 5;
        /// <summary>Number of deaths considered for gesture memory.</summary>
        private const int GestureDeaths = 2;

        private static readonly List<int> Plans = new List<int>(PlanMemory);
        private static readonly List<int> Sectors = new List<int>(2);
        private static readonly List<int> LastGestures = new List<int>(16);
        private static readonly List<int> PreviousGestures = new List<int>(16);
        private static int _deathIndex;

        /// <summary>Number of deaths processed since loading (log).</summary>
        public static int DeathCount => _deathIndex;

        /// <summary>New death: the previous death becomes history, older history is forgotten.</summary>
        public static void BeginDeath()
        {
            _deathIndex++;
            PreviousGestures.Clear();
            PreviousGestures.AddRange(LastGestures);
            LastGestures.Clear();
        }

        // ---------------------------------------------------------------- fall plans

        /// <summary>
        /// Relative weight of a fall plan: a recently played plan is strongly discouraged so that
        /// the same death silhouette does not repeat for several bodies.
        /// </summary>
        public static float PlanMultiplier(int kind)
        {
            float m = 1f;
            for (int i = 0; i < Plans.Count && i < PlanMemory; i++)
            {
                if (Plans[i] != kind) continue;
                float f = i == 0 ? 0.12f : i == 1 ? 0.28f : i == 2 ? 0.5f : 0.72f;
                m = Mathf.Min(m, f);
            }
            return Mathf.Max(0.05f, m);
        }

        public static void RecordPlan(int kind, float azimuth)
        {
            Plans.Insert(0, kind);
            while (Plans.Count > PlanMemory) Plans.RemoveAt(Plans.Count - 1);
            Sectors.Insert(0, Sector(azimuth));
            while (Sectors.Count > 2) Sectors.RemoveAt(Sectors.Count - 1);
        }

        /// <summary>Fall sector in the body frame: 0 front, 1 side, 2 back.</summary>
        public static int Sector(float azimuth)
        {
            float a = Mathf.Abs(Mathf.Repeat(azimuth, BMath.TwoPi));
            if (a > Mathf.PI) a = BMath.TwoPi - a;
            return a < Mathf.PI * 0.25f ? 0 : a > Mathf.PI * 0.75f ? 2 : 1;
        }

        /// <summary>
        /// Direction penalty: after a backward fall, another backward fall is unlikely. Without this
        /// memory, backward plans (collapse, stagger, sit, plank) chain together and make it look like
        /// all bodies topple to the same side.
        /// </summary>
        public static float SectorMultiplier(float azimuth)
        {
            int s = Sector(azimuth);
            float m = 1f;
            if (Sectors.Count > 0 && Sectors[0] == s) m *= 0.3f;
            if (Sectors.Count > 1 && Sectors[1] == s) m *= 0.65f;
            return m;
        }

        // ---------------------------------------------------------------- gestures

        public static void RecordGesture(AgonyEvent e)
        {
            int i = (int)e;
            if (!LastGestures.Contains(i)) LastGestures.Add(i);
        }

        /// <summary>
        /// Recency penalty for a gesture: one from the previous death is rare, one from two deaths ago
        /// slightly less so. A gesture can still recur, but never as a habit.
        /// </summary>
        public static float GestureMultiplier(AgonyEvent e)
        {
            int i = (int)e;
            float m = 1f;
            if (LastGestures.Contains(i)) m *= 0.3f;
            if (GestureDeaths > 1 && PreviousGestures.Contains(i)) m *= 0.6f;
            return m;
        }

        // ---------------------------------------------------------------- signature draw

        /// <summary>
        /// Draws the death signature. <paramref name="variety"/> ("Gesture variety" setting) modulates the spread
        /// between palettes: at 0, all deaths share the same neutral palette.
        /// </summary>
        public static DeathSignature Draw(System.Random rng, float variety)
        {
            float v = Mathf.Clamp01(variety);
            var s = new DeathSignature
            {
                Tempo = BMath.Range(rng, Mathf.Lerp(0.94f, 0.8f, v), Mathf.Lerp(1.06f, 1.28f, v)),
                Tremor = BMath.Range(rng, Mathf.Lerp(0.9f, 0.7f, v), Mathf.Lerp(1.1f, 1.4f, v)),
                Writhing = BMath.Range(rng, Mathf.Lerp(0.9f, 0.7f, v), Mathf.Lerp(1.1f, 1.35f, v)),
                Asymmetry = BMath.Chance(rng, 0.6f) ? BMath.Range(rng, -1f, 1f) * Mathf.Lerp(0.4f, 1f, v) : 0f,
                ToppleJitter = BMath.Range(rng, 0.82f, 1.22f),
            };

            int n = (int)AgonyEvent.Count;
            s.Palette = new float[n];
            for (int i = 0; i < n; i++) s.Palette[i] = 1f;

            // Favored gestures: each death has its own gestural "signature".
            int boost = 4 + rng.Next(3);
            for (int b = 0; b < boost; b++)
            {
                int i = rng.Next(n);
                float m = BMath.Range(rng, 1.5f, 3.2f);
                s.Palette[i] = Mathf.Max(s.Palette[i], Mathf.Lerp(1f, m, v));
            }
            // Attenuated gestures: two consecutive deaths do not show the same reflexes.
            int damp = 3 + rng.Next(3);
            for (int d = 0; d < damp; d++)
            {
                int i = rng.Next(n);
                if (s.Palette[i] > 1.05f) continue;
                s.Palette[i] = Mathf.Lerp(1f, BMath.Range(rng, 0.12f, 0.45f), v);
            }
            return s;
        }

        /// <summary>Resets memory (raid change: previous bodies are no longer visible).</summary>
        public static void Reset()
        {
            Plans.Clear();
            Sectors.Clear();
            LastGestures.Clear();
            PreviousGestures.Clear();
            _deathIndex = 0;
        }
    }
}
