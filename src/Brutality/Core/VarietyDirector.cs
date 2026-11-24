using System;
using System.Collections.Generic;
using Brutality.Neural;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>
    /// Signature d'une mort : ce qui distingue cette agonie de la précédente. Elle est tirée une seule fois, à la mort,
    /// puis lue par le contrôleur neural (palette de gestes, cadence, asymétrie) et par la performance Euphoria
    /// (bascule, vrille).
    /// </summary>
    internal sealed class DeathSignature
    {
        /// <summary>Cadence des gestes involontaires (0,8 à 1,28).</summary>
        public float Tempo = 1f;
        /// <summary>Amplitude du tremblement (0,7 à 1,4).</summary>
        public float Tremor = 1f;
        /// <summary>Amplitude des contorsions continues (0,7 à 1,35).</summary>
        public float Writhing = 1f;
        /// <summary>Asymétrie du corps (-1 à 1) : un côté est plus actif que l'autre.</summary>
        public float Asymmetry;
        /// <summary>Modulation de la bascule de chute (0,8 à 1,25).</summary>
        public float ToppleJitter = 1f;
        /// <summary>Multiplicateur de poids par geste : la « palette » de cette mort.</summary>
        public float[] Palette = Array.Empty<float>();
    }

    /// <summary>
    /// Mémoire des morts récentes. Deux morts ne doivent jamais se ressembler : ce directeur retient les plans de
    /// chute et les gestes des dernières morts de la session et pénalise leur retour, et tire pour chaque mort une
    /// palette de gestes privilégiés / atténués. Le résultat est combinatoire : quelques plans × une palette propre
    /// à chaque mort × des paramètres continus (cadence, asymétrie, bascule) donnent un très grand nombre de
    /// cinématiques distinctes.
    /// </summary>
    internal static class VarietyDirector
    {
        private const int PlanMemory = 5;
        /// <summary>Nombre de morts prises en compte pour la mémoire des gestes.</summary>
        private const int GestureDeaths = 2;

        private static readonly List<int> Plans = new List<int>(PlanMemory);
        private static readonly List<int> Sectors = new List<int>(2);
        private static readonly List<int> LastGestures = new List<int>(16);
        private static readonly List<int> PreviousGestures = new List<int>(16);
        private static int _deathIndex;

        /// <summary>Nombre de morts traitées depuis le chargement (journal).</summary>
        public static int DeathCount => _deathIndex;

        /// <summary>Nouvelle mort : la mort précédente devient l'historique, l'historique ancien est oublié.</summary>
        public static void BeginDeath()
        {
            _deathIndex++;
            PreviousGestures.Clear();
            PreviousGestures.AddRange(LastGestures);
            LastGestures.Clear();
        }

        // ---------------------------------------------------------------- plans de chute

        /// <summary>
        /// Poids relatif d'un plan de chute : un plan déjà joué récemment est fortement déconseillé, pour qu'une
        /// même silhouette de mort ne revienne pas avant plusieurs corps.
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

        /// <summary>Secteur de chute dans le repère du corps : 0 avant, 1 côté, 2 arrière.</summary>
        public static int Sector(float azimuth)
        {
            float a = Mathf.Abs(Mathf.Repeat(azimuth, BMath.TwoPi));
            if (a > Mathf.PI) a = BMath.TwoPi - a;
            return a < Mathf.PI * 0.25f ? 0 : a > Mathf.PI * 0.75f ? 2 : 1;
        }

        /// <summary>
        /// Pénalité de direction : après une chute arrière, une autre chute arrière est improbable. Sans cette
        /// mémoire, les plans arrière (effondrement, titubation, assis, planche) s'enchaînent et donnent l'impression
        /// que tous les corps basculent du même côté.
        /// </summary>
        public static float SectorMultiplier(float azimuth)
        {
            int s = Sector(azimuth);
            float m = 1f;
            if (Sectors.Count > 0 && Sectors[0] == s) m *= 0.3f;
            if (Sectors.Count > 1 && Sectors[1] == s) m *= 0.65f;
            return m;
        }

        // ---------------------------------------------------------------- gestes

        public static void RecordGesture(AgonyEvent e)
        {
            int i = (int)e;
            if (!LastGestures.Contains(i)) LastGestures.Add(i);
        }

        /// <summary>
        /// Pénalité de récence d'un geste : celui de la mort précédente est rare, celui de l'avant-dernière le sera
        /// un peu moins. Un geste peut donc revenir, mais jamais comme habitude.
        /// </summary>
        public static float GestureMultiplier(AgonyEvent e)
        {
            int i = (int)e;
            float m = 1f;
            if (LastGestures.Contains(i)) m *= 0.3f;
            if (GestureDeaths > 1 && PreviousGestures.Contains(i)) m *= 0.6f;
            return m;
        }

        // ---------------------------------------------------------------- tirage de la signature

        /// <summary>
        /// Tire la signature de la mort. <paramref name="variety"/> (réglage « Variété des gestes ») module l'écart
        /// entre les palettes : à 0, toutes les morts partagent la même palette neutre.
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

            // Gestes privilégiés : chaque mort a sa « signature » gestuelle.
            int boost = 4 + rng.Next(3);
            for (int b = 0; b < boost; b++)
            {
                int i = rng.Next(n);
                float m = BMath.Range(rng, 1.5f, 3.2f);
                s.Palette[i] = Mathf.Max(s.Palette[i], Mathf.Lerp(1f, m, v));
            }
            // Gestes atténués : deux morts de suite ne montrent pas les mêmes réflexes.
            int damp = 3 + rng.Next(3);
            for (int d = 0; d < damp; d++)
            {
                int i = rng.Next(n);
                if (s.Palette[i] > 1.05f) continue;
                s.Palette[i] = Mathf.Lerp(1f, BMath.Range(rng, 0.12f, 0.45f), v);
            }
            return s;
        }

        /// <summary>Réinitialise la mémoire (changement de raid : les corps précédents ne sont plus visibles).</summary>
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
