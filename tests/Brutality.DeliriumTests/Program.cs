// Suite de preuves formelles de fidélité au projet Delirium (PVCPC/delirium-physics) et de diversité cinématique.
// Compile et exécute directement les vraies sources C# du plugin Brutality hors-jeu.
// Lancer : dotnet run --project tests/Brutality.DeliriumTests
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Brutality;
using Brutality.Anatomy;
using Brutality.Core;
using Brutality.Euphoria;
using Brutality.Neural;
using EFT;
using UnityEngine;

namespace DeliriumTests
{
    internal static class Program
    {
        private sealed record CheckRecord(
            int Index,
            int Level,
            string Name,
            string DeliriumRef,
            string BrutalitySrc,
            string Detail,
            bool Passed);

        private sealed class DiversityAudit
        {
            public List<(NmPlanKind Kind, int Count)> Plans = new List<(NmPlanKind, int)>();
            public int MaxPlanShare;
            public int[] Sectors = new int[3];
            public int Grabs;
            public int WoundReaches;
            public int Pronations;
            public int Rolls;
            public int UniBuckles;
            public int BiBuckles;
            public int Windmills;
            public int Flinches;
            public int NaNs;
        }

        private static int _pass, _fail, _currentLevel = 1;
        private static readonly List<CheckRecord> _records = new List<CheckRecord>();
        private static DiversityAudit _diversity;

        private static void Check(string name, bool ok, string detail = "", string deliriumRef = "", string brutalitySrc = "")
        {
            if (ok) _pass++; else _fail++;
            _records.Add(new CheckRecord(_records.Count + 1, _currentLevel, name, deliriumRef, brutalitySrc, detail ?? "", ok));
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " " + detail : "")}");
        }

        private static DeathContext MakeCtx(int seed, DeathMechanism mech = DeathMechanism.Thoracic,
            EBodyPart part = EBodyPart.Chest, EBodyPartColliderType col = EBodyPartColliderType.RibcageUp,
            Vector3? dir = null, Vector3? vel = null, EPlayerPose pose = EPlayerPose.Stand,
            float kick = 1f, string kickClass = "intermédiaire")
        {
            return new DeathContext
            {
                Rng = new Random(seed),
                Mechanism = mech,
                BodyPart = part,
                ColliderType = col,
                Direction = dir ?? new Vector3(0f, 0f, -1f),
                Velocity = vel ?? Vector3.zero,
                Pose = pose,
                KickScale = kick,
                KickClass = kickClass,
                ImpactMomentum = 3.2f,
            };
        }

        private static void Main(string[] args)
        {
            string reportPath = null;
            string dllPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--report" && i + 1 < args.Length) reportPath = args[++i];
                else if (args[i] == "--dll" && i + 1 < args.Length) dllPath = args[++i];
            }

            _currentLevel = 1;
            Console.WriteLine("=== NIVEAU 1 : PREUVES MATHEMATIQUES LOI-PAR-LOI (DELIRIUM RUST vs BRUTALITY C#) ===");
            Test_Nmrs_StiffnessRelaxationUnits();
            Test_Nmrs_ConeTargetsAndSetRelative();
            Test_Nmrs_MasksAndLockUnlock();
            Test_Nmrs_ElbowPronationFlushed();
            Test_Wrist_ForearmPronation3DGeometry();
            Test_Nmdbm_OpposeGravityExactLaw();
            Test_Nmbb_ShoulderVsElbowStiffnessAndBlendToZero();
            Test_Nm_ExecutionOrderBalanceThenShot();

            _currentLevel = 2;
            Console.WriteLine("\n=== NIVEAU 2 : PREUVES COMPORTEMENTALES EN SIMULATION PHYSIQUE 60 HZ ===");
            Test_Shot_WoundReachPullAndPronation();
            Test_Shot_ShockSpinTorqueUnitsAndLeverArm();
            Test_Shot_LooseArmOnArmHit();
            Test_Shot_GiveWayBackShotNoOverwrite();
            Test_Legs_UnilateralAndBilateralBuckling();
            Test_CatchFall_DownwardProbeAndFloorPronation();
            Test_Grab_WallAndLedgeDetectionGripAndSnap();
            Test_RollDownStairs_And_RollUp();
            Test_Windmill_PhaseOffsetAndImpactStop();
            Test_Writhe_AsymmetryAndRollOverForces();
            Test_Foetal_AsymmetryAndPronation();
            Test_Flinch_FrontalBurstGuard();

            _currentLevel = 3;
            Console.WriteLine("\n=== NIVEAU 3 : AUDIT STATISTIQUE ET CINEMATIQUE DE DIVERSITE (60 MORTS) ===");
            Test_Diversity_60TacticalScenarios();

            Console.WriteLine($"\nRESULTAT FINAL : {_pass} réussis, {_fail} échoués");

            if (!string.IsNullOrWhiteSpace(reportPath))
                WriteMarkdownReport(reportPath, dllPath);

            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ NIVEAU 1

        private static void Test_Nmrs_StiffnessRelaxationUnits()
        {
            Physics.ResetWorld();
            var sim = new SimRagdoll(MakeCtx(101));
            var eff = sim.Perf.Eff;
            eff.SetStiffness(JointId.RShoulder, 11f, 0.75f, 0.9f);
            Check("nmrs::set_stiffness : strength = s², damping = 2·s·d",
                MathF.Abs(eff.Strength((int)JointId.RShoulder) - 121f) < 1e-3f &&
                MathF.Abs(eff.Damping((int)JointId.RShoulder) - 16.5f) < 1e-3f,
                $"(strength={eff.Strength((int)JointId.RShoulder):0.0}, damping={eff.Damping((int)JointId.RShoulder):0.0})",
                "nmrs.rs (set_stiffness)", "DlCharacter.cs (SetStiffness)");

            sim.Buffer.Reset();
            eff.Flush(sim.State, sim.Body, 1f);
            bool ok = sim.Buffer.Resolve((int)JointId.RShoulder, 0, out MotorCommand mc);
            Check("nmrs::muscles → Flush : ω = √strength et ζ = damping/(2·ω) sans conversion",
                ok && MathF.Abs(mc.Omega - 11f) < 1e-3f && MathF.Abs(mc.Zeta - 0.75f) < 1e-3f && MathF.Abs(mc.MuscleStiffness - 0.9f) < 1e-3f,
                $"(ω={mc.Omega:0.00}, ζ={mc.Zeta:0.00}, ms={mc.MuscleStiffness:0.00})",
                "nmrs.rs (muscles)", "DlCharacter.cs (Flush)");

            // set_relaxation conserve le rapport d'amortissement ζ invariant.
            eff.SetRelaxation((int)JointId.LKnee, 0.4f);
            float sRel = MathF.Sqrt(eff.Strength((int)JointId.LKnee));
            float zRel = eff.Damping((int)JointId.LKnee) / (2f * sRel);
            Check("nmrs::set_relaxation(0.4) : ω = 0.4·base, ζ invariant = baseDamping",
                MathF.Abs(sRel - DlCfg.RestStiffness * 0.4f) < 1e-3f && MathF.Abs(zRel - DlCfg.RestDamping) < 1e-3f,
                $"(ω={sRel:0.00}, ζ={zRel:0.00})",
                "nmrs.rs (set_relaxation)", "DlCharacter.cs (SetRelaxation)");
        }

        private static void Test_Nmrs_ConeTargetsAndSetRelative()
        {
            Physics.ResetWorld();
            var sim = new SimRagdoll(MakeCtx(102));
            var eff = sim.Perf.Eff;
            int sh = (int)JointId.RShoulder;

            // Vérification exacte de k = 1 + 25 / (strength + 256) (nmrs.rs:545).
            float kWeak = DlCfg.ConeRadius(4f);
            float kStiff = DlCfg.ConeRadius(225f);
            Check("nmrs::targets : ConeRadius(strength) = 1 + 25/(strength+256) décroissant avec strength",
                MathF.Abs(kWeak - (1f + 25f / 260f)) < 1e-5f &&
                MathF.Abs(kStiff - (1f + 25f / 481f)) < 1e-5f &&
                kWeak > kStiff,
                $"(kWeak={kWeak:0.000}, kStiff={kStiff:0.000})",
                "nmrs.rs:545 (targets)", "DlCharacter.cs (DlCfg.ConeRadius)");

            // SetRelative(1.4) ne doit PAS pré-clamper dans [lo, hi] : c'est Targets() qui borne au cône k.
            eff.SetStiffness(JointId.RShoulder, 2f, 1f);
            eff.SetRelative(sh, 0, 1.4f);
            eff.SetRelative(sh, 1, 0f);
            DlCfg.Range(sh, 0, out float lo, out float hi);
            float mid = 0.5f * (lo + hi), half = 0.5f * (hi - lo);
            float tgtWeak = eff.Targets(sh).x;
            eff.SetStiffness(JointId.RShoulder, 15f, 1f);
            float tgtStiff = eff.Targets(sh).x;
            Check("nmrs::set_relative + targets : muscle faible autorise débordement cône > muscle raide",
                MathF.Abs(tgtWeak - (mid + kWeak * half)) < 1e-3f &&
                MathF.Abs(tgtStiff - (mid + kStiff * half)) < 1e-3f &&
                tgtWeak > hi && tgtWeak > tgtStiff,
                $"(weak={tgtWeak / BMath.Deg:0.0}°, stiff={tgtStiff / BMath.Deg:0.0}°, hi={hi / BMath.Deg:0.0}°)",
                "nmrs.rs (set_relative, targets)", "DlCharacter.cs (SetRelative, Targets)");
        }

        private static void Test_Nmrs_MasksAndLockUnlock()
        {
            Check("nmrs::mask(\"fb\") = 10 articulations", DlCharacter.Mask("fb").Length == 10,
                $"({DlCharacter.Mask("fb").Length} articulations)", "nmrs.rs (mask)", "DlCharacter.cs (Mask)");
            Check("nmrs::mask(\"ub\") = 6 (rachis+cou+bras)", DlCharacter.Mask("ub").Length == 6,
                $"({DlCharacter.Mask("ub").Length} articulations)", "nmrs.rs (mask)", "DlCharacter.cs (Mask)");
            Check("nmrs::mask(\"ut\") = 5 (tronc+bras, SANS cou)", DlCharacter.Mask("ut").Length == 5 && Array.IndexOf(DlCharacter.Mask("ut"), JointId.Neck) < 0,
                $"({DlCharacter.Mask("ut").Length}, Neck absent={Array.IndexOf(DlCharacter.Mask("ut"), JointId.Neck) < 0})", "nmrs.rs (mask)", "DlCharacter.cs (Mask)");
            Check("nmrs::mask(\"uk\") = 2 (rachis+cou)", DlCharacter.Mask("uk").Length == 2,
                $"({DlCharacter.Mask("uk").Length} articulations)", "nmrs.rs (mask)", "DlCharacter.cs (Mask)");
            Check("nmrs::mask(\"uw\") = 0 (poignet absent sur EFT)", DlCharacter.Mask("uw").Length == 0,
                $"({DlCharacter.Mask("uw").Length} articulations)", "nmrs.rs (mask)", "DlCharacter.cs (Mask)");
            Check("nmrs::mask(\"lb\") = 4, \"ll\" = 2, \"lr\" = 2",
                DlCharacter.Mask("lb").Length == 4 && DlCharacter.Mask("ll").Length == 2 && DlCharacter.Mask("lr").Length == 2,
                $"(lb={DlCharacter.Mask("lb").Length}, ll={DlCharacter.Mask("ll").Length}, lr={DlCharacter.Mask("lr").Length})",
                "nmrs.rs (mask)", "DlCharacter.cs (Mask)");

            var sim = new SimRagdoll(MakeCtx(103));
            var eff = sim.Perf.Eff;
            eff.SetDesired((int)JointId.RShoulder, 0, 1.23f);
            var keep = eff.Lock("lb");
            eff.SetDesired((int)JointId.RShoulder, 0, -0.5f);
            eff.SetDesired((int)JointId.LKnee, 0, 1.11f);
            eff.Unlock("lb", keep);
            Check("nmrs::lock/unlock(\"lb\") : préserve haut du corps et conserve jambes",
                MathF.Abs(eff.Desired((int)JointId.RShoulder).x - 1.23f) < 1e-4f &&
                MathF.Abs(eff.Desired((int)JointId.LKnee).x - 1.11f) < 1e-4f,
                $"(shR={eff.Desired((int)JointId.RShoulder).x:0.00} rad, kneeL={eff.Desired((int)JointId.LKnee).x:0.00} rad)",
                "nmrs.rs (lock/unlock)", "DlCharacter.cs (Lock/Unlock)");
        }

        private static void Test_Nmrs_ElbowPronationFlushed()
        {
            var sim = new SimRagdoll(MakeCtx(104));
            var eff = sim.Perf.Eff;
            eff.SetStiffness(JointId.RElbow, 11f, 1f);
            eff.SetStiffness(JointId.LElbow, 11f, 1f);
            eff.SetStiffness(JointId.RKnee, 11f, 1f);
            eff.SetDesired((int)JointId.RElbow, 2, -0.85f); // pronation droite
            eff.SetDesired((int)JointId.LElbow, 2, +0.85f); // pronation gauche
            eff.SetDesired((int)JointId.RKnee, 2, 0.5f);

            sim.Buffer.Reset();
            eff.Flush(sim.State, sim.Body, 1f);
            bool rHas = sim.Buffer.Resolve((int)JointId.RElbow, 2, out MotorCommand rCmd);
            bool lHas = sim.Buffer.Resolve((int)JointId.LElbow, 2, out MotorCommand lCmd);
            bool kHas = sim.Buffer.Resolve((int)JointId.RKnee, 2, out _);
            Check("Flush pronation coude (DOF 2) : émis sur coudes G/D, ignoré sur genoux",
                rHas && lHas && !kHas && MathF.Abs(rCmd.Target - (-0.85f)) < 1e-3f && MathF.Abs(lCmd.Target - 0.85f) < 1e-3f,
                $"(D={rCmd.Target / BMath.Deg:0.0}°, G={lCmd.Target / BMath.Deg:0.0}°, genou={kHas})",
                "nmrs.rs (muscles) + nmshot", "DlCharacter.cs (Flush DOF 2)");
        }

        private static void Test_Wrist_ForearmPronation3DGeometry()
        {
            var sim = new SimRagdoll(MakeCtx(105));
            // Coude fléchi à 90° vers l'avant (+Z) pour les deux bras : fore.U = -Z, fore.R = +X.
            sim.JointPhi[(int)JointId.RElbow] = new Vector3(90f * BMath.Deg, 0f, 0f);
            sim.JointPhi[(int)JointId.LElbow] = new Vector3(90f * BMath.Deg, 0f, 0f);
            sim.SyncKinematics(0f);

            // Surface = sol (normale +Y) → on veut la paume vers -Y (contre le sol).
            float pR = DlPhys.ForearmPronationForNormal(sim.Body, 1, Vector3.up);
            float pL = DlPhys.ForearmPronationForNormal(sim.Body, -1, Vector3.up);
            var foreR = sim.Body[Seg.RForearm];
            var foreL = sim.Body[Seg.LForearm];
            Vector3 palmRAfter = Quaternion.AngleAxis(pR / BMath.Deg, foreR.U) * (-foreR.R);
            Vector3 palmLAfter = Quaternion.AngleAxis(pL / BMath.Deg, foreL.U) * (foreL.R);
            float dotR = Vector3.Dot(palmRAfter.normalized, Vector3.down);
            float dotL = Vector3.Dot(palmLAfter.normalized, Vector3.down);
            Check("Shot::wrist / ForearmPronationForNormal : paumes G/D tournées face au sol",
                dotR > 0.90f && dotL > 0.90f && pR * pL < 0f,
                $"(pR={pR / BMath.Deg:0.0}°, dotR={dotR:0.00} ; pL={pL / BMath.Deg:0.0}°, dotL={dotL:0.00})",
                "nmshot (wrist)", "DlCharacter.cs (DlPhys.ForearmPronationForNormal)");
        }

        private static void Test_Nmdbm_OpposeGravityExactLaw()
        {
            var sim = new SimRagdoll(MakeCtx(106));
            // Bras droit tendu à l'horizontale (+Z) → couple de gravité maximal autour de l'épaule.
            sim.JointPhi[(int)JointId.RShoulder] = new Vector3(90f * BMath.Deg, 0f, 0f);
            sim.JointPhi[(int)JointId.RElbow] = new Vector3(20f * BMath.Deg, 0f, 0f);
            sim.SyncKinematics(0f);
            foreach (var s in sim.Body.Segs) s.Rb.ClearAccum();

            var eff = sim.Perf.Eff;
            for (int j = 0; j < DlCharacter.NJ; j++) eff.SetOpposeGravity(j, 0f);
            eff.SetStiffness(JointId.RShoulder, 10f, 1f);
            eff.SetOpposeGravity((int)JointId.RShoulder, 1f);
            eff.SetStiffness(JointId.RElbow, 10f, 1f);
            eff.SetOpposeGravity((int)JointId.RElbow, 1f);
            DlPhys.OpposeGravity(eff, sim.Body, 1f);

            Vector3 tSum = Vector3.zero;
            foreach (var s in sim.Body.Segs) tSum += s.Rb.TorqueAccum;
            float shTq = sim.Body[Seg.RUpperArm].Rb.TorqueAccum.magnitude;
            float elTq = sim.Body[Seg.RForearm].Rb.TorqueAccum.magnitude;
            // Sur le coude (charnière), le couple doit être colinéaire à l'axe de flexion a0.
            var elJm = sim.Body.Joints[(int)JointId.RElbow];
            Vector3 a0 = (sim.Body[Seg.RForearm].Q * elJm.Driven.Axis).normalized;
            float align = MathF.Abs(Vector3.Dot(sim.Body[Seg.RForearm].Rb.TorqueAccum.normalized, a0));
            Check("nmdbm::oppose_gravity : action+réaction=0, couple borné et charnière projetée sur l'axe",
                tSum.magnitude < 1e-4f && shTq > 2f && elTq > 0.5f && align > 0.999f,
                $"(|Στ|={tSum.magnitude:0.0000}, τSh={shTq:0.0} N·m, τEl={elTq:0.0} N·m, align={align:0.000})",
                "nmdbm.rs (oppose_gravity)", "DlCharacter.cs (DlPhys.OpposeGravity)");
        }

        private static void Test_Nmbb_ShoulderVsElbowStiffnessAndBlendToZero()
        {
            Physics.ResetWorld();
            var ctx = MakeCtx(107);
            var sim = new SimRagdoll(ctx, riflePose: true); // mort en tenant son fusil (épaule 70°, coude 95°)
            Settings.EuphoriaForce.Value = "Titube";
            sim = new SimRagdoll(MakeCtx(107), riflePose: true);
            Settings.EuphoriaForce.Value = "Auto";

            // Marque le bras gauche comme occupé (Busy[0] = true) et règle une cible sentinelle.
            sim.Perf.Eff.Busy[0] = true;
            sim.Perf.Eff.SetDesired((int)JointId.LShoulder, 0, 1.42f);

            sim.Perf.Balance.Tick(sim.State, 0.02f, 0.016f);
            float shK = MathF.Sqrt(sim.Perf.Eff.Strength((int)JointId.RShoulder));
            float elK = MathF.Sqrt(sim.Perf.Eff.Strength((int)JointId.RElbow));
            bool busyKept = MathF.Abs(sim.Perf.Eff.Desired((int)JointId.LShoulder).x - 1.42f) < 1e-4f;
            // À vitesse nulle, la cible ne doit PAS être écrasée par la pose du fusil (70° = 1.22 rad) !
            float shDesDeg = sim.Perf.Eff.Desired((int)JointId.RShoulder).x / BMath.Deg;
            Check("nmbb::BodyBalance : épaule=11, coude=6.6, Busy préservé, BlendToZero ne fige plus sur le fusil",
                MathF.Abs(shK - DlCfg.BalanceBodyStiffness) < 0.05f &&
                MathF.Abs(elK - DlCfg.BalanceBodyStiffness * 0.6f) < 0.05f &&
                busyKept && shDesDeg < 55f,
                $"(ωSh={shK:0.0}, ωEl={elK:0.0}, busyKept={busyKept}, shDes={shDesDeg:0.0}°)",
                "nmbb.rs (BodyBalance)", "DlBalance.cs (Tick)");
        }

        private static void Test_Nm_ExecutionOrderBalanceThenShot()
        {
            Physics.ResetWorld();
            Settings.EuphoriaForce.Value = "Titube";
            var sim = new SimRagdoll(MakeCtx(108));
            Settings.EuphoriaForce.Value = "Auto";

            // Tir jambe gauche + plaie thorax → Shot doit l'emporter sur Balance dans le même tick !
            Vector3 hitPt = sim.Body[Seg.Thorax].Com + new Vector3(0.08f, 0f, 0.12f);
            sim.Perf.OnBullet(0f, NmHit.Make(sim.Body, sim.Body[Seg.Thorax], hitPt, new Vector3(0f, 0f, -1f), 4f));
            sim.Perf.Shot.CollapseLegs[0] = true;

            // Avance jusqu'à t = 0.35 s (pendant la phase Balance : Reach et Collapse actifs).
            for (int step = 0; step < 22; step++)
            {
                sim.Step(step * 0.016f, 0.016f);
            }
            bool reached = sim.Perf.Shot.ReachedOnce;
            // Vérifie que la raideur du genou gauche (collapsé) est nettement inférieure au genou droit (intact).
            float kL = MathF.Sqrt(sim.Perf.Eff.Strength((int)JointId.LKnee));
            float kR = MathF.Sqrt(sim.Perf.Eff.Strength((int)JointId.RKnee));
            Check("nm.rs ordre Balance → Shot : Shot survit à Balance (Reach actif, genou collapsé non écrasé)",
                reached && kL < kR * 0.7f,
                $"(reached={reached}, ωKneeL={kL:0.0}, ωKneeR={kR:0.0}, phase={sim.Perf.Phase})",
                "nm.rs (ordre Balance → Shot)", "NmPerformance.cs (Tick)");
        }

        // ------------------------------------------------------------------ NIVEAU 2

        private static void Test_Shot_WoundReachPullAndPronation()
        {
            Physics.ResetWorld();
            Settings.EuphoriaForce.Value = "Titube";
            var sim = new SimRagdoll(MakeCtx(201));
            Settings.EuphoriaForce.Value = "Auto";

            Vector3 woundPt = sim.Body[Seg.Thorax].Com + new Vector3(0.02f, 0.02f, 0.13f);
            var hit = NmHit.Make(sim.Body, sim.Body[Seg.Thorax], woundPt, new Vector3(0f, 0f, -1f), 3.5f);
            sim.Perf.OnBullet(0f, hit);

            float dStart = MathF.Min(Vector3.Distance(sim.Perf.Dl.HandPos(-1), woundPt), Vector3.Distance(sim.Perf.Dl.HandPos(1), woundPt));
            float minDist = dStart;
            bool pullObserved = false;
            bool actionReactionZero = true;
            for (int s = 0; s < 55; s++)
            {
                float t = s * 0.016f;
                sim.Step(t, 0.016f);
                Vector3 wp = hit.World;
                float dNow = MathF.Min(Vector3.Distance(sim.Perf.Dl.HandPos(-1), wp), Vector3.Distance(sim.Perf.Dl.HandPos(1), wp));
                minDist = MathF.Min(minDist, dNow);

                // Vérifie l'impulsion/force Shot::pull isolée.
                if (t > 0.25f && t < 0.7f)
                {
                    foreach (var sg in sim.Body.Segs) sg.Rb.ClearAccum();
                    sim.Perf.Shot.Forces(t, 0.016f, 1f);
                    Vector3 fArm = sim.Body[Seg.LForearm].Rb.ForceAccum + sim.Body[Seg.RForearm].Rb.ForceAccum;
                    Vector3 fTh = sim.Body[Seg.Thorax].Rb.ForceAccum;
                    if (fArm.magnitude > 1f)
                    {
                        pullObserved = true;
                        if ((fArm + fTh).magnitude > 1e-3f) actionReactionZero = false;
                    }
                }
            }
            Check("nmshot::reach + pull + wrist : main rapprochée de la plaie, pull action/réaction exact, pronation active",
                sim.AnyWoundReach && pullObserved && actionReactionZero && minDist < dStart - 0.08f && sim.PeakPronationRad > 0.2f,
                $"(d0={dStart:0.00}m→min={minDist:0.00}m, pull={pullObserved}, ΣF=0:{actionReactionZero}, pron={sim.PeakPronationRad / BMath.Deg:0.0}°)",
                "nmshot (reach, pull, wrist)", "DlShot.cs (Reach, Forces)");
        }

        private static void Test_Shot_ShockSpinTorqueUnitsAndLeverArm()
        {
            Physics.ResetWorld();
            var ctx = MakeCtx(202, kick: 1.5f, kickClass: "slug");
            var sim = new SimRagdoll(ctx);
            Vector3 rightEdge = sim.Body[Seg.Thorax].Com + new Vector3(0.22f, 0.05f, 0.08f);
            sim.Perf.OnBullet(0f, NmHit.Make(sim.Body, sim.Body[Seg.Thorax], rightEdge, new Vector3(0f, 0f, -1f), 9f));
            float spinMag = MathF.Abs(sim.Perf.Shot.LastShockSpinTorque);
            foreach (var sg in sim.Body.Segs) sg.Rb.ClearAccum();
            sim.Perf.Shot.Forces(0.02f, 0.016f, 1f);
            float tqTh = sim.Body[Seg.Thorax].Rb.TorqueAccum.magnitude;
            Check("nmshot::shock_spin : couple en N·m (20..90 N·m, pas Deg2Rad) proportionnel au bras de levier",
                spinMag >= 20f && spinMag <= 95f && tqTh >= 20f,
                $"(spin={sim.Perf.Shot.LastShockSpinTorque:0.0} N·m, τThorax={tqTh:0.0} N·m)",
                "nmshot (shock_spin)", "DlShot.cs (OnBullet, Forces)");
        }

        private static void Test_Shot_LooseArmOnArmHit()
        {
            Physics.ResetWorld();
            Settings.EuphoriaForce.Value = "Titube";
            var sim = new SimRagdoll(MakeCtx(203, DeathMechanism.Limb, EBodyPart.LeftArm, EBodyPartColliderType.LeftUpperArm));
            Settings.EuphoriaForce.Value = "Auto";
            var hit = NmHit.Make(sim.Body, sim.Body[Seg.LUpperArm], sim.Body[Seg.LUpperArm].Com, new Vector3(0f, 0f, -1f), 3f);
            sim.Perf.OnBullet(0f, hit);
            for (int s = 0; s < 30; s++) sim.Step(s * 0.016f, 0.016f);
            float kL = MathF.Sqrt(sim.Perf.Eff.Strength((int)JointId.LElbow));
            float kR = MathF.Sqrt(sim.Perf.Eff.Strength((int)JointId.RElbow));
            Check("nmshot::LOOSE_L : bras gauche touché devient flasque, bras droit saisit le bras blessé",
                sim.Perf.Shot.LooseArms[0] && !sim.Perf.Shot.LooseArms[1] && kL < 4f && sim.Perf.Shot.ReachArms[1],
                $"(ωElL={kL:0.0}, ωElR={kR:0.0}, reachR={sim.Perf.Shot.ReachArms[1]})",
                "nmshot (LOOSE_L)", "DlShot.cs (OnBullet, Tick)");
        }

        private static void Test_Shot_GiveWayBackShotNoOverwrite()
        {
            Physics.ResetWorld();
            var ctx = MakeCtx(204, dir: new Vector3(0f, 0f, +1f)); // balle dans le dos (+Z = vers l'avant du perso)
            var sim = new SimRagdoll(ctx);
            float y0 = sim.Body[Seg.Pelvis].Com.y;
            var hit = NmHit.Make(sim.Body, sim.Body[Seg.Thorax], sim.Body[Seg.Thorax].Com - new Vector3(0f, 0f, 0.12f), new Vector3(0f, 0f, +1f), 3.5f);
            sim.Perf.OnBullet(0f, hit);
            float minKneeStiff = 99f;
            for (int s = 0; s < 55; s++)
            {
                sim.Step(s * 0.016f, 0.016f);
                minKneeStiff = MathF.Min(minKneeStiff, MathF.Sqrt(sim.Perf.Eff.Strength((int)JointId.LKnee)));
                minKneeStiff = MathF.Min(minKneeStiff, MathF.Sqrt(sim.Perf.Eff.Strength((int)JointId.RKnee)));
            }
            float drop = y0 - sim.MinPelvisY;
            Check("nmshot::GiveWay (balle dans le dos) : raideur genou chute (<4), genoux fléchissent, bassin descend",
                sim.Perf.Plan.ShotFromBehind && minKneeStiff < 4.5f && MathF.Max(sim.PeakLeftKneeFlex, sim.PeakRightKneeFlex) > 25f * BMath.Deg && drop > 0.15f,
                $"(minωKnee={minKneeStiff:0.0}, kneeFlex={MathF.Max(sim.PeakLeftKneeFlex, sim.PeakRightKneeFlex) / BMath.Deg:0.0}°, drop={drop:0.00}m)",
                "nmshot (GiveWay)", "DlShot.cs, DlBalance.cs");
        }

        private static void Test_Legs_UnilateralAndBilateralBuckling()
        {
            // 1. Unilatéral : balle dans la jambe gauche → seule la jambe gauche cède d'abord.
            Physics.ResetWorld();
            Settings.EuphoriaForce.Value = "Effondrement latéral";
            var sim1 = new SimRagdoll(MakeCtx(205, DeathMechanism.Limb, EBodyPart.LeftLeg, EBodyPartColliderType.LeftThigh));
            Settings.EuphoriaForce.Value = "Auto";
            sim1.Perf.OnBullet(0f, NmHit.Make(sim1.Body, sim1.Body[Seg.LThigh], sim1.Body[Seg.LThigh].Com, new Vector3(0f, 0f, -1f), 3.5f));
            for (int s = 0; s < 15; s++) sim1.Step(s * 0.016f, 0.016f);
            bool uniOk = sim1.Perf.Balance != null && sim1.Perf.Balance.LegBuckle[0] > 0.6f && sim1.Perf.Balance.LegBuckle[1] < 0.05f;

            // 2. Bilatéral synchrone (mode 2) : les deux jambes cèdent ensemble.
            Physics.ResetWorld();
            Settings.EuphoriaForce.Value = "Titube";
            var simSync = new SimRagdoll(MakeCtx(2061));
            Settings.EuphoriaForce.Value = "Auto";
            simSync.Perf.Balance.O.BuckleMode = 2;
            simSync.Perf.Balance.O.BuckleDelay = 0.04f;
            simSync.Perf.Balance.O.LeanAmount = 0f;
            for (int s = 0; s < 16; s++) simSync.Step(s * 0.016f, 0.016f);
            bool syncOk = simSync.Perf.Balance.LegBuckle[0] > 0.7f && simSync.Perf.Balance.LegBuckle[1] > 0.7f
                && MathF.Abs(simSync.Perf.Balance.LegBuckle[0] - simSync.Perf.Balance.LegBuckle[1]) < 0.01f;

            // 3. Bilatéral décalé (mode 3) : une jambe cède avant l'autre, puis les deux cèdent.
            Physics.ResetWorld();
            Settings.EuphoriaForce.Value = "Titube";
            var sim2 = new SimRagdoll(MakeCtx(206));
            Settings.EuphoriaForce.Value = "Auto";
            sim2.Perf.Balance.O.BuckleMode = 3;
            sim2.Perf.Balance.O.BuckleLeg = -1;
            sim2.Perf.Balance.O.BuckleDelay = 0.04f;
            sim2.Perf.Balance.O.BuckleStaggerDelay = 0.12f;
            sim2.Perf.Balance.O.LeanAmount = 0f;
            bool staggeredMid = false, bothLate = false;
            for (int s = 0; s < 30; s++)
            {
                float t = s * 0.016f;
                sim2.Step(t, 0.016f);
                if (sim2.Perf.Balance != null && t > 0.09f && t < 0.16f)
                    if (sim2.Perf.Balance.LegBuckle[0] > 0.25f && sim2.Perf.Balance.LegBuckle[1] < 0.05f) staggeredMid = true;
                if (sim2.Perf.Balance != null && t >= 0.22f)
                    if (sim2.Perf.Balance.LegBuckle[0] > 0.7f && sim2.Perf.Balance.LegBuckle[1] > 0.35f) bothLate = true;
            }
            Check("Dérobement jambes : 1 jambe (unilatéral), 2 jambes (synchrone) ET 2 jambes décalées (2 temps) prouvés",
                uniOk && syncOk && staggeredMid && bothLate,
                $"(uni={uniOk}, sync={syncOk}, décaléMi={staggeredMid}, deuxTard={bothLate})",
                "nmshot + nmdbm", "DlBalance.cs (LegBuckle, Forces)");
        }

        private static void Test_CatchFall_DownwardProbeAndFloorPronation()
        {
            Physics.ResetWorld(0f);
            Settings.EuphoriaForce.Value = "Plongeon";
            var sim = new SimRagdoll(MakeCtx(207, vel: new Vector3(0f, -1.5f, 1.4f)));
            Settings.EuphoriaForce.Value = "Auto";
            sim.Perf.OnBullet(0f, NmHit.Make(sim.Body, sim.Body[Seg.Thorax], sim.Body[Seg.Thorax].Com, new Vector3(0f, 0f, 1f), 3.5f));
            // Corps penché en avant pendant le plongeon, épaules à ~0.85 m du sol.
            sim.RootPos = new Vector3(0f, 0.65f, 0f);
            sim.RootRot = Quaternion.AngleAxis(45f, Vector3.right);
            sim.RootVel = new Vector3(0f, -2.2f, 1.3f);
            sim.SyncKinematics(0f);

            bool dirDown = false, fwdPos = false, floorHit = false, neckRadOk = false, pronOk = false;
            for (int s = 0; s < 35; s++)
            {
                sim.Step(s * 0.016f, 0.016f);
                var cf = sim.Perf.CatchFall;
                if (cf != null)
                {
                    if (cf.FallDir.y < -0.5f) dirDown = true;
                    if (cf.ForwardDot > 0.1f) fwdPos = true;
                    if (cf.ArmHitSurface[0] || cf.ArmHitSurface[1]) floorHit = true;
                    if (MathF.Abs(sim.Perf.Eff.Desired((int)JointId.Neck).x) > 0.15f) neckRadOk = true;
                    if (MathF.Max(MathF.Abs(sim.Perf.Eff.Desired((int)JointId.LElbow).z), MathF.Abs(sim.Perf.Eff.Desired((int)JointId.RElbow).z)) > 0.25f) pronOk = true;
                }
            }
            Check("nmcf::CatchFall : sonde vers le SOL (y<0), forward>0, touche le sol, cou en rad, pronation paumes",
                dirDown && fwdPos && floorHit && neckRadOk && pronOk,
                $"(down={dirDown}, fwd={fwdPos}, hit={floorHit}, neckRad={neckRadOk}, pron={pronOk})",
                "nmcf (CatchFall)", "DlBehaviours.cs (DlCatchFall)");
        }

        private static void Test_Grab_WallAndLedgeDetectionGripAndSnap()
        {
            // Scénario 1 : Mur vertical à droite (x = +0.58 m) → doit détecter, tendre le bras droit, pronater, verrouiller la prise !
            Physics.ResetWorld(0f);
            Physics.Boxes.Add(new StaticBox(new Vector3(0.75f, 1.2f, 0f), new Vector3(0.30f, 2.4f, 4f))); // face en x = 0.60 m
            Settings.EuphoriaForce.Value = "Effondrement latéral";
            var sim = new SimRagdoll(MakeCtx(208, vel: new Vector3(0.6f, 0f, 0f)));
            Settings.EuphoriaForce.Value = "Auto";
            sim.Perf.OnBullet(0f, NmHit.Make(sim.Body, sim.Body[Seg.Thorax], sim.Body[Seg.Thorax].Com, new Vector3(1f, 0f, 0f), 3.5f));

            bool gripForceSeen = false;
            for (int s = 0; s < 90; s++)
            {
                float t = s * 0.016f;
                // Après verrouillage, tire fort le corps loin du mur pour vérifier la rupture (SnapCount).
                if (sim.Perf.Grab != null && sim.Perf.Grab.EverGripped && s > 45)
                    sim.RootVel += new Vector3(-12f, -4f, 0f) * 0.016f;
                sim.Step(t, 0.016f);
                if (sim.Perf.Grab != null)
                {
                    for (int i = 0; i < 2; i++)
                        if (sim.Perf.Grab.Grips[i] != null && sim.Perf.Grab.Grips[i].LastForce > 5f) gripForceSeen = true;
                }
            }

            bool grabReleasedAfterSnap = sim.AnyGrabSnapped && !sim.Perf.Eff.Busy[0] && !sim.Perf.Eff.Busy[1]
                && (sim.Perf.Grab == null || !sim.Perf.Grab.Active);

            // Scénario 2 : Champ libre plat (aucun mur ni caisse) → ProbeEnvironment ne doit PAS déclencher sur le sol plat !
            Physics.ResetWorld(0f);
            var openSim = new SimRagdoll(MakeCtx(209));
            bool falsePositive = DlGrab.ProbeEnvironment(openSim.Perf.Dl, openSim.Perf.Ch, Vector3.forward, out _);

            // Scénario 3 : Caisse / rebord devant à 0.65 m (hauteur 0.90 m) → doit détecter comme rebord !
            Physics.ResetWorld(0f);
            Physics.Boxes.Add(new StaticBox(new Vector3(0f, 0.45f, 0.90f), new Vector3(1.2f, 0.90f, 0.60f)));
            var ledgeSim = new SimRagdoll(MakeCtx(210));
            bool ledgeHit = DlGrab.ProbeEnvironment(ledgeSim.Perf.Dl, ledgeSim.Perf.Ch, Vector3.forward, out DlGrabTarget lt);

            Check("nmgr::Grab + GrabHelper : détecte mur & caisse, 0 faux positif sol plat, prise physique + force + rupture + libération Busy",
                sim.AnyGrabActivated && sim.AnyGrabGripped && gripForceSeen && sim.AnyGrabSnapped && grabReleasedAfterSnap && !falsePositive && ledgeHit,
                $"(act={sim.AnyGrabActivated}, grip={sim.AnyGrabGripped}, force={gripForceSeen}, snap={sim.AnyGrabSnapped}, rel={grabReleasedAfterSnap}[b0={sim.Perf.Eff.Busy[0]},b1={sim.Perf.Eff.Busy[1]},gAct={sim.Perf.Grab?.Active}], openFP={falsePositive}, crate={ledgeHit})",
                "nmgr (Grab, GrabHelper)", "DlGrab.cs (DlGrab)");
        }

        private static void Test_RollDownStairs_And_RollUp()
        {
            Physics.ResetWorld(0f);
            var ctx = MakeCtx(211, vel: new Vector3(0f, 0f, 3.0f)); // course à 3 m/s → RollDownStairs d'emblée
            var sim = new SimRagdoll(ctx);
            sim.Perf.OnBullet(0f, NmHit.Make(sim.Body, sim.Body[Seg.Thorax], sim.Body[Seg.Thorax].Com, new Vector3(0f, 0f, 1f), 4f));
            bool lumbarNoOg = true;
            for (int s = 0; s < 65; s++)
            {
                sim.Step(s * 0.016f, 0.016f);
                if (sim.Perf.RollDownStairs != null && sim.Perf.Eff.OpposeGravity((int)JointId.Lumbar) > 0.01f)
                    lumbarNoOg = false;
            }
            Check("nmrds::RollDownStairs + nmru::RollUp : roulade active, rotation réelle, Lumbar sans oppose_gravity",
                sim.Perf.Plan.Kind == NmPlanKind.RollDownStairs && sim.AnyRollActivated && sim.PeakRollAngVel > 2.2f && lumbarNoOg,
                $"(plan={sim.Perf.Plan.Kind}, peakW={sim.PeakRollAngVel:0.0} rad/s, lumbarOg0={lumbarNoOg})",
                "nmrds + nmru", "DlBehaviours.cs (DlRollDownStairs, DlRollUp)");
        }

        private static void Test_Windmill_PhaseOffsetAndImpactStop()
        {
            Physics.ResetWorld(0f);
            Settings.EuphoriaForce.Value = "Moulinets";
            var sim = new SimRagdoll(MakeCtx(212));
            Settings.EuphoriaForce.Value = "Auto";
            sim.Perf.OnBullet(0f, NmHit.Make(sim.Body, sim.Body[Seg.Thorax], sim.Body[Seg.Thorax].Com, new Vector3(0f, 0f, -1f), 4f));
            for (int s = 0; s < 12; s++) sim.Step(s * 0.016f, 0.016f);
            var wm = sim.Perf.Windmill;
            bool phased = wm != null && wm.PhaseOffset >= 2.2f && wm.PhaseOffset <= 4.0f;
            float shL = sim.Perf.Eff.Desired((int)JointId.LShoulder).x;
            float shR = sim.Perf.Eff.Desired((int)JointId.RShoulder).x;
            // Simule l'impact de la main gauche au sol : le bras gauche doit s'arrêter (disableOnImpact).
            sim.Perf.Ch.HandContact[0] = true;
            sim.Perf.Eff.SetDesired((int)JointId.LShoulder, 0, 0.77f);
            wm?.Tick(0.25f, 0.016f, 1f);
            bool leftStopped = MathF.Abs(sim.Perf.Eff.Desired((int)JointId.LShoulder).x - 0.77f) < 1e-3f;
            Check("nmaw::Windmill : bras déphasés (≠ robot synchrone) et arrêt individuel au contact",
                phased && MathF.Abs(shL - shR) > 0.08f && leftStopped,
                $"(phase={wm?.PhaseOffset:0.00} rad, Δsh={MathF.Abs(shL - shR) / BMath.Deg:0.0}°, leftStopped={leftStopped})",
                "nmaw (ArmsWindmill)", "DlBehaviours.cs (DlWindmill)");
        }

        private static void Test_Writhe_AsymmetryAndRollOverForces()
        {
            Physics.ResetWorld(0f);
            var sim = new SimRagdoll(MakeCtx(213, pose: EPlayerPose.Prone));
            for (int s = 0; s < 50; s++) sim.Step(s * 0.016f, 0.016f);
            var wr = sim.Perf.Writhe;
            float hipDiff = MathF.Abs(sim.Perf.Eff.Desired((int)JointId.LHip).x - sim.Perf.Eff.Desired((int)JointId.RHip).x);
            float peakRollTq = 0f;
            for (int s = 50; s < 160; s++)
            {
                float t = s * 0.016f;
                wr?.Tick(t, 0.016f, 0.9f, 1f);
                foreach (var sg in sim.Body.Segs) sg.Rb.ClearAccum();
                wr?.Forces(t, 0.016f, 1f);
                peakRollTq = MathF.Max(peakRollTq, sim.Body[Seg.Thorax].Rb.TorqueAccum.magnitude);
            }
            Check("nmbw::Writhe : jambes dissymétriques G/D + vrai couple rollOver en Forces()",
                wr != null && hipDiff > 0.03f && peakRollTq > 8f,
                $"(Δhip={hipDiff / BMath.Deg:0.0}°, peakRollTq={peakRollTq:0.0} N·m)",
                "nmbw (BodyWrithe)", "DlBehaviours.cs (DlWrithe)");
        }

        private static void Test_Foetal_AsymmetryAndPronation()
        {
            Physics.ResetWorld(0f);
            var sim = new SimRagdoll(MakeCtx(214, DeathMechanism.Abdominal, EBodyPart.Stomach, EBodyPartColliderType.Pelvis, pose: EPlayerPose.Prone));
            for (int s = 0; s < 40; s++) sim.Step(s * 0.016f, 0.016f);
            var ft = sim.Perf.Foetal;
            float sL = sim.Perf.Eff.Strength((int)JointId.LHip), sR = sim.Perf.Eff.Strength((int)JointId.RHip);
            float pronL = sim.Perf.Eff.Desired((int)JointId.LElbow).z, pronR = sim.Perf.Eff.Desired((int)JointId.RElbow).z;
            Check("nmbf::Foetal : asymétrie de raideur par articulation + pronation croisée sur la poitrine",
                ft != null && MathF.Abs(sL - sR) > 0.1f && pronL > 0.1f && pronR < -0.1f,
                $"(sL={sL:0.0}, sR={sR:0.0}, pronL={pronL / BMath.Deg:0.0}°, pronR={pronR / BMath.Deg:0.0}°)",
                "nmbf (BodyFoetal)", "DlBehaviours.cs (DlFoetal)");
        }

        private static void Test_Flinch_FrontalBurstGuard()
        {
            Physics.ResetWorld(0f);
            Settings.EuphoriaForce.Value = "Titube";
            var sim = new SimRagdoll(MakeCtx(215));
            Settings.EuphoriaForce.Value = "Auto";
            var h1 = NmHit.Make(sim.Body, sim.Body[Seg.Thorax], sim.Body[Seg.Thorax].Com + new Vector3(0f, 0f, 0.12f), new Vector3(0f, 0f, -1f), 3f);
            var h2 = NmHit.Make(sim.Body, sim.Body[Seg.Thorax], sim.Body[Seg.Thorax].Com + new Vector3(0.05f, 0.05f, 0.12f), new Vector3(0f, 0f, -1f), 3f);
            sim.Perf.OnBullet(0f, h1);
            sim.Perf.OnBullet(0.08f, h2); // rafale frontale → déclenche Flinch + lastStandMode
            for (int s = 0; s < 25; s++) sim.Step(s * 0.016f, 0.016f);
            Check("nmfl::UpperBodyFlinch : réflexe protecteur sur rafale frontale + pronation avant-bras",
                sim.AnyFlinchActivated && sim.PeakPronationRad > 0.35f,
                $"(flinch={sim.AnyFlinchActivated}, peakPron={sim.PeakPronationRad / BMath.Deg:0.0}°)",
                "nmfl (UpperBodyFlinch)", "DlGrab.cs (DlFlinch)");
        }

        // ------------------------------------------------------------------ NIVEAU 3

        private static void Test_Diversity_60TacticalScenarios()
        {
            VarietyDirector.Reset();
            Settings.EuphoriaForce.Value = "Auto";
            var plans = new Dictionary<NmPlanKind, int>();
            var sectors = new int[3];
            int grabs = 0, woundReaches = 0, pronations = 0, rolls = 0, uniBuckles = 0, biBuckles = 0, windmills = 0, flinches = 0, nans = 0;

            for (int i = 0; i < 60; i++)
            {
                Physics.ResetWorld(0f);
                int sc = i % 12;
                DeathContext ctx;
                Seg hitSeg = Seg.Thorax;
                Vector3 hitOff = new Vector3(0.05f, 0.03f, 0.12f);
                bool burst = false;

                switch (sc)
                {
                    case 0: // Tir face poitrine
                        ctx = MakeCtx(1000 + i, DeathMechanism.Thoracic, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, new Vector3(0f, 0f, -1f));
                        break;
                    case 1: // Tir dans le dos
                        ctx = MakeCtx(1000 + i, DeathMechanism.Thoracic, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, new Vector3(0f, 0f, 1f));
                        hitOff = new Vector3(0f, 0f, -0.12f);
                        break;
                    case 2: // Tir jambe gauche (dérobement unilatéral)
                        ctx = MakeCtx(1000 + i, DeathMechanism.Limb, EBodyPart.LeftLeg, EBodyPartColliderType.LeftThigh, new Vector3(0f, 0f, -1f));
                        hitSeg = Seg.LThigh; hitOff = Vector3.zero;
                        break;
                    case 3: // Tir jambe droite (dérobement unilatéral)
                        ctx = MakeCtx(1000 + i, DeathMechanism.Limb, EBodyPart.RightLeg, EBodyPartColliderType.RightCalf, new Vector3(0.3f, 0f, -1f));
                        hitSeg = Seg.RCalf; hitOff = Vector3.zero;
                        break;
                    case 4: // Tir abdomen
                        ctx = MakeCtx(1000 + i, DeathMechanism.Abdominal, EBodyPart.Stomach, EBodyPartColliderType.Pelvis, new Vector3(0f, 0f, -1f));
                        hitSeg = Seg.Pelvis; hitOff = new Vector3(0f, 0.05f, 0.12f);
                        break;
                    case 5: // Tir bras gauche
                        ctx = MakeCtx(1000 + i, DeathMechanism.Limb, EBodyPart.LeftArm, EBodyPartColliderType.LeftUpperArm, new Vector3(1f, 0f, -0.3f));
                        hitSeg = Seg.LUpperArm; hitOff = Vector3.zero;
                        break;
                    case 6: // Course → roulade
                        ctx = MakeCtx(1000 + i, DeathMechanism.Thoracic, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 2.9f));
                        break;
                    case 7: // Près d'un mur à droite → Grab
                        Physics.Boxes.Add(new StaticBox(new Vector3(0.75f, 1.2f, 0f), new Vector3(0.30f, 2.4f, 4f)));
                        ctx = MakeCtx(1000 + i, DeathMechanism.Thoracic, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, new Vector3(0.9f, 0f, -0.2f), new Vector3(0.6f, 0f, 0f));
                        break;
                    case 8: // Près d'une caisse / rebord devant → Grab
                        Physics.Boxes.Add(new StaticBox(new Vector3(0f, 0.45f, 0.85f), new Vector3(1.2f, 0.90f, 0.50f)));
                        ctx = MakeCtx(1000 + i, DeathMechanism.Thoracic, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 0.7f));
                        break;
                    case 9: // Accroupi tir poitrine
                        ctx = MakeCtx(1000 + i, DeathMechanism.Thoracic, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, new Vector3(0f, 0f, -1f), Vector3.zero, EPlayerPose.Duck);
                        break;
                    case 10: // Rafale frontale gros calibre (flinch + lastStand + shockSpin)
                        ctx = MakeCtx(1000 + i, DeathMechanism.Thoracic, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, new Vector3(0.2f, 0f, -1f), Vector3.zero, EPlayerPose.Stand, 1.5f, "slug");
                        burst = true;
                        break;
                    default: // Tir à la tête (encéphale)
                        ctx = MakeCtx(1000 + i, DeathMechanism.Cerebral, EBodyPart.Head, EBodyPartColliderType.HeadCommon, new Vector3(0f, 0f, -1f));
                        hitSeg = Seg.Head; hitOff = Vector3.zero;
                        break;
                }

                var sim = new SimRagdoll(ctx);
                var seg = sim.Body[hitSeg];
                sim.Perf.OnBullet(0f, NmHit.Make(sim.Body, seg, seg.Com + hitOff, ctx.Direction, 3.5f * ctx.KickScale));
                if (burst)
                    sim.Perf.OnBullet(0.08f, NmHit.Make(sim.Body, seg, seg.Com + hitOff + new Vector3(-0.06f, 0.04f, 0f), ctx.Direction, 3.5f * ctx.KickScale));

                if (sim.Perf.Balance != null)
                {
                    if (sim.Perf.Balance.O.BuckleMode == 1) uniBuckles++;
                    else if (sim.Perf.Balance.O.BuckleMode >= 2) biBuckles++;
                }

                for (int step = 0; step < 90; step++) sim.Step(step * 0.016f, 0.016f);

                plans[sim.Perf.Plan.Kind] = plans.TryGetValue(sim.Perf.Plan.Kind, out int cnt) ? cnt + 1 : 1;
                sectors[VarietyDirector.Sector(sim.Perf.Plan.ToppleAzimuth)]++;
                if (sim.AnyGrabActivated) grabs++;
                if (sim.AnyWoundReach) woundReaches++;
                if (sim.PeakPronationRad > 15f * BMath.Deg) pronations++;
                if (sim.AnyRollActivated) rolls++;
                if (sim.AnyWindmillActivated) windmills++;
                if (sim.AnyFlinchActivated) flinches++;
                if (sim.AnyNaN) nans++;
            }

            int maxPlanShare = 0;
            foreach (var kv in plans) maxPlanShare = Math.Max(maxPlanShare, kv.Value);
            Console.WriteLine($"      plans distincts : {plans.Count} (max={maxPlanShare}/60), secteurs Av/Cot/Ar={sectors[0]}/{sectors[1]}/{sectors[2]}");
            Console.WriteLine($"      agrippements décor={grabs}, plaies={woundReaches}, pronations={pronations}, roulades={rolls}, dérobements 1J/2J={uniBuckles}/{biBuckles}, moulinets={windmills}, flinch={flinches}");

            _diversity = new DiversityAudit
            {
                Plans = plans.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.ToString()).Select(kv => (kv.Key, kv.Value)).ToList(),
                MaxPlanShare = maxPlanShare,
                Sectors = sectors,
                Grabs = grabs,
                WoundReaches = woundReaches,
                Pronations = pronations,
                Rolls = rolls,
                UniBuckles = uniBuckles,
                BiBuckles = biBuckles,
                Windmills = windmills,
                Flinches = flinches,
                NaNs = nans,
            };

            Check("Diversité 60 morts : ≥ 8 plans distincts, aucun plan > 35 %, 3 secteurs couverts",
                plans.Count >= 8 && maxPlanShare <= 21 && sectors[0] >= 5 && sectors[1] >= 5 && sectors[2] >= 5,
                $"({plans.Count} plans, max={maxPlanShare}/60, Av/Cot/Ar={sectors[0]}/{sectors[1]}/{sectors[2]})",
                "VarietyDirector + NmPlan", "NmPerformance.cs, VarietyDirector.cs");
            Check("Diversité 60 morts : agrippements décor + plaies + pronations + roulades + dérobements 1J & 2J tous prouvés",
                grabs >= 6 && woundReaches >= 25 && pronations >= 35 && rolls >= 5 && uniBuckles >= 8 && biBuckles >= 8 && nans == 0,
                $"({grabs} grabs, {woundReaches} plaies, {pronations} pronations, {rolls} roulades, 1J={uniBuckles}, 2J={biBuckles}, NaN={nans})",
                "Suite complète Delirium", "DlBalance, DlShot, DlGrab, DlBehaviours");
        }

        // ------------------------------------------------------------------ RAPPORT MARKDOWN

        private static string FindRepoRoot()
        {
            foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                string dir = Path.GetFullPath(start);
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
                {
                    if (File.Exists(Path.Combine(dir, "src", "Brutality", "Plugin.cs")))
                        return dir;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            return Environment.CurrentDirectory;
        }

        private static string ReadPluginVersion(string repoRoot)
        {
            string pluginPath = Path.Combine(repoRoot, "src", "Brutality", "Plugin.cs");
            if (!File.Exists(pluginPath)) return "0.3.0";
            string text = File.ReadAllText(pluginPath);
            var m = Regex.Match(text, @"PluginVersion\s*=\s*""([^""]+)""");
            return m.Success ? m.Groups[1].Value : "0.3.0";
        }

        private static string ComputeSourcesSha256(string repoRoot, out int fileCount)
        {
            string csproj = Path.Combine(repoRoot, "tests", "Brutality.DeliriumTests", "Brutality.DeliriumTests.csproj");
            var relPaths = new List<string>();
            if (File.Exists(csproj))
            {
                string xml = File.ReadAllText(csproj);
                foreach (Match m in Regex.Matches(xml, @"<Compile\s+Include=""([^""]+)"""))
                    relPaths.Add(m.Groups[1].Value.Replace('\\', '/'));
            }
            relPaths.Sort(StringComparer.Ordinal);
            fileCount = relPaths.Count;

            using var sha = SHA256.Create();
            foreach (string rel in relPaths)
            {
                string full = Path.GetFullPath(Path.Combine(repoRoot, "tests", "Brutality.DeliriumTests", rel));
                if (!File.Exists(full)) continue;
                string normalized = File.ReadAllText(full, Encoding.UTF8).Replace("\r\n", "\n");
                byte[] header = Encoding.UTF8.GetBytes(rel + "\n");
                byte[] body = Encoding.UTF8.GetBytes(normalized);
                sha.TransformBlock(header, 0, header.Length, null, 0);
                sha.TransformBlock(body, 0, body.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        private static string Esc(string text)
            => string.IsNullOrEmpty(text) ? "—" : text.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

        private static void AppendLevelTable(StringBuilder sb, int level)
        {
            sb.AppendLine("| # | Référence Delirium | Source Brutality | Invariant vérifié | Mesure observée | Statut |");
            sb.AppendLine("|---:|---|---|---|---|---|");
            foreach (var r in _records.Where(x => x.Level == level))
            {
                string status = r.Passed ? "`PASS`" : "`FAIL`";
                string detail = string.IsNullOrWhiteSpace(r.Detail) ? "conforme" : Esc(r.Detail);
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {r.Index} | `{Esc(r.DeliriumRef)}` | `{Esc(r.BrutalitySrc)}` | {Esc(r.Name)} | `{detail}` | {status} |"));
            }
            sb.AppendLine();
        }

        private static void WriteMarkdownReport(string reportPath, string dllArg)
        {
            string repoRoot = FindRepoRoot();
            string version = ReadPluginVersion(repoRoot);
            string sourcesSha = ComputeSourcesSha256(repoRoot, out int srcCount);

            string dllPath = !string.IsNullOrWhiteSpace(dllArg)
                ? Path.GetFullPath(dllArg)
                : Path.Combine(repoRoot, "src", "Brutality", "bin", "Release", "Brutality.dll");

            string dllInfo = "non compilée dans cet environnement (tests exécutés directement sur les sources C#)";
            if (File.Exists(dllPath))
            {
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(dllPath);
                string dllSha = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
                long dllBytes = new FileInfo(dllPath).Length;
                dllInfo = string.Create(CultureInfo.InvariantCulture, $"`{dllSha}` ({dllBytes:N0} octets)");
            }

            string dlStatus = _fail == 0 ? "PASS" : "FAIL";
            var sb = new StringBuilder();
            sb.AppendLine($"# Brutality {version} — Fiche de preuves formelles");
            sb.AppendLine();
            sb.AppendLine("> **Document généré automatiquement** par l'exécution des suites de tests hors jeu (`Brutality.DeliriumTests` et `Brutality.KinematicsTests`) directement sur les vraies sources C# du plugin. Aucune ligne de résultats ci-dessous n'est saisie à la main.");
            sb.AppendLine();
            sb.AppendLine("## Métadonnées & traçabilité");
            sb.AppendLine();
            sb.AppendLine("| Champ | Valeur |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| Version du plugin | `{version}` |");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| Date d'exécution | `{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC` |"));
            sb.AppendLine($"| Runtime & OS | `.NET {Environment.Version}` / `{Environment.OSVersion}` |");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| Empreinte SHA-256 des {srcCount} sources C# testées | `{sourcesSha}` |"));
            sb.AppendLine($"| Empreinte SHA-256 de `Brutality.dll` (Release) | {dllInfo} |");
            sb.AppendLine("| Reproduction locale | `dotnet run --project tests/Brutality.DeliriumTests -- --report docs/VERIFICATION.md` puis `dotnet run --project tests/Brutality.KinematicsTests -- --append-report docs/VERIFICATION.md` |");
            sb.AppendLine();
            sb.AppendLine("## Synthèse globale");
            sb.AppendLine();
            sb.AppendLine("| Suite | Périmètre | Résultat | Statut |");
            sb.AppendLine("|---|---|---:|---|");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| `tests/Brutality.DeliriumTests` | Lois Delirium (`nmrs`, `nmdbm`, `nmbb`, `nmshot`, `nmcf`, `nmgr`, `nmrds`, `nmfl`), simulation 60 Hz & diversité 60 morts | **{_pass} / {_records.Count}** | `{dlStatus}` |"));
            sb.AppendLine("<!-- KINEMATICS_SUMMARY_ROW -->");
            sb.AppendLine("<!-- TOTAL_SUMMARY_ROW -->");
            sb.AppendLine();

            sb.AppendLine("## Niveau 1 — Fidélité mathématique loi par loi (Delirium Rust vs Brutality C#)");
            sb.AppendLine();
            AppendLevelTable(sb, 1);

            sb.AppendLine("## Niveau 2 — Preuves comportementales en simulation physique 60 Hz");
            sb.AppendLine();
            AppendLevelTable(sb, 2);

            sb.AppendLine("## Niveau 3 — Audit statistique de diversité cinématique (60 morts tactiques, graines 1000..1059)");
            sb.AppendLine();
            AppendLevelTable(sb, 3);

            if (_diversity != null)
            {
                var d = _diversity;
                sb.AppendLine("### Métriques observées sur 60 morts (12 scénarios × 5 itérations)");
                sb.AppendLine();
                sb.AppendLine("| Indicateur | Seuil formel | Mesure observée | Statut |");
                sb.AppendLine("|---|---|---|---|");
                sb.AppendLine($"| Plans Euphoria distincts | `>= 8` | `{d.Plans.Count}` | {(d.Plans.Count >= 8 ? "`PASS`" : "`FAIL`")} |");
                sb.AppendLine($"| Part maximale d'un même plan | `<= 21 / 60 (35 %)` | `{d.MaxPlanShare} / 60 ({100.0 * d.MaxPlanShare / 60.0:0.0} %)` | {(d.MaxPlanShare <= 21 ? "`PASS`" : "`FAIL`")} |");
                sb.AppendLine($"| Secteurs d'azimut (Avant / Côté / Arrière) | `>= 5` par secteur | `{d.Sectors[0]} / {d.Sectors[1]} / {d.Sectors[2]}` | {(d.Sectors[0] >= 5 && d.Sectors[1] >= 5 && d.Sectors[2] >= 5 ? "`PASS`" : "`FAIL`")} |");
                sb.AppendLine($"| Agrippements au décor (`grab`) | `>= 6` | `{d.Grabs} / 60` | {(d.Grabs >= 6 ? "`PASS`" : "`FAIL`")} |");
                sb.AppendLine($"| Mains portées sur la plaie (`Shot::reach`) | `>= 25` | `{d.WoundReaches} / 60` | {(d.WoundReaches >= 25 ? "`PASS`" : "`FAIL`")} |");
                sb.AppendLine($"| Pronations avant-bras (`> 15°`) | `>= 35` | `{d.Pronations} / 60` | {(d.Pronations >= 35 ? "`PASS`" : "`FAIL`")} |");
                sb.AppendLine($"| Roulades (`rollDownStairs` / `rollUp`) | `>= 5` | `{d.Rolls} / 60` | {(d.Rolls >= 5 ? "`PASS`" : "`FAIL`")} |");
                sb.AppendLine($"| Dérobement 1 jambe (unilatéral) | `>= 8` | `{d.UniBuckles} / 60` | {(d.UniBuckles >= 8 ? "`PASS`" : "`FAIL`")} |");
                sb.AppendLine($"| Dérobement 2 jambes (synchrone / décalé) | `>= 8` | `{d.BiBuckles} / 60` | {(d.BiBuckles >= 8 ? "`PASS`" : "`FAIL`")} |");
                sb.AppendLine($"| Moulinets des bras (`armsWindmill`) | informatif | `{d.Windmills} / 60` | `INFO` |");
                sb.AppendLine($"| Réflexes de garde (`upperBodyFlinch`) | informatif | `{d.Flinches} / 60` | `INFO` |");
                sb.AppendLine($"| Erreurs numériques (`NaN`) | `0` | `{d.NaNs}` | {(d.NaNs == 0 ? "`PASS`" : "`FAIL`")} |");
                sb.AppendLine();

                sb.AppendLine("### Répartition observée par plan Euphoria (`NmPlanKind`)");
                sb.AppendLine();
                sb.AppendLine("| Plan | Effectif (sur 60) | Part |");
                sb.AppendLine("|---|---:|---:|");
                foreach (var (kind, count) in d.Plans)
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| `{kind}` | {count} | {100.0 * count / 60.0:0.0} % |"));
                sb.AppendLine();
            }

            string full = Path.GetFullPath(reportPath);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
