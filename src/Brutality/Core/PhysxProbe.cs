using System;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Brutality.Core
{
    /// <summary>
    /// PhysX probe: measures in-game, once per session, two conventions that cannot be verified outside the engine:
    ///  1. the sign of ConfigurableJoint drives' targetRotation (local-space control formula);
    ///  2. the sign of angular X limits (lowAngularXLimit / highAngularXLimit) relative to a positive rotation
    ///     around the joint axis.
    /// Two pairs of non-colliding bodies are created far below the map, simulated for ~0.5 s, then destroyed.
    /// Until the measurement completes, default conventions are used.
    /// </summary>
    internal static class PhysxProbe
    {
        public static bool? DriveInverted;
        public static bool? LimitXInverted;
        public static bool Done;

        private static GameObject _root;
        private static Rigidbody _anchorA, _driveB, _anchorC, _limitD;
        private static int _steps;
        private static int _attempts;
        private static float _startTime;
        private static float _worldSince;
        private static float _retryAfter;
        private static bool _running;
        private static bool _physicsSeen;

        private const float DriveTargetDeg = 30f;
        private const float LimitLowDeg = -10f, LimitHighDeg = 60f;
        private const int MaxAttempts = 3;
        private static readonly Vector3 Axis = Vector3.right;

        /// <summary>Called every frame by the plugin: starts the probe as soon as a raid is loaded.</summary>
        public static void Tick()
        {
            if (Done) return;
            if (!Singleton<GameWorld>.Instantiated)
            {
                // Return to menu / raid change: re-arm the probe if it has not concluded yet.
                if (_running) Cleanup();
                _attempts = 0;
                _retryAfter = 0f;
                _worldSince = 0f;
                _physicsSeen = false;
                return;
            }
            if (_worldSince <= 0f) _worldSince = Time.time;
            if (!_running)
            {
                if (_attempts >= MaxAttempts || Time.time < _retryAfter) return;
                // Wait for Physics.Simulate to have run (or 2 s after entering raid if no body is simulated).
                if (!_physicsSeen && Time.time - _worldSince < 2f) return;
                try { Begin(); }
                catch (Exception e)
                {
                    _attempts++;
                    _retryAfter = Time.time + 5f;
                    Plugin.Log.LogWarning("Brutality: PhysX probe failed (" + e.Message + "), will retry later.");
                    Cleanup();
                }
                return;
            }
            if (Time.time - _startTime > 10f)
            {
                _attempts++;
                _retryAfter = Time.time + 5f;
                Plugin.Log.LogWarning(_attempts >= MaxAttempts
                    ? "Brutality: PhysX probe was not simulated on this raid, using default conventions (will retry next raid)."
                    : "Brutality: PhysX probe has not been simulated yet, retrying in 5 s.");
                Cleanup();
            }
        }

        /// <summary>Called before each Physics.Simulate.</summary>
        public static void OnStep()
        {
            _physicsSeen = true;
            if (!_running) return;
            try
            {
                // Constant torque in the + axis direction: body D hits the corresponding limit.
                if (_limitD != null) _limitD.AddTorque(Axis * 40f, ForceMode.Acceleration);
                _steps++;
                if (_steps >= 30 && Time.time - _startTime > 0.5f) Finish();
            }
            catch (Exception e)
            {
                _attempts++;
                _retryAfter = Time.time + 5f;
                Plugin.Log.LogWarning("Brutality: PhysX probe interrupted (" + e.Message + ").");
                Cleanup();
            }
        }

        private static Rigidbody MakeBody(string name, Vector3 pos, bool kinematic)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.identity;
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 1f;
            rb.useGravity = false;
            rb.isKinematic = kinematic;
            rb.detectCollisions = false;
            rb.drag = 0f;
            rb.angularDrag = 0.5f;
            rb.maxAngularVelocity = 20f;
            rb.inertiaTensor = new Vector3(0.05f, 0.05f, 0.05f);
            rb.inertiaTensorRotation = Quaternion.identity;
            rb.centerOfMass = Vector3.zero;
            return rb;
        }

        private static ConfigurableJoint MakeJoint(Rigidbody body, Rigidbody anchor)
        {
            var cj = body.gameObject.AddComponent<ConfigurableJoint>();
            cj.autoConfigureConnectedAnchor = false;
            cj.anchor = Vector3.zero;
            cj.axis = Axis;
            cj.secondaryAxis = Vector3.up;
            cj.connectedBody = anchor;
            cj.connectedAnchor = anchor.transform.InverseTransformPoint(body.position);
            cj.xMotion = ConfigurableJointMotion.Locked;
            cj.yMotion = ConfigurableJointMotion.Locked;
            cj.zMotion = ConfigurableJointMotion.Locked;
            cj.angularYMotion = ConfigurableJointMotion.Free;
            cj.angularZMotion = ConfigurableJointMotion.Free;
            cj.enablePreprocessing = false;
            return cj;
        }

        private static void Begin()
        {
            _root = new GameObject("BrutalityPhysxProbe");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Vector3 origin = new Vector3(0f, -4000f, 0f);

            // Pair 1: drive. Rest pose = identity, target = +30° around axis (plugin control formula).
            _anchorA = MakeBody("A", origin, true);
            _driveB = MakeBody("B", origin + Vector3.down * 0.5f, false);
            var drive = MakeJoint(_driveB, _anchorA);
            drive.angularXMotion = ConfigurableJointMotion.Free;
            drive.rotationDriveMode = RotationDriveMode.XYAndZ;
            var jd = new JointDrive { positionSpring = 400f, positionDamper = 40f, maximumForce = 1e6f, useAcceleration = true };
            drive.angularXDrive = jd;
            drive.angularYZDrive = jd;
            drive.targetRotation = DriveTarget(Quaternion.AngleAxis(DriveTargetDeg, Axis), Quaternion.identity, Axis, Vector3.up, false);

            // Pair 2: asymmetric X limit, torque in the + direction.
            _anchorC = MakeBody("C", origin + Vector3.right * 3f, true);
            _limitD = MakeBody("D", origin + Vector3.right * 3f + Vector3.down * 0.5f, false);
            var lim = MakeJoint(_limitD, _anchorC);
            lim.angularXMotion = ConfigurableJointMotion.Limited;
            lim.lowAngularXLimit = new SoftJointLimit { limit = LimitLowDeg };
            lim.highAngularXLimit = new SoftJointLimit { limit = LimitHighDeg };

            // EFT only simulates physics if a "supported" body is active.
            PhysicsExtensions.UpdateController.SupportRigidbody(_driveB, 0f);
            PhysicsExtensions.UpdateController.SupportRigidbody(_limitD, 0f);
            _steps = 0;
            _startTime = Time.time;
            _running = true;
        }

        /// <summary>ConfigurableJoint control formula (parent→child relative rotation).</summary>
        public static Quaternion DriveTarget(Quaternion desiredRel, Quaternion startRel, Vector3 axis, Vector3 secondary, bool inverted)
        {
            Vector3 forward = Vector3.Cross(axis, secondary).normalized;
            Vector3 up = Vector3.Cross(forward, axis).normalized;
            Quaternion w = Quaternion.LookRotation(forward, up);
            Quaternion tr = Quaternion.Inverse(w) * Quaternion.Inverse(desiredRel) * startRel * w;
            return inverted ? Quaternion.Inverse(tr) : tr;
        }

        private static float AngleAboutAxis(Rigidbody anchor, Rigidbody body)
        {
            Quaternion rel = Quaternion.Inverse(anchor.rotation) * body.rotation;
            return BMath.TwistAngle(rel, Axis) * Mathf.Rad2Deg;
        }

        private static void Finish()
        {
            float driveAngle = AngleAboutAxis(_anchorA, _driveB);
            float limitAngle = AngleAboutAxis(_anchorC, _limitD);

            if (Mathf.Abs(driveAngle - DriveTargetDeg) < 10f) DriveInverted = false;
            else if (Mathf.Abs(driveAngle + DriveTargetDeg) < 10f) DriveInverted = true;

            if (limitAngle > 35f) LimitXInverted = false;
            else if (limitAngle > 2f && limitAngle < 25f) LimitXInverted = true;

            Plugin.Log.LogInfo(string.Format(
                "Brutality: PhysX probe — drive {0:0.0}° (target {1}°) → {2}; X limit {3:0.0}° → {4}.",
                driveAngle, DriveTargetDeg, DriveInverted.HasValue ? (DriveInverted.Value ? "inverted" : "normal") : "undetermined",
                limitAngle, LimitXInverted.HasValue ? (LimitXInverted.Value ? "inverted" : "normal") : "undetermined"));
            Done = true;
            Cleanup();
        }

        private static void Cleanup()
        {
            _running = false;
            try
            {
                if (_driveB != null) PhysicsExtensions.UpdateController.UnsupportRigidbody(_driveB);
                if (_limitD != null) PhysicsExtensions.UpdateController.UnsupportRigidbody(_limitD);
            }
            catch { }
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _anchorA = _driveB = _anchorC = _limitD = null;
        }
    }
}
