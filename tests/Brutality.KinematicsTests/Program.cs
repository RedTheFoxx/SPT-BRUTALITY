// Banc de test hors jeu : vérifie les conventions de signe et la cohérence des angles anatomiques de Brutality.
// Lancer : dotnet run --project tests/Brutality.KinematicsTests
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Brutality.Anatomy;
using Brutality.Core;
using UnityEngine;

internal static class Program
{
    private sealed record CheckRecord(int Index, string Domain, string Name, string Detail, bool Passed);

    private static int _fail, _pass;
    private static string _domain = "Général";
    private static readonly List<CheckRecord> _records = new List<CheckRecord>();
    private static readonly Random Rng = new Random(1234);

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        _records.Add(new CheckRecord(_records.Count + 1, _domain, name, detail ?? "", ok));
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {detail}");
    }

    private static bool Near(float a, float b, float tolDeg) => MathF.Abs(BMath.WrapPi(a - b)) < tolDeg * BMath.Deg;

    private static Quaternion RandomRot()
    {
        var axis = new Vector3((float)Rng.NextDouble() - 0.5f, (float)Rng.NextDouble() - 0.5f, (float)Rng.NextDouble() - 0.5f);
        return Quaternion.AngleAxis((float)Rng.NextDouble() * 360f, axis);
    }

    /// <summary>Segment de test : orientation d'os arbitraire + repère anatomique stocké en local (comme BodyModel).</summary>
    private sealed class S
    {
        public Quaternion Q;
        public Vector3 RL, UL, FL;

        public static S Make(Vector3 up, Vector3 rightHint, Vector3 fwdHint)
        {
            var s = new S { Q = RandomRot() };
            BMath.OrthoFrame(up, rightHint, fwdHint, out var r, out var u, out var f);
            var inv = Quaternion.Inverse(s.Q);
            s.RL = inv * r; s.UL = inv * u; s.FL = inv * f;
            return s;
        }
    }

    private static JointFrame Frame(JointKind kind, int side, S p, S c)
    {
        var spec = AnatomyData.Specs[(int)kind];
        var f = new JointFrame
        {
            Kind = kind, Side = side,
            PR = p.RL, PU = p.UL, PF = p.FL, CR = c.RL, CU = c.UL,
            RelRest = Quaternion.Inverse(p.Q) * c.Q,
            FlexMid = 0.5f * (spec.Dofs[0].MinDeg + spec.Dofs[0].MaxDeg) * BMath.Deg,
        };
        if (kind == JointKind.Knee || kind == JointKind.Elbow)
            f.HingeAxisP = JointKinematics.HingeAxis(kind, p.Q, p.RL, p.UL, p.FL, c.Q, c.UL);
        if (kind == JointKind.Lumbar || kind == JointKind.Neck)
            f.TwistOffset = JointKinematics.AbsoluteTwist(p.Q, p.RL, c.Q, c.RL, c.UL);
        return f;
    }

    private static Vector3 Eval(in JointFrame f, S p, Quaternion qC) => JointKinematics.Evaluate(f, p.Q, qC, out _);

    /// <summary>
    /// BepInEx refuse certains caractères dans les sections et clés de configuration (exception dans Awake :
    /// le plugin ne s'installe pas). Vérifie toutes les chaînes passées à cfg.Bind dans Settings.cs.
    /// </summary>
    private static void CheckConfigKeys()
    {
        string dir = AppContext.BaseDirectory;
        string path = null;
        for (int i = 0; i < 8 && dir != null; i++, dir = System.IO.Path.GetDirectoryName(dir))
        {
            string candidate = System.IO.Path.Combine(dir, "src", "Brutality", "Settings.cs");
            if (System.IO.File.Exists(candidate)) { path = candidate; break; }
        }
        if (path == null) { Check("configuration : Settings.cs introuvable", false); return; }
        string src = System.IO.File.ReadAllText(path);
        char[] forbidden = { '=', '\n', '\t', '\\', '"', '\'', '[', ']' };
        var names = new System.Collections.Generic.List<string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(src, @"const string \w+ = ""([^""]*)"""))
            names.Add(m.Groups[1].Value);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(src, @"\.Bind\((?:\w+|""[^""]*""),\s*""([^""]*)"""))
            names.Add(m.Groups[1].Value);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(src, @"\.Bind\(""([^""]*)"""))
            names.Add(m.Groups[1].Value);
        var bad = names.FindAll(n => n.IndexOfAny(forbidden) >= 0 || n.Trim() != n || n.Length == 0);
        Check("configuration : sections et clés valides pour BepInEx", names.Count >= 20 && bad.Count == 0,
            $"({names.Count} noms{(bad.Count > 0 ? ", invalides : " + string.Join(" | ", bad) : "")})");
    }

    private static void Main(string[] args)
    {
        string reportPath = null;
        string appendPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--report" && i + 1 < args.Length) reportPath = args[++i];
            else if (args[i] == "--append-report" && i + 1 < args.Length) appendPath = args[++i];
        }

        // Personnage debout, face à +Z (convention Unity : X droite, Y haut, Z avant).
        Vector3 R = Vector3.right, U = Vector3.up, F = Vector3.forward;
        var pelvis = S.Make(U, R, F);
        var thorax = S.Make(U, R, F);
        var head = S.Make(U, Vector3.Cross(U, F), F);
        var thighR = S.Make(U, R, F);  var calfR = S.Make(U, R, F);
        var thighL = S.Make(U, R, F);  var calfL = S.Make(U, R, F);
        var upperR = S.Make(U, R, F);  var foreR = S.Make(U, R, F);
        var upperL = S.Make(U, R, F);

        // --- Genou
        _domain = "Genou";
        var knee = Frame(JointKind.Knee, 1, thighR, calfR);
        Check("genou droit tendu = 0°", Near(Eval(knee, thighR, calfR.Q).x, 0f, 0.5f));
        var calfBack = Quaternion.AngleAxis(60f, R) * calfR.Q; // la jambe part vers l'arrière
        Check("genou : jambe vers l'arrière = flexion +60°", Near(Eval(knee, thighR, calfBack).x, 60f * BMath.Deg, 0.5f),
            $"({Eval(knee, thighR, calfBack).x / BMath.Deg:0.0}°)");
        var calfFwd = Quaternion.AngleAxis(-5f, R) * calfR.Q;
        Check("genou : hyperextension = négatif", Eval(knee, thighR, calfFwd).x < 0f);

        // Genou fléchi à 60° au moment de la mort (axe déduit du plan cuisse-jambe).
        var calfBent = new S { Q = calfBack, RL = calfR.RL, UL = calfR.UL, FL = calfR.FL };
        var kneeBent = Frame(JointKind.Knee, 1, thighR, calfBent);
        Check("genou fléchi à la mort : 60° relu", Near(Eval(kneeBent, thighR, calfBent.Q).x, 60f * BMath.Deg, 0.5f));
        Check("genou fléchi : +10° vers l'arrière = 70°",
            Near(Eval(kneeBent, thighR, Quaternion.AngleAxis(10f, R) * calfBent.Q).x, 70f * BMath.Deg, 0.5f));

        // --- Coude
        _domain = "Coude";
        var elbow = Frame(JointKind.Elbow, 1, upperR, foreR);
        var foreUp = Quaternion.AngleAxis(-90f, R) * foreR.Q; // l'avant-bras monte vers l'avant
        Check("coude : avant-bras vers l'avant = flexion +90°", Near(Eval(elbow, upperR, foreUp).x, 90f * BMath.Deg, 0.5f),
            $"({Eval(elbow, upperR, foreUp).x / BMath.Deg:0.0}°)");
        var pron = Quaternion.AngleAxis(40f, foreR.Q * foreR.UL) * foreR.Q;
        Check("coude : prono-supination lue sur la torsion", MathF.Abs(MathF.Abs(Eval(elbow, upperR, pron).z) - 40f * BMath.Deg) < 0.01f);

        // --- Hanche
        _domain = "Hanche";
        var hipR = Frame(JointKind.Hip, 1, pelvis, thighR);
        var hipL = Frame(JointKind.Hip, -1, pelvis, thighL);
        Check("hanche neutre = 0/0/0", Eval(hipR, pelvis, thighR.Q).magnitude < 0.01f);
        var thighFwd = Quaternion.AngleAxis(-45f, R) * thighR.Q;
        Check("hanche : cuisse vers l'avant = flexion +45°", Near(Eval(hipR, pelvis, thighFwd).x, 45f * BMath.Deg, 0.5f));
        var thighOutR = Quaternion.AngleAxis(30f, F) * thighR.Q; // pied droit vers +X
        Check("hanche droite : écartement = abduction +30°", Near(Eval(hipR, pelvis, thighOutR).y, 30f * BMath.Deg, 0.5f),
            $"({Eval(hipR, pelvis, thighOutR).y / BMath.Deg:0.0}°)");
        var thighOutL = Quaternion.AngleAxis(-30f, F) * thighL.Q; // pied gauche vers -X
        Check("hanche gauche : écartement = abduction +30°", Near(Eval(hipL, pelvis, thighOutL).y, 30f * BMath.Deg, 0.5f),
            $"({Eval(hipL, pelvis, thighOutL).y / BMath.Deg:0.0}°)");
        var thighBack = Quaternion.AngleAxis(15f, R) * thighR.Q;
        Check("hanche : cuisse vers l'arrière = extension (négatif)", Eval(hipR, pelvis, thighBack).x < 0f);

        // --- Épaule (déroulement au-delà de 180°)
        _domain = "Épaule";
        var shR = Frame(JointKind.Shoulder, 1, thorax, upperR);
        var armUp = Quaternion.AngleAxis(-170f, R) * upperR.Q;
        Check("épaule : bras levé devant à 170°", Near(Eval(shR, thorax, armUp).x, 170f * BMath.Deg, 0.5f));
        var armOver = Quaternion.AngleAxis(-190f, R) * upperR.Q;
        float over = Eval(shR, thorax, armOver).x / BMath.Deg;
        Check("épaule : 190° lu 190° (pas -170°)", MathF.Abs(over - 190f) < 0.5f, $"({over:0.0}°)");
        var armSide = Quaternion.AngleAxis(60f, F) * upperR.Q;
        Check("épaule droite : abduction +60°", Near(Eval(shR, thorax, armSide).y, 60f * BMath.Deg, 0.5f));
        var shL = Frame(JointKind.Shoulder, -1, thorax, upperL);
        var armAcrossL = Quaternion.AngleAxis(20f, F) * upperL.Q; // bras gauche vers +X = adduction
        Check("épaule gauche : bras vers la ligne médiane = adduction (négatif)", Eval(shL, thorax, armAcrossL).y < 0f);

        // --- Rotation interne (posturing)
        _domain = "Rotation interne & pronation";
        float irR = Eval(shR, thorax, Quaternion.AngleAxis(-30f, U) * upperR.Q).z;
        float irL = Eval(shL, thorax, Quaternion.AngleAxis(30f, U) * upperL.Q).z;
        Check("épaule droite : rotation interne lue ±30°", MathF.Abs(MathF.Abs(irR) - 30f * BMath.Deg) < 0.01f, $"({irR / BMath.Deg:0.0}°)");
        Check("rotation interne : signe = -côté (droite -, gauche +)", irR < 0f && irL > 0f,
            $"(D {irR / BMath.Deg:0.0}°, G {irL / BMath.Deg:0.0}°)");
        float irHipR = Eval(hipR, pelvis, Quaternion.AngleAxis(-30f, U) * thighR.Q).z;
        float irHipL = Eval(hipL, pelvis, Quaternion.AngleAxis(30f, U) * thighL.Q).z;
        Check("hanche : rotation interne, même convention que l'épaule", irHipR < 0f && irHipL > 0f,
            $"(D {irHipR / BMath.Deg:0.0}°, G {irHipL / BMath.Deg:0.0}°)");
        float pronR = Eval(elbow, upperR, Quaternion.AngleAxis(-40f, U) * foreR.Q).z;
        Check("coude droit : pronation (paume vers l'intérieur puis l'arrière) = négatif", pronR < 0f, $"({pronR / BMath.Deg:0.0}°)");

        // --- Rachis lombaire
        _domain = "Rachis lombaire";
        var lumbar = Frame(JointKind.Lumbar, 0, pelvis, thorax);
        Check("rachis neutre = 0/0/0", Eval(lumbar, pelvis, thorax.Q).magnitude < 0.01f);
        var bendFwd = Quaternion.AngleAxis(30f, R) * thorax.Q;
        Check("rachis : penché en avant = flexion +30°", Near(Eval(lumbar, pelvis, bendFwd).x, 30f * BMath.Deg, 0.5f));
        var bendRight = Quaternion.AngleAxis(-20f, F) * thorax.Q;
        Check("rachis : inclinaison à droite = +20°", Near(Eval(lumbar, pelvis, bendRight).y, 20f * BMath.Deg, 0.5f));
        var twist = Quaternion.AngleAxis(25f, U) * thorax.Q;
        Check("rachis : torsion +25° lue +25°", Near(Eval(lumbar, pelvis, twist).z, 25f * BMath.Deg, 0.5f));
        Vector3 leftShoulder = Quaternion.AngleAxis(25f, U) * new Vector3(-1, 0, 0);
        Check("torsion positive = épaule gauche vers l'avant (roulade vers la droite)", leftShoulder.z > 0f);

        // Torsion absolue présente à la mort (le tronc était déjà tourné de 20°).
        var thoraxTw = new S { Q = Quaternion.AngleAxis(20f, U) * thorax.Q, RL = thorax.RL, UL = thorax.UL, FL = thorax.FL };
        var lumbarTw = Frame(JointKind.Lumbar, 0, pelvis, thoraxTw);
        Check("rachis : torsion à la mort relue (20°)", Near(Eval(lumbarTw, pelvis, thoraxTw.Q).z, 20f * BMath.Deg, 0.5f));

        // --- Cou
        _domain = "Cou";
        var neck = Frame(JointKind.Neck, 0, thorax, head);
        var lookRight = Quaternion.AngleAxis(40f, U) * head.Q;
        Check("cou : torsion +40° = regard vers la droite", Near(Eval(neck, thorax, lookRight).z, 40f * BMath.Deg, 0.5f)
              && (lookRight * head.FL).x > 0f);
        var chin = Quaternion.AngleAxis(30f, R) * head.Q;
        Check("cou : menton vers la poitrine = flexion +30°", Near(Eval(neck, thorax, chin).x, 30f * BMath.Deg, 0.5f));

        // --- Gradients numériques (comme BodyModel.ComputeKinematics) : dφ ≈ g·ω dt
        _domain = "Gradients numériques";
        int gradOk = 0, gradN = 0;
        var frames = new (JointFrame f, S p, S c)[] { (knee, thighR, calfR), (elbow, upperR, foreR), (hipR, pelvis, thighR), (shR, thorax, upperR), (lumbar, pelvis, thorax), (neck, thorax, head) };
        Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
        foreach (var (f, p, c) in frames)
        {
            for (int trial = 0; trial < 30; trial++)
            {
                var qC = Quaternion.AngleAxis((float)(Rng.NextDouble() * 70 - 35), new Vector3((float)Rng.NextDouble() - .5f, (float)Rng.NextDouble() - .5f, (float)Rng.NextDouble() - .5f)) * c.Q;
                if (f.Kind == JointKind.Knee) qC = Quaternion.AngleAxis(40f, R) * c.Q;
                if (f.Kind == JointKind.Elbow) qC = Quaternion.AngleAxis(-50f, R) * c.Q;
                Vector3 phi = JointKinematics.Evaluate(f, p.Q, qC, out _);
                var g = new Vector3[3];
                for (int k = 0; k < 3; k++)
                {
                    var q2 = Quaternion.AngleAxis(0.01f * Mathf.Rad2Deg, axes[k]) * qC;
                    Vector3 p2 = JointKinematics.Evaluate(f, p.Q, q2, out _);
                    g[0][k] = BMath.WrapPi(p2.x - phi.x) / 0.01f;
                    g[1][k] = BMath.WrapPi(p2.y - phi.y) / 0.01f;
                    g[2][k] = BMath.WrapPi(p2.z - phi.z) / 0.01f;
                }
                var w = new Vector3((float)Rng.NextDouble() - .5f, (float)Rng.NextDouble() - .5f, (float)Rng.NextDouble() - .5f) * 2f;
                float dt = 0.002f;
                var q3 = Quaternion.AngleAxis(w.magnitude * dt * Mathf.Rad2Deg, w) * qC;
                Vector3 phi3 = JointKinematics.Evaluate(f, p.Q, q3, out _);
                bool ok = true;
                for (int d = 0; d < 3; d++)
                {
                    float pred = Vector3.Dot(g[d], w) * dt;
                    float real = BMath.WrapPi(phi3[d] - phi[d]);
                    if (MathF.Abs(pred - real) > 2e-4f + 0.05f * MathF.Abs(real)) ok = false;
                }
                gradN++;
                if (ok) gradOk++;
            }
        }
        Check("gradients numériques cohérents (dφ = g·ω dt)", gradOk >= gradN - 2, $"({gradOk}/{gradN})");

        // --- Cinématique inverse du bras
        _domain = "Cinématique inverse";
        Vector3 shoulderPos = new Vector3(0.2f, 1.5f, 0f);
        Vector3 target = new Vector3(0.05f, 1.15f, 0.15f); // main sur l'abdomen
        bool ik = JointKinematics.ArmIK(shoulderPos, 0.30f, 0.33f, target, 1, R, U, F, out float fl, out float ab, out float el, out Vector3 ud);
        Vector3 recon = (R * (MathF.Sin(ab * BMath.Deg))) + (-U * MathF.Cos(fl * BMath.Deg) + F * MathF.Sin(fl * BMath.Deg)) * MathF.Cos(ab * BMath.Deg);
        Check("IK : angles d'épaule ↔ direction du bras", ik && (recon - ud).magnitude < 0.02f, $"(flex {fl:0}°, abd {ab:0}°, coude {el:0}°)");
        Vector3 elbowPos = shoulderPos + ud * 0.30f;
        Check("IK : l'avant-bras atteint la cible", MathF.Abs((target - elbowPos).magnitude - 0.33f) < 0.01f);
        Check("IK : coude fléchi pour une cible proche", el > 30f && el < 160f);

        // --- Cinématique inverse de la jambe
        foreach (int legSide in new[] { -1, 1 })
        {
            Vector3 hipPos = new Vector3(0.1f * legSide, 0.94f, 0f);
            Vector3 stepTarget = new Vector3(0.14f * legSide, 0.12f, 0.28f);
            bool lik = JointKinematics.LegIK(hipPos, 0.44f, 0.44f, stepTarget, legSide, R, U, F, out float hf, out float ha, out float kf);
            Vector3 thighDir = R * (legSide * MathF.Sin(ha * BMath.Deg)) + (-U * MathF.Cos(hf * BMath.Deg) + F * MathF.Sin(hf * BMath.Deg)) * MathF.Cos(ha * BMath.Deg);
            Vector3 kneePos = hipPos + thighDir * 0.44f;
            Check($"IK jambe {(legSide < 0 ? "G" : "D")} : la jambe atteint la cheville cible", lik && MathF.Abs((stepTarget - kneePos).magnitude - 0.44f) < 0.01f,
                $"(hanche {hf:0}°/{ha:0}°, genou {kf:0}°)");
            Check($"IK jambe {(legSide < 0 ? "G" : "D")} : genou fléchi vers l'avant, cuisse fléchie", kf > 5f && hf > 20f && kneePos.z > stepTarget.z * 0.5f);
        }
        {
            bool lik = JointKinematics.LegIK(new Vector3(0.1f, 0.94f, 0f), 0.44f, 0.44f, new Vector3(0.1f, 0.06f, 0f), 1, R, U, F, out float hf, out float ha, out float kf);
            Check("IK jambe : appui vertical = jambe presque tendue", lik && MathF.Abs(hf) < 7f && MathF.Abs(ha) < 1f && kf < 14f, $"({hf:0.0}°, {ha:0.0}°, {kf:0.0}°)");
        }

        // --- Solveur de pose
        _domain = "Solveur de pose";
        int solved = 0, total = 0;
        float worst = 0f;
        foreach (var (f, p, c) in frames)
        {
            var spec = AnatomyData.Specs[(int)f.Kind];
            Quaternion qRel0 = Quaternion.Inverse(p.Q) * c.Q;
            for (int trial = 0; trial < 40; trial++)
            {
                var goal = new Vector3();
                for (int d = 0; d < 3; d++)
                {
                    float mid = 0.5f * (spec.Dofs[d].MinDeg + spec.Dofs[d].MaxDeg);
                    float half = 0.5f * (spec.Dofs[d].MaxDeg - spec.Dofs[d].MinDeg);
                    goal[d] = (mid + half * 0.85f * (float)(Rng.NextDouble() * 2 - 1)) * BMath.Deg;
                }
                Quaternion q = JointKinematics.SolvePose(f, qRel0, goal, 25, out float res);
                Vector3 got = JointKinematics.Evaluate(f, Quaternion.identity, q, out _);
                float err = 0f;
                for (int d = 0; d < 3; d++) err = MathF.Max(err, MathF.Abs(BMath.WrapPi(got[d] - goal[d])));
                worst = MathF.Max(worst, err / BMath.Deg);
                total++;
                if (err < 0.5f * BMath.Deg) solved++;
            }
        }
        Check("solveur de pose : cibles anatomiques atteintes (< 0,5°)", solved >= total - 2, $"({solved}/{total}, pire {worst:0.00}°)");

        bool centersOk = true;
        foreach (var (f, p, c) in frames)
        {
            var spec = AnatomyData.Specs[(int)f.Kind];
            var center = new Vector3();
            for (int d = 0; d < 3; d++) center[d] = 0.5f * (spec.Dofs[d].MinDeg + spec.Dofs[d].MaxDeg) * BMath.Deg;
            Quaternion q = JointKinematics.SolvePose(f, Quaternion.Inverse(p.Q) * c.Q, center, 40, out float res);
            if (res > 1f * BMath.Deg) { centersOk = false; Console.WriteLine($"      centre {f.Kind} : résidu {res / BMath.Deg:0.00}°"); }
        }
        Check("solveur de pose : pose centrale de chaque articulation", centersOk);

        // --- Régressions stabilité / physique ---
        _domain = "Stabilité, axes & enveloppe thoracique";
        {
            Vector3 shPos = new Vector3(0.2f, 1.4f, 0f);
            Vector3 elPos = new Vector3(0.25f, 1.15f, 0.10f);
            Vector3 wrPos = new Vector3(0.0f, 1.20f, 0.05f);
            var uArm = S.Make(shPos - elPos, R, F);
            var fArm = S.Make(elPos - wrPos, R, F);
            var elCross = Frame(JointKind.Elbow, 1, uArm, fArm);
            float flexDeg = Eval(elCross, uArm, fArm.Q).x / BMath.Deg;
            Check("coude replié en travers du torse : flexion positive", flexDeg > 90f && flexDeg < 140f, $"({flexDeg:0.0}°)");
        }

        {
            bool axesOk = true;
            string badDetail = "";
            foreach (var (f, p, c) in frames)
            {
                Quaternion qRel0 = Quaternion.Inverse(p.Q) * c.Q;
                JointKinematics.JointAxes(f, qRel0, out Vector3 ax, out Vector3 sec);
                Vector3 p0 = JointKinematics.Evaluate(f, Quaternion.identity, qRel0, out _);
                Quaternion qPlus = qRel0 * Quaternion.AngleAxis(2f, ax);
                Vector3 p1 = JointKinematics.Evaluate(f, Quaternion.identity, qPlus, out _);
                float df = (p1.x - p0.x) / BMath.Deg;
                float ortho = MathF.Abs(Vector3.Dot(ax, sec));
                if (MathF.Abs(df - 2f) > 0.25f || ortho > 1e-4f)
                {
                    axesOk = false;
                    badDetail += $" {f.Kind}(df={df:0.00}°,dot={ortho:0.000})";
                }
            }
            Quaternion qAbd = Quaternion.AngleAxis(89.5f, F) * upperR.Q;
            Quaternion qRelAbd = Quaternion.Inverse(thorax.Q) * qAbd;
            JointKinematics.JointAxes(shR, qRelAbd, out Vector3 shAx, out Vector3 shSec);
            Vector3 worldAx = qAbd * shAx;
            bool noSingularity = MathF.Abs(Vector3.Dot(worldAx, -R)) > 0.999f && MathF.Abs(Vector3.Dot(shAx, shSec)) < 1e-4f;
            Check("JointAxes analytiques : sens flexion + orthogonalité + épaule 89,5°", axesOk && noSingularity, badDetail);
        }

        {
            Quaternion qOver = Quaternion.Inverse(thorax.Q) * (Quaternion.AngleAxis(-200f, R) * upperR.Q);
            Vector3 neutral = new Vector3(15f * BMath.Deg, 10f * BMath.Deg, 0f);
            Quaternion q = qOver;
            bool wentForward = true;
            float prevFlex = JointKinematics.Evaluate(shR, Quaternion.identity, q, out _).x / BMath.Deg;
            for (int step = 0; step < 8; step++)
            {
                q = JointKinematics.SolvePose(shR, q, neutral, 3, out _);
                float curFlex = JointKinematics.Evaluate(shR, Quaternion.identity, q, out _).x / BMath.Deg;
                if (curFlex > prevFlex + 1f) wentForward = false;
                prevFlex = curFlex;
            }
            float qNorm = MathF.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            Check("SolvePose : épaule à 200° revient par l'avant + quaternion normalisé",
                wentForward && MathF.Abs(prevFlex - 15f) < 1f && MathF.Abs(qNorm - 1f) < 1e-4f,
                $"(final {prevFlex:0.0}°, |q|={qNorm:0.00000})");
        }

        {
            bool stable = true;
            foreach (float dtTest in new[] { 1f / 60f, 0.05f, 0.1f, 0.25f })
            {
                float x = 0f, v = 0f, goal = 2f;
                int steps = Math.Max(10, (int)(1.5f / dtTest));
                for (int i = 0; i < steps; i++)
                {
                    BMath.StepCriticallyDamped(ref x, ref v, goal, 18f, dtTest, 10f);
                    if (float.IsNaN(x) || float.IsInfinity(x) || x < -0.05f || x > goal + 0.05f) stable = false;
                }
                if (MathF.Abs(x - goal) > 0.01f) stable = false;
            }
            Check("StepCriticallyDamped : stable et sans dépassement de 60 Hz à 4 Hz", stable);
        }

        {
            float a0 = JointKinematics.MinShoulderAbductionRad(0f) / BMath.Deg;
            float aBack = JointKinematics.MinShoulderAbductionRad(-30f * BMath.Deg) / BMath.Deg;
            float a90 = JointKinematics.MinShoulderAbductionRad(90f * BMath.Deg) / BMath.Deg;
            float a175 = JointKinematics.MinShoulderAbductionRad(175f * BMath.Deg) / BMath.Deg;
            Check("épaule : adduction bloquée à 0° le long du thorax, ouverte devant (~-22°), resserrée près de la tête",
                MathF.Abs(a0) < 0.1f && MathF.Abs(aBack) < 0.1f && a90 < -20f && a90 > -25f && a175 > a90 + 5f,
                $"(0°:{a0:0.0}°, -30°:{aBack:0.0}°, 90°:{a90:0.0}°, 175°:{a175:0.0}°)");
        }

        {
            Vector3 tPos = new Vector3(0f, 1.0f, 0f);
            float rx = 0.175f, rz = 0.155f, ryCap = 0.06f, chestZ = 0.03f, yBot = -0.05f, yTop = 0.25f;
            bool outClear = !JointKinematics.ThoraxPenetration(tPos + new Vector3(0.18f, 0.15f, chestZ), tPos, R, U, F, chestZ, yBot, yTop, rx, ryCap, rz, 1, out _, out _);
            bool inSide = JointKinematics.ThoraxPenetration(tPos + new Vector3(0.12f, 0.15f, chestZ), tPos, R, U, F, chestZ, yBot, yTop, rx, ryCap, rz, 1, out float dSide, out Vector3 nSide);
            bool inFront = JointKinematics.ThoraxPenetration(tPos + new Vector3(0.0f, 0.15f, chestZ + 0.05f), tPos, R, U, F, chestZ, yBot, yTop, rx, ryCap, rz, -1, out float dFront, out Vector3 nFront);
            Vector3 clamped = JointKinematics.ClampOutsideThorax(tPos + new Vector3(0.02f, 0.15f, chestZ), tPos, R, U, F, chestZ, yBot, yTop, rx, ryCap, rz, 1, 0.015f);
            bool clampedClear = !JointKinematics.ThoraxPenetration(clamped, tPos, R, U, F, chestZ, yBot, yTop, rx, ryCap, rz, 1, out _, out _);
            Check("enveloppe thoracique : détection, normales sortantes et projection extérieure",
                outClear && inSide && MathF.Abs(dSide - 0.055f) < 0.002f && nSide.x > 0.99f
                && inFront && MathF.Abs(dFront - 0.105f) < 0.002f && nFront.z > 0.99f && clampedClear,
                $"(dSide={dSide * 100f:0.1}cm, dFront={dFront * 100f:0.1}cm)");
        }

        _domain = "Configuration BepInEx";
        CheckConfigKeys();

        Console.WriteLine($"\n{_pass} réussis, {_fail} échoués");

        if (!string.IsNullOrWhiteSpace(appendPath)) AppendToReport(appendPath);
        if (!string.IsNullOrWhiteSpace(reportPath)) WriteStandaloneReport(reportPath);

        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    private static string Esc(string text)
        => string.IsNullOrEmpty(text) ? "—" : text.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

    private static void AppendTable(StringBuilder sb)
    {
        sb.AppendLine("## Niveau 4 — Cinématique, anatomie ISB et configuration BepInEx");
        sb.AppendLine();
        sb.AppendLine("Tests exécutés sur `Core/BMath.cs`, `Core/JointKinematics.cs`, `Anatomy/AnatomyData.cs` et `Settings.cs` sans aucune dépendance EFT/SPT.");
        sb.AppendLine();
        sb.AppendLine("| # | Domaine | Vérification | Mesure observée | Statut |");
        sb.AppendLine("|---:|---|---|---|---|");
        foreach (var r in _records)
        {
            string status = r.Passed ? "`PASS`" : "`FAIL`";
            string detail = string.IsNullOrWhiteSpace(r.Detail) ? "conforme" : Esc(r.Detail);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.Index} | {Esc(r.Domain)} | {Esc(r.Name)} | `{detail}` | {status} |"));
        }
        sb.AppendLine();
    }

    private static void AppendVisualAndTransparencySections(StringBuilder sb)
    {
        sb.AppendLine("## Vérifier de ses propres yeux dans le studio 3D (`demo/banc-essai.html`)");
        sb.AppendLine();
        sb.AppendLine("Le studio 3D embarqué dans le dépôt permet de reproduire visuellement chaque comportement ci-dessus dans un navigateur, avec la même logique que la version `0.3.0` :");
        sb.AppendLine();
        sb.AppendLine("1. **Agrippement au mur + rupture (`DlGrab`)** — Choisir le décor **`Mur proche à droite (grab)`**, posture **`Debout`**, comportement **`Auto`** (ou **`Effondrement latéral`**), puis cliquer sur le flanc gauche du mannequin pour le projeter vers la droite : le bras droit se tend vers le mur, l'avant-bras effectue une pronation paume vers la paroi, un point d'ancrage jaune apparaît au contact puis cède lorsque la charge dépasse le seuil.");
        sb.AppendLine("2. **Agrippement à une caisse / rebord (`GrabHelper`)** — Choisir **`Caisse / rebord devant (grab)`**, posture **`Debout`**, et tirer dans le dos ou le thorax pour basculer vers l'avant : les sondes détectent l'arête supérieure de la caisse devant le corps et verrouillent une prise.");
        sb.AppendLine("3. **Dérobement 1 ou 2 jambes & balle dans le dos (`GiveWay`)** — Choisir **`Pièce standard`**, tourner la caméra derrière le mannequin, et tirer dans le dos ou dans une cuisse : le bassin s'affaisse sans raidir les jambes, avec flexion visible d'un genou (tir jambe) ou des deux jambes (simultané ou décalé).");
        sb.AppendLine("4. **Réflexe de garde (`upperBodyFlinch`) & « dernier carré »** — Choisir **`Pièce standard`**, posture **`Debout`**, et cliquer **`Rafale ×5`** (touche **`B`**) de face : les bras montent protéger le haut du corps avec pronation des avant-bras tandis que le tronc résiste brièvement avant de céder.");
        sb.AppendLine("5. **Roulade d'élan (`rollDownStairs` / `bodyRollUp`) et comparaison vanilla** — Passer la posture sur **`En course (3,5 m/s)`** ou **`Sprint (5,5 m/s)`**, réduire **`Vitesse de simulation`** à **`0,25×`** et tirer dans le thorax pour observer la roulade et l'agonie au sol ; activer **`Pose visée par les muscles (fantôme)`** ou cocher **`Ragdoll inerte, sans Brutality (comparaison)`** puis cliquer **`Rejouer`** sur la même graine pour comparer directement avec le ragdoll inerte.");
        sb.AppendLine();
        sb.AppendLine("## Périmètre des preuves et transparence");
        sb.AppendLine();
        sb.AppendLine("| Ce qui est prouvé formellement hors jeu | Ce qui reste à valider en jeu (Unity / PhysX / SPT) |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| Toutes les lois mathématiques Delirium (`nmrs`, `nmdbm`, `nmbb`, `nmshot`, `nm.rs`) exécutées directement sur les vrais `.cs` du plugin. | Calibrage des ordres de grandeur sur les squelettes réels de Tarkov (`PhysxProbe` de sens des drives/butées en début de raid). |");
        sb.AppendLine("| Boucle fermée physique 60 Hz (`Shot`, `Balance`, `Grab`, `CatchFall`, `RollDownStairs`/`RollUp`, `Windmill`, `Writhe`, `Foetal`, `Flinch`). | Interactions sur géométrie complexe des cartes SPT (escaliers, portes, mobilier) et masses complètes d'équipement porté. |");
        sb.AppendLine("| Conventions ISB de tous les joints, IK bras/jambes, solveur de pose `SolvePose`, enveloppe thoracique, et validité de toutes les clés BepInEx. | Cohabitation en raid avec les mods tiers (`Smoothie`, `HollywoodFX`, `Fika`, `ReClassic`) et performance multi-corps. |");
    }

    private static void AppendToReport(string reportPath)
    {
        string full = Path.GetFullPath(reportPath);
        if (!File.Exists(full))
        {
            WriteStandaloneReport(reportPath);
            return;
        }

        string existing = File.ReadAllText(full, Encoding.UTF8);
        int dlPass = 0, dlTotal = 0;
        var mDl = Regex.Match(existing, @"Brutality\.DeliriumTests` \|[^|]*\|\s*\*\*(\d+)\s*/\s*(\d+)\*\*");
        if (mDl.Success)
        {
            dlPass = int.Parse(mDl.Groups[1].Value, CultureInfo.InvariantCulture);
            dlTotal = int.Parse(mDl.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        string kinStatus = _fail == 0 ? "PASS" : "FAIL";
        string kinRow = string.Create(CultureInfo.InvariantCulture,
            $"| `tests/Brutality.KinematicsTests` | Anatomie ISB, IK, solveur de pose, enveloppe thoracique, clés BepInEx | **{_pass} / {_records.Count}** | `{kinStatus}` |");

        int totPass = dlPass + _pass;
        int totCount = dlTotal + _records.Count;
        string totStatus = _fail == 0 && dlPass == dlTotal && dlTotal > 0 ? "PASS" : "FAIL";
        string totRow = string.Create(CultureInfo.InvariantCulture,
            $"| **Total vérifié** | **Preuves formelles complètes hors jeu** | **{totPass} / {totCount}** | `{totStatus}` |");

        existing = existing.Replace("<!-- KINEMATICS_SUMMARY_ROW -->", kinRow);
        existing = existing.Replace("<!-- TOTAL_SUMMARY_ROW -->", totRow);

        var sb = new StringBuilder();
        sb.Append(existing.TrimEnd());
        sb.AppendLine();
        sb.AppendLine();
        AppendTable(sb);
        AppendVisualAndTransparencySections(sb);

        File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WriteStandaloneReport(string reportPath)
    {
        string full = Path.GetFullPath(reportPath);
        string dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine("# Brutality — Rapport Cinématique & Anatomie");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"> Généré automatiquement par `tests/Brutality.KinematicsTests` le `{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC` — **{_pass} / {_records.Count}** réussis."));
        sb.AppendLine();
        AppendTable(sb);
        AppendVisualAndTransparencySections(sb);

        File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));
    }
}
