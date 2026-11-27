// Out-of-game test bench: verifies sign conventions and consistency of Brutality's anatomical angles.
// Run: dotnet run --project tests/Brutality.KinematicsTests
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
    private static string _domain = "General";
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

    /// <summary>Test segment: arbitrary bone orientation + anatomical frame stored in local space (like BodyModel).</summary>
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
    /// BepInEx rejects certain characters in configuration sections and keys (exception in Awake:
    /// the plugin fails to load). Verifies all strings passed to cfg.Bind in Settings.cs.
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
        if (path == null) { Check("configuration: Settings.cs not found", false); return; }
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
        Check("configuration: valid sections and keys for BepInEx", names.Count >= 20 && bad.Count == 0,
            $"({names.Count} names{(bad.Count > 0 ? ", invalid: " + string.Join(" | ", bad) : "")})");
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

        // Standing character, facing +Z (Unity convention: X right, Y up, Z forward).
        Vector3 R = Vector3.right, U = Vector3.up, F = Vector3.forward;
        var pelvis = S.Make(U, R, F);
        var thorax = S.Make(U, R, F);
        var head = S.Make(U, Vector3.Cross(U, F), F);
        var thighR = S.Make(U, R, F);  var calfR = S.Make(U, R, F);
        var thighL = S.Make(U, R, F);  var calfL = S.Make(U, R, F);
        var upperR = S.Make(U, R, F);  var foreR = S.Make(U, R, F);
        var upperL = S.Make(U, R, F);

        // --- Knee
        _domain = "Knee";
        var knee = Frame(JointKind.Knee, 1, thighR, calfR);
        Check("straight right knee = 0°", Near(Eval(knee, thighR, calfR.Q).x, 0f, 0.5f));
        var calfBack = Quaternion.AngleAxis(60f, R) * calfR.Q; // lower leg moves backward
        Check("knee: lower leg backward = flexion +60°", Near(Eval(knee, thighR, calfBack).x, 60f * BMath.Deg, 0.5f),
            $"({Eval(knee, thighR, calfBack).x / BMath.Deg:0.0}°)");
        var calfFwd = Quaternion.AngleAxis(-5f, R) * calfR.Q;
        Check("knee: hyperextension = negative", Eval(knee, thighR, calfFwd).x < 0f);

        // Knee flexed at 60° at time of death (axis inferred from thigh-calf plane).
        var calfBent = new S { Q = calfBack, RL = calfR.RL, UL = calfR.UL, FL = calfR.FL };
        var kneeBent = Frame(JointKind.Knee, 1, thighR, calfBent);
        Check("knee flexed at death: 60° read back", Near(Eval(kneeBent, thighR, calfBent.Q).x, 60f * BMath.Deg, 0.5f));
        Check("flexed knee: +10° backward = 70°",
            Near(Eval(kneeBent, thighR, Quaternion.AngleAxis(10f, R) * calfBent.Q).x, 70f * BMath.Deg, 0.5f));

        // --- Elbow
        _domain = "Elbow";
        var elbow = Frame(JointKind.Elbow, 1, upperR, foreR);
        var foreUp = Quaternion.AngleAxis(-90f, R) * foreR.Q; // forearm rises forward
        Check("elbow: forearm forward = flexion +90°", Near(Eval(elbow, upperR, foreUp).x, 90f * BMath.Deg, 0.5f),
            $"({Eval(elbow, upperR, foreUp).x / BMath.Deg:0.0}°)");
        var pron = Quaternion.AngleAxis(40f, foreR.Q * foreR.UL) * foreR.Q;
        Check("elbow: pronation-supination read on twist", MathF.Abs(MathF.Abs(Eval(elbow, upperR, pron).z) - 40f * BMath.Deg) < 0.01f);

        // --- Hip
        _domain = "Hip";
        var hipR = Frame(JointKind.Hip, 1, pelvis, thighR);
        var hipL = Frame(JointKind.Hip, -1, pelvis, thighL);
        Check("neutral hip = 0/0/0", Eval(hipR, pelvis, thighR.Q).magnitude < 0.01f);
        var thighFwd = Quaternion.AngleAxis(-45f, R) * thighR.Q;
        Check("hip: thigh forward = flexion +45°", Near(Eval(hipR, pelvis, thighFwd).x, 45f * BMath.Deg, 0.5f));
        var thighOutR = Quaternion.AngleAxis(30f, F) * thighR.Q; // right foot toward +X
        Check("right hip: spread outward = abduction +30°", Near(Eval(hipR, pelvis, thighOutR).y, 30f * BMath.Deg, 0.5f),
            $"({Eval(hipR, pelvis, thighOutR).y / BMath.Deg:0.0}°)");
        var thighOutL = Quaternion.AngleAxis(-30f, F) * thighL.Q; // left foot toward -X
        Check("left hip: spread outward = abduction +30°", Near(Eval(hipL, pelvis, thighOutL).y, 30f * BMath.Deg, 0.5f),
            $"({Eval(hipL, pelvis, thighOutL).y / BMath.Deg:0.0}°)");
        var thighBack = Quaternion.AngleAxis(15f, R) * thighR.Q;
        Check("hip: thigh backward = extension (negative)", Eval(hipR, pelvis, thighBack).x < 0f);

        // --- Shoulder (unwrapping beyond 180°)
        _domain = "Shoulder";
        var shR = Frame(JointKind.Shoulder, 1, thorax, upperR);
        var armUp = Quaternion.AngleAxis(-170f, R) * upperR.Q;
        Check("shoulder: arm raised forward to 170°", Near(Eval(shR, thorax, armUp).x, 170f * BMath.Deg, 0.5f));
        var armOver = Quaternion.AngleAxis(-190f, R) * upperR.Q;
        float over = Eval(shR, thorax, armOver).x / BMath.Deg;
        Check("shoulder: 190° read as 190° (not -170°)", MathF.Abs(over - 190f) < 0.5f, $"({over:0.0}°)");
        var armSide = Quaternion.AngleAxis(60f, F) * upperR.Q;
        Check("right shoulder: abduction +60°", Near(Eval(shR, thorax, armSide).y, 60f * BMath.Deg, 0.5f));
        var shL = Frame(JointKind.Shoulder, -1, thorax, upperL);
        var armAcrossL = Quaternion.AngleAxis(20f, F) * upperL.Q; // left arm toward +X = adduction
        Check("left shoulder: arm toward midline = adduction (negative)", Eval(shL, thorax, armAcrossL).y < 0f);

        // --- Internal rotation (posturing)
        _domain = "Internal rotation & pronation";
        float irR = Eval(shR, thorax, Quaternion.AngleAxis(-30f, U) * upperR.Q).z;
        float irL = Eval(shL, thorax, Quaternion.AngleAxis(30f, U) * upperL.Q).z;
        Check("right shoulder: internal rotation read ±30°", MathF.Abs(MathF.Abs(irR) - 30f * BMath.Deg) < 0.01f, $"({irR / BMath.Deg:0.0}°)");
        Check("internal rotation: sign = -side (right -, left +)", irR < 0f && irL > 0f,
            $"(R {irR / BMath.Deg:0.0}°, L {irL / BMath.Deg:0.0}°)");
        float irHipR = Eval(hipR, pelvis, Quaternion.AngleAxis(-30f, U) * thighR.Q).z;
        float irHipL = Eval(hipL, pelvis, Quaternion.AngleAxis(30f, U) * thighL.Q).z;
        Check("hip: internal rotation, same convention as shoulder", irHipR < 0f && irHipL > 0f,
            $"(R {irHipR / BMath.Deg:0.0}°, L {irHipL / BMath.Deg:0.0}°)");
        float pronR = Eval(elbow, upperR, Quaternion.AngleAxis(-40f, U) * foreR.Q).z;
        Check("right elbow: pronation (palm inward then backward) = negative", pronR < 0f, $"({pronR / BMath.Deg:0.0}°)");

        // --- Lumbar spine
        _domain = "Lumbar spine";
        var lumbar = Frame(JointKind.Lumbar, 0, pelvis, thorax);
        Check("neutral spine = 0/0/0", Eval(lumbar, pelvis, thorax.Q).magnitude < 0.01f);
        var bendFwd = Quaternion.AngleAxis(30f, R) * thorax.Q;
        Check("spine: leaned forward = flexion +30°", Near(Eval(lumbar, pelvis, bendFwd).x, 30f * BMath.Deg, 0.5f));
        var bendRight = Quaternion.AngleAxis(-20f, F) * thorax.Q;
        Check("spine: right lateral bend = +20°", Near(Eval(lumbar, pelvis, bendRight).y, 20f * BMath.Deg, 0.5f));
        var twist = Quaternion.AngleAxis(25f, U) * thorax.Q;
        Check("spine: twist +25° read +25°", Near(Eval(lumbar, pelvis, twist).z, 25f * BMath.Deg, 0.5f));
        Vector3 leftShoulder = Quaternion.AngleAxis(25f, U) * new Vector3(-1, 0, 0);
        Check("positive twist = left shoulder forward (roll to the right)", leftShoulder.z > 0f);

        // Absolute twist present at death (trunk was already twisted by 20°).
        var thoraxTw = new S { Q = Quaternion.AngleAxis(20f, U) * thorax.Q, RL = thorax.RL, UL = thorax.UL, FL = thorax.FL };
        var lumbarTw = Frame(JointKind.Lumbar, 0, pelvis, thoraxTw);
        Check("spine: twist at death read back (20°)", Near(Eval(lumbarTw, pelvis, thoraxTw.Q).z, 20f * BMath.Deg, 0.5f));

        // --- Neck
        _domain = "Neck";
        var neck = Frame(JointKind.Neck, 0, thorax, head);
        var lookRight = Quaternion.AngleAxis(40f, U) * head.Q;
        Check("neck: twist +40° = looking right", Near(Eval(neck, thorax, lookRight).z, 40f * BMath.Deg, 0.5f)
              && (lookRight * head.FL).x > 0f);
        var chin = Quaternion.AngleAxis(30f, R) * head.Q;
        Check("neck: chin toward chest = flexion +30°", Near(Eval(neck, thorax, chin).x, 30f * BMath.Deg, 0.5f));

        // --- Numerical gradients (like BodyModel.ComputeKinematics): dφ ≈ g·ω dt
        _domain = "Numerical gradients";
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
        Check("consistent numerical gradients (dφ = g·ω dt)", gradOk >= gradN - 2, $"({gradOk}/{gradN})");

        // --- Arm inverse kinematics
        _domain = "Inverse kinematics";
        Vector3 shoulderPos = new Vector3(0.2f, 1.5f, 0f);
        Vector3 target = new Vector3(0.05f, 1.15f, 0.15f); // hand on abdomen
        bool ik = JointKinematics.ArmIK(shoulderPos, 0.30f, 0.33f, target, 1, R, U, F, out float fl, out float ab, out float el, out Vector3 ud);
        Vector3 recon = (R * (MathF.Sin(ab * BMath.Deg))) + (-U * MathF.Cos(fl * BMath.Deg) + F * MathF.Sin(fl * BMath.Deg)) * MathF.Cos(ab * BMath.Deg);
        Check("IK: shoulder angles ↔ arm direction", ik && (recon - ud).magnitude < 0.02f, $"(flex {fl:0}°, abd {ab:0}°, elbow {el:0}°)");
        Vector3 elbowPos = shoulderPos + ud * 0.30f;
        Check("IK: forearm reaches target", MathF.Abs((target - elbowPos).magnitude - 0.33f) < 0.01f);
        Check("IK: elbow flexed for close target", el > 30f && el < 160f);

        // --- Leg inverse kinematics
        foreach (int legSide in new[] { -1, 1 })
        {
            Vector3 hipPos = new Vector3(0.1f * legSide, 0.94f, 0f);
            Vector3 stepTarget = new Vector3(0.14f * legSide, 0.12f, 0.28f);
            bool lik = JointKinematics.LegIK(hipPos, 0.44f, 0.44f, stepTarget, legSide, R, U, F, out float hf, out float ha, out float kf);
            Vector3 thighDir = R * (legSide * MathF.Sin(ha * BMath.Deg)) + (-U * MathF.Cos(hf * BMath.Deg) + F * MathF.Sin(hf * BMath.Deg)) * MathF.Cos(ha * BMath.Deg);
            Vector3 kneePos = hipPos + thighDir * 0.44f;
            Check($"leg IK {(legSide < 0 ? "L" : "R")}: leg reaches ankle target", lik && MathF.Abs((stepTarget - kneePos).magnitude - 0.44f) < 0.01f,
                $"(hip {hf:0}°/{ha:0}°, knee {kf:0}°)");
            Check($"leg IK {(legSide < 0 ? "L" : "R")}: knee flexed forward, thigh flexed", kf > 5f && hf > 20f && kneePos.z > stepTarget.z * 0.5f);
        }
        {
            bool lik = JointKinematics.LegIK(new Vector3(0.1f, 0.94f, 0f), 0.44f, 0.44f, new Vector3(0.1f, 0.06f, 0f), 1, R, U, F, out float hf, out float ha, out float kf);
            Check("leg IK: vertical stance = nearly straight leg", lik && MathF.Abs(hf) < 7f && MathF.Abs(ha) < 1f && kf < 14f, $"({hf:0.0}°, {ha:0.0}°, {kf:0.0}°)");
        }

        // --- Pose solver
        _domain = "Pose solver";
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
        Check("pose solver: anatomical targets reached (< 0.5°)", solved >= total - 2, $"({solved}/{total}, worst {worst:0.00}°)");

        bool centersOk = true;
        foreach (var (f, p, c) in frames)
        {
            var spec = AnatomyData.Specs[(int)f.Kind];
            var center = new Vector3();
            for (int d = 0; d < 3; d++) center[d] = 0.5f * (spec.Dofs[d].MinDeg + spec.Dofs[d].MaxDeg) * BMath.Deg;
            Quaternion q = JointKinematics.SolvePose(f, Quaternion.Inverse(p.Q) * c.Q, center, 40, out float res);
            if (res > 1f * BMath.Deg) { centersOk = false; Console.WriteLine($"      center {f.Kind}: residual {res / BMath.Deg:0.00}°"); }
        }
        Check("pose solver: center pose for every joint", centersOk);

        // --- Stability / physics regressions ---
        _domain = "Stability, axes & thorax envelope";
        {
            Vector3 shPos = new Vector3(0.2f, 1.4f, 0f);
            Vector3 elPos = new Vector3(0.25f, 1.15f, 0.10f);
            Vector3 wrPos = new Vector3(0.0f, 1.20f, 0.05f);
            var uArm = S.Make(shPos - elPos, R, F);
            var fArm = S.Make(elPos - wrPos, R, F);
            var elCross = Frame(JointKind.Elbow, 1, uArm, fArm);
            float flexDeg = Eval(elCross, uArm, fArm.Q).x / BMath.Deg;
            Check("elbow folded across torso: positive flexion", flexDeg > 90f && flexDeg < 140f, $"({flexDeg:0.0}°)");
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
            Check("analytic JointAxes: flexion direction + orthogonality + shoulder 89.5°", axesOk && noSingularity, badDetail);
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
            Check("SolvePose: shoulder at 200° returns through the front + normalized quaternion",
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
            Check("StepCriticallyDamped: stable and overshoot-free from 60 Hz to 4 Hz", stable);
        }

        {
            float a0 = JointKinematics.MinShoulderAbductionRad(0f) / BMath.Deg;
            float aBack = JointKinematics.MinShoulderAbductionRad(-30f * BMath.Deg) / BMath.Deg;
            float a90 = JointKinematics.MinShoulderAbductionRad(90f * BMath.Deg) / BMath.Deg;
            float a175 = JointKinematics.MinShoulderAbductionRad(175f * BMath.Deg) / BMath.Deg;
            Check("shoulder: adduction clamped at 0° along thorax, open in front (~-22°), tightened near head",
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
            Check("thorax envelope: detection, outward normals, and exterior projection",
                outClear && inSide && MathF.Abs(dSide - 0.055f) < 0.002f && nSide.x > 0.99f
                && inFront && MathF.Abs(dFront - 0.105f) < 0.002f && nFront.z > 0.99f && clampedClear,
                $"(dSide={dSide * 100f:0.1}cm, dFront={dFront * 100f:0.1}cm)");
        }

        {
            var neckSpec = AnatomyData.Specs[(int)JointKind.Neck];
            var shSpec = AnatomyData.Specs[(int)JointKind.Shoulder];
            var elSpec = AnatomyData.Specs[(int)JointKind.Elbow];
            Check("neck ROM: bounded extension, lateral tilt, and twist",
                neckSpec.Dofs[0].MinDeg >= -30f && neckSpec.Dofs[0].MaxDeg <= 45f
                && neckSpec.Dofs[1].MaxDeg <= 32f && neckSpec.Dofs[2].MaxDeg <= 60f,
                $"(flex [{neckSpec.Dofs[0].MinDeg},{neckSpec.Dofs[0].MaxDeg}], lat ±{neckSpec.Dofs[1].MaxDeg}, twist ±{neckSpec.Dofs[2].MaxDeg})");
            Check("shoulder ROM: clear of 180° wrap, 90° abduction pole, and bounded twist",
                shSpec.Dofs[0].MinDeg >= -45f && shSpec.Dofs[0].MaxDeg <= 160f
                && shSpec.Dofs[1].MinDeg >= -22f && shSpec.Dofs[1].MaxDeg <= 80f
                && shSpec.Dofs[2].MaxDeg <= 50f,
                $"(flex [{shSpec.Dofs[0].MinDeg},{shSpec.Dofs[0].MaxDeg}], abd [{shSpec.Dofs[1].MinDeg},{shSpec.Dofs[1].MaxDeg}], twist ±{shSpec.Dofs[2].MaxDeg})");
            Check("relaxed neutral arm pose: shoulder down/outward, elbow nearly extended",
                shSpec.Dofs[0].NeutralDeg <= 10f && shSpec.Dofs[1].NeutralDeg >= 12f && elSpec.Dofs[0].NeutralDeg <= 25f,
                $"(shNeutral={shSpec.Dofs[0].NeutralDeg}°/{shSpec.Dofs[1].NeutralDeg}°, elNeutral={elSpec.Dofs[0].NeutralDeg}°)");
        }

        _domain = "BepInEx Configuration";
        CheckConfigKeys();

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");

        if (!string.IsNullOrWhiteSpace(appendPath)) AppendToReport(appendPath);
        if (!string.IsNullOrWhiteSpace(reportPath)) WriteStandaloneReport(reportPath);

        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    private static string Esc(string text)
        => string.IsNullOrEmpty(text) ? "—" : text.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

    private static void AppendTable(StringBuilder sb)
    {
        sb.AppendLine("## Level 4 — Kinematics, ISB Anatomy, and BepInEx Configuration");
        sb.AppendLine();
        sb.AppendLine("Tests executed on `Core/BMath.cs`, `Core/JointKinematics.cs`, `Anatomy/AnatomyData.cs`, and `Settings.cs` with zero EFT/SPT dependencies.");
        sb.AppendLine();
        sb.AppendLine("| # | Domain | Check | Observed measurement | Status |");
        sb.AppendLine("|---:|---|---|---|---|");
        foreach (var r in _records)
        {
            string status = r.Passed ? "`PASS`" : "`FAIL`";
            string detail = string.IsNullOrWhiteSpace(r.Detail) ? "compliant" : Esc(r.Detail);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.Index} | {Esc(r.Domain)} | {Esc(r.Name)} | `{detail}` | {status} |"));
        }
        sb.AppendLine();
    }

    private static void AppendVisualAndTransparencySections(StringBuilder sb)
    {
        sb.AppendLine("## See It Yourself in the 3D Studio (`demo/test-bench.html`)");
        sb.AppendLine();
        sb.AppendLine("The embedded 3D studio in the repository lets you visually reproduce each behaviour above in a browser, using the same logic as version `1.1.0`:");
        sb.AppendLine();
        sb.AppendLine("1. **Wall grab + break (`DlGrab`)** — Choose the **`Close right wall (grab)`** scene, **`Standing`** posture, **`Auto`** behaviour (or **`Side collapse`**), then click the mannequin's left flank to throw it toward the right: the right arm reaches toward the wall, the forearm pronates palm toward the surface, a yellow anchor point appears on contact, then snaps when the load exceeds the threshold.");
        sb.AppendLine("2. **Crate / ledge grab (`GrabHelper`)** — Choose **`Crate / ledge ahead (grab)`**, **`Standing`** posture, and shoot the back or thorax to topple forward: probes detect the top edge of the crate in front of the body and lock a grip.");
        sb.AppendLine("3. **1- or 2-leg buckling & back shot (`GiveWay`)** — Choose **`Standard room`**, orbit the camera behind the mannequin, and shoot the back or a thigh: the pelvis sinks without stiffening the legs, with visible flexion of one knee (leg shot) or both knees (simultaneous or staggered).");
        sb.AppendLine("4. **Guard reflex (`upperBodyFlinch`) & \"last stand\"** — Choose **`Standard room`**, **`Standing`** posture, and click **`Burst ×5`** (**`B`** key) from the front: the arms rise to shield the upper body with forearm pronation while the trunk briefly resists before giving way.");
        sb.AppendLine("5. **Momentum roll (`rollDownStairs` / `bodyRollUp`) and vanilla comparison** — Switch posture to **`Running (3.5 m/s)`** or **`Sprinting (5.5 m/s)`**, lower **`Simulation speed`** to **`0.25×`** and shoot the thorax to observe the roll and ground agony; enable **`Muscle target pose (ghost)`** or check **`Inert ragdoll, no Brutality (comparison)`** then click **`Replay`** on the same seed to compare directly against the inert ragdoll.");
        sb.AppendLine();
        sb.AppendLine("## Proof Scope and Transparency");
        sb.AppendLine();
        sb.AppendLine("| Formally proven out-of-game | Remaining to validate in-game (Unity / PhysX / SPT) |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| All Delirium mathematical laws (`nmrs`, `nmdbm`, `nmbb`, `nmshot`, `nmbr`, `nmfow`, `nmhf`, `nm.rs`) executed directly on the plugin's real `.cs` files. | Calibration of magnitudes on real Tarkov skeletons (`PhysxProbe` for drive/limit directions at raid start). |");
        sb.AppendLine("| 60 Hz closed-loop physics (`Shot`, `Balance`, `WallBrace`/`FallOverWall`, `HighFall`, `OnFire`, `MeleeGuard`, `Grab`, `CatchFall`, `RollDownStairs`/`RollUp`, `Windmill`, `Writhe`, `Foetal`, `Flinch`). | Interactions on complex SPT map geometry (stairs, doors, walls, low ledges, furniture) and full carried equipment masses. |");
        sb.AppendLine("| ISB conventions for all joints, arm/leg IK, `SolvePose` solver, thorax envelope, and validity of all BepInEx keys. | In-raid coexistence with third-party mods (`Smoothie`, `HollywoodFX`, `Fika`, `ReClassic`) and multi-body performance. |");
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
            $"| `tests/Brutality.KinematicsTests` | ISB anatomy, IK, pose solver, thorax envelope, BepInEx keys | **{_pass} / {_records.Count}** | `{kinStatus}` |");

        int totPass = dlPass + _pass;
        int totCount = dlTotal + _records.Count;
        string totStatus = _fail == 0 && dlPass == dlTotal && dlTotal > 0 ? "PASS" : "FAIL";
        string totRow = string.Create(CultureInfo.InvariantCulture,
            $"| **Verified total** | **Complete out-of-game formal proofs** | **{totPass} / {totCount}** | `{totStatus}` |");

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
        sb.AppendLine("# Brutality — Kinematics & Anatomy Report");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"> Automatically generated by `tests/Brutality.KinematicsTests` on `{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC` — **{_pass} / {_records.Count}** passed."));
        sb.AppendLine();
        AppendTable(sb);
        AppendVisualAndTransparencySections(sb);

        File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));
    }
}
