using System;
using System.Collections.Generic;
using InControl;
using UnityEngine;
using UnityEngine.Rendering;

namespace TunicCamera
{
    public enum CamMode { Default, Orbit, Chase, FirstPerson }

    /// <summary>
    /// Runs after the game's CameraController.Update each frame (LateUpdate) and, in a custom mode,
    /// overwrites the camera's pose and projection. The game keeps simulating its own camera underneath,
    /// so switching back to Default resumes vanilla behaviour seamlessly.
    /// </summary>
    public class CameraModeController : MonoBehaviour
    {
        public CameraModeController(IntPtr ptr) : base(ptr) { }

        // ---- tuning that isn't worth a config entry ----
        const float TapMaxSeconds = 0.35f;     // R3 shorter than this = cycle
        const float ResetHoldSeconds = 1.0f;   // R3 held this long without stick = back to Default
        const float StickDeadzone = 0.2f;
        const float FirstPersonSmoothTime = 0.04f;
        const float CollisionRadius = 0.25f;
        const float CollisionOutTime = 0.3f;
        const float LockTurnTime = 0.15f;      // how fast cameras swing toward an LT target
        const float FacingTurnTime = 0.12f;    // how fast LT with no target swings the camera behind the fox
        const float HideRadius = 1.2f;         // FirstPerson hides player renderers within this of the fox

        // ---- mode state ----
        CamMode[] cycle = { CamMode.Default };
        int cycleIndex;
        CamMode mode = CamMode.Default;
        bool active;            // currently overriding the camera
        bool modeNeedsInit = true;

        float yaw, pitch, distance, fov;
        float yawVel, pitchVel, collisionDist, collisionVel;
        Vector3 pivot, pivotVel, lastPlayerPos;
        float lastLookInputTime = -999f;

        // ---- input state ----
        float r3DownTime;
        bool r3Held, r3UsedForZoom, r3ResetFired;
        Vector2 lookInput;      // degrees this frame
        float zoomInput;        // -1..1 while R3 is held
        bool mouseAxesBroken;

        // ---- game pose tracking ----
        Vector3 gamePos, lastWrittenPos;
        Quaternion gameRot, lastWrittenRot;

        // ---- saved state to restore on exit ----
        struct CamState { public Camera cam; public bool ortho; public float fov, near, far; }
        readonly List<CamState> savedCams = new List<CamState>();
        bool savedDof, haveSavedEffects;
        bool maskSuppressed, maskBeforeSuppress;
        IntPtr lastPlayerPtr, lastCamPtr;
        readonly StereoRenderer stereo = new StereoRenderer();
        struct HiddenRenderer { public Renderer r; public ShadowCastingMode original; }
        readonly Dictionary<IntPtr, HiddenRenderer> hidden = new Dictionary<IntPtr, HiddenRenderer>();
        float nextHideScan;
        Rigidbody interpBody; RigidbodyInterpolation savedInterp; bool haveSavedInterp;

        // ---- UI / diagnostics ----
        string banner; float bannerUntil;
        GUIStyle bannerStyle; bool guiBroken;
        bool bindingsLogged, r3StripBroken;
        float nextR3Strip;

        void Start()
        {
            cycle = ParseCycle(Plugin.ModeCycle.Value);
            Plugin.Logger.LogInfo("Mode cycle: " + string.Join(" -> ", cycle));
        }

        static CamMode[] ParseCycle(string s)
        {
            var list = new List<CamMode> { };
            foreach (var part in s.Split(','))
            {
                if (Enum.TryParse(part.Trim(), true, out CamMode m) && !list.Contains(m)) list.Add(m);
                else if (part.Trim().Length > 0) Plugin.Logger.LogWarning($"Ignoring unknown camera mode '{part.Trim()}' in ModeCycle.");
            }
            if (!list.Contains(CamMode.Default)) list.Insert(0, CamMode.Default);
            return list.ToArray();
        }

        // =====================================================================================
        // Input
        // =====================================================================================

        void Update()
        {
            if (!bindingsLogged) TryLogBindings();
            if (Time.unscaledTime >= nextR3Strip) StripR3Bindings();

            lookInput = Vector2.zero;
            zoomInput = 0f;
            bool paused = Time.timeScale <= 0f;

            if (Input.GetKeyDown(KeyCode.F9)) { LogDiagnostics(); LogViewBlockers(); LogPostEffects(); }
            if (Input.GetKeyDown(Plugin.Stereo3DKey.Value)) Plugin.Stereo3D.Value = !Plugin.Stereo3D.Value;
            if (!paused && Input.GetKeyDown(Plugin.CycleKey.Value))
                Cycle(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? -1 : 1);

            InputDevice device = null;
            try { device = InputManager.ActiveDevice; } catch { }
            if (device == null || paused) { r3Held = false; return; }

            Vector2 stick = device.RightStick.Vector;
            var r3 = device.RightStickButton;

            if (r3.WasPressed)
            {
                r3Held = true; r3DownTime = Time.unscaledTime; r3UsedForZoom = false; r3ResetFired = false;
            }
            if (r3Held && r3.IsPressed)
            {
                if (Mathf.Abs(stick.y) > StickDeadzone) { zoomInput = stick.y; r3UsedForZoom = true; }
                if (!r3UsedForZoom && !r3ResetFired && Time.unscaledTime - r3DownTime >= ResetHoldSeconds)
                {
                    r3ResetFired = true;
                    SetMode(0);
                }
            }
            else if (r3Held)
            {
                r3Held = false;
                if (!r3UsedForZoom && !r3ResetFired && Time.unscaledTime - r3DownTime < TapMaxSeconds) Cycle(1);
            }

            // Right stick looks around, unless R3 is held (then it zooms).
            if (!r3Held && stick.magnitude > StickDeadzone)
            {
                float inv = Plugin.InvertY.Value ? 1f : -1f;
                lookInput.x += stick.x * Plugin.StickYawSpeed.Value * Time.deltaTime;
                lookInput.y += stick.y * inv * Plugin.StickPitchSpeed.Value * Time.deltaTime;
            }

            // Mouse look while holding right mouse button.
            if (!mouseAxesBroken && Input.GetMouseButton(1))
            {
                try
                {
                    float inv = Plugin.InvertY.Value ? 1f : -1f;
                    lookInput.x += Input.GetAxis("Mouse X") * Plugin.MouseSensitivity.Value;
                    lookInput.y += Input.GetAxis("Mouse Y") * inv * Plugin.MouseSensitivity.Value;
                    zoomInput += Input.GetAxis("Mouse ScrollWheel") * 10f;
                }
                catch (Exception e)
                {
                    mouseAxesBroken = true;
                    Plugin.Logger.LogWarning("Mouse look unavailable (input axes missing): " + e.Message);
                }
            }

            if (lookInput != Vector2.zero) lastLookInputTime = Time.time;
        }

        void Cycle(int dir)
        {
            int n = cycle.Length;
            SetMode(((cycleIndex + dir) % n + n) % n);
        }

        void SetMode(int index)
        {
            cycleIndex = index;
            var next = cycle[index];
            if (next == mode) return;
            if (mode == CamMode.FirstPerson) UnhidePlayer();
            mode = next;
            modeNeedsInit = true;
            pendingTransition = Plugin.TransitionTime.Value > 0f;
            banner = "Camera: " + DisplayName(next); bannerUntil = Time.unscaledTime + 1.5f;
            Plugin.Logger.LogInfo("Camera mode -> " + DisplayName(next));
        }

        /// <summary>On-screen names. Config files keep the short enum names (Orbit, Chase, FirstPerson).</summary>
        internal static string DisplayName(CamMode m)
        {
            switch (m)
            {
                case CamMode.Orbit: return "3rd Person (Orbit)";
                case CamMode.Chase: return "3rd Person (Chase)";
                case CamMode.FirstPerson: return "1st Person";
                default: return m.ToString();
            }
        }

        // =====================================================================================
        // Camera
        // =====================================================================================

        // The post-processing reads the see-through-walls mask texture even when the mask is switched off. In a newly
        // loaded area that texture holds garbage (a muddy brown screen) until the mask camera has drawn into it, so
        // the mask is only switched off after it has run for a few frames in the new area.
        const int MaskGraceFrames = 10;
        int maskFrames;

        void LateUpdate()
        {
            CameraController cc = null; Camera cam = null; PlayerCharacter player = null;
            try
            {
                cc = CameraController.instance;
                if (cc != null) cam = cc.cachedCamera;
                player = PlayerCharacter.instance;
            }
            catch { }

            // A scene change replaces the player (and often the camera rig). Drop everything tied to the old ones.
            IntPtr playerPtr = IntPtr.Zero, camPtr = IntPtr.Zero;
            try { if (player != null) playerPtr = player.Pointer; if (cam != null) camPtr = cam.Pointer; } catch { }
            if (playerPtr != lastPlayerPtr || camPtr != lastCamPtr)
            {
                Plugin.Logger.LogInfo($"Scene objects changed (player {(playerPtr == IntPtr.Zero ? "none" : "new")}, camera {(camPtr == IntPtr.Zero ? "none" : "new")}); resetting.");
                lastPlayerPtr = playerPtr; lastCamPtr = camPtr;
                stereo.Stop();
                maskFrames = 0;
                if (active) Exit();
                UnhidePlayer();
                ShowFogPlanes();
                RestoreInterpolation();
                maskSuppressed = false;
                RestoreMaskEffect();
                sceneChangeTime = Time.unscaledTime;
            }

            UpdateCamera(cc, cam, player);

            bool stereoOn = Plugin.Stereo3D.Value && cc != null && cam != null;
            if (cc != null && cam != null && player != null && maskFrames < MaskGraceFrames) maskFrames++;
            // Not for 3D: the eyes converge on the player, so the game's single see-through-walls silhouette lines up in both.
            SuppressMask(cc, active && Plugin.DisableOcclusionMask.Value);
            Vector3? focus = null;
            try { if (player != null) focus = player.transform.position + Vector3.up * 0.6f; } catch { }
            stereo.Frame(stereoOn ? cc : null, stereoOn ? AllCameras(cc, cam) : null, focus);
        }

        /// <summary>
        /// The game's see-through-walls mask is aligned to the vanilla camera, so custom modes turn it off (3D leaves it
        /// on: the eyes converge on the player, where the mask is drawn).
        /// </summary>
        DrawCameraMask maskEffect;   // the mask image effect we switched off, to switch back on

        void RestoreMaskEffect()
        {
            try { if (maskEffect != null) maskEffect.enabled = true; } catch { }
            maskEffect = null;
        }

        void SuppressMask(CameraController cc, bool want)
        {
            try
            {
                if (cc == null) { maskSuppressed = false; RestoreMaskEffect(); return; }
                if (maskFrames < MaskGraceFrames) want = false;
                if (want && !maskSuppressed) { maskBeforeSuppress = cc.useMask; maskSuppressed = true; }
                if (want)
                {
                    cc.useMask = false;
                    // The mask image effect keeps drawing even with the mask off, using a mask lined up with the
                    // vanilla camera: dark layers with holes from other angles (e.g. the catacombs). Turn it off too.
                    var fx = DrawCameraMask.instance;
                    if (fx != null && fx.enabled)
                    {
                        RestoreMaskEffect();
                        fx.enabled = false;
                        maskEffect = fx;
                    }
                }
                else if (maskSuppressed) { cc.useMask = maskBeforeSuppress; maskSuppressed = false; RestoreMaskEffect(); }
            }
            catch { }
        }

        void UpdateCamera(CameraController cc, Camera cam, PlayerCharacter player)
        {
            bool canRun = cc != null && cam != null && player != null && !cc.cutsceneFocusMode;
            // Default is the game's camera, but while gliding back to it we still drive the camera.
            bool want = canRun && (mode != CamMode.Default || (active && (transitioning || pendingTransition)));
            if (!want)
            {
                transitioning = false; pendingTransition = false;
                if (active) Exit();
                return;
            }

            try
            {
                bool wasActive = active;
                if (!active) Enter(cc, cam);
                EnableInterpolation(player);   // cheap no-op once set; re-applies if the player object is replaced
                if (Time.unscaledTime >= nextFogScan) HideFogPlanesNow();

                // What did the game want this frame? If the transform still holds exactly what we wrote last
                // frame, the game didn't move it, so keep using the last real game pose.
                var t = cam.transform;
                if (t.position != lastWrittenPos || t.rotation != lastWrittenRot) { gamePos = t.position; gameRot = t.rotation; }

                Transform pt = player.transform;
                Vector3 feet = pt.position;
                if (modeNeedsInit) InitMode(cam, pt);
                if (pendingTransition) { pendingTransition = false; BeginTransition(wasActive, cam); }
                float tp = transitioning ? Mathf.Clamp01((Time.unscaledTime - transStart) / Mathf.Max(Plugin.TransitionTime.Value, 0.01f)) : 1f;

                Vector3 pos; Quaternion rot; float near = 0.1f, far = 1000f;
                Vector3? lockOn = LockTargetPosition(player);

                switch (mode)
                {
                    case CamMode.Orbit:
                    case CamMode.Chase:
                    {
                        bool chase = mode == CamMode.Chase;
                        distance = Mathf.Clamp(distance * Mathf.Exp(-zoomInput * 1.5f * Time.deltaTime), 1.5f, 30f);
                        float minPitch = chase ? -5f : -15f, maxPitch = chase ? 60f : 85f;

                        // Follow the (interpolated) player rigidly: any smoothing here lags a target that only
                        // moves on physics ticks, which makes the fox judder on screen.
                        pivot = feet + Vector3.up * Plugin.PivotHeight.Value;

                        if (lockOn is Vector3 tgt)
                        {
                            // Swing behind the player to face the target, and tilt just enough to keep it in frame.
                            Vector3 to = tgt - pivot;
                            float horiz = new Vector2(to.x, to.z).magnitude;
                            if (horiz > 0.3f)
                                yaw = Mathf.SmoothDampAngle(yaw, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, ref yawVel, LockTurnTime);
                            float elev = Mathf.Atan2(to.y, Mathf.Max(horiz, 0.01f)) * Mathf.Rad2Deg;
                            float slack = (chase ? Plugin.ChaseFov.Value : Plugin.OrbitFov.Value) * 0.3f;
                            float wantPitch = Mathf.Clamp(pitch, -elev - slack, -elev + slack);
                            pitch = Mathf.SmoothDamp(pitch, Mathf.Clamp(wantPitch, minPitch, maxPitch), ref pitchVel, LockTurnTime);
                        }
                        else if (FocusHeld())
                        {
                            // LT with nothing to target: swing behind the way the fox is facing and hold there
                            // (like Z-targeting in Ocarina of Time). The fox strafes while focused, so this stays put.
                            yaw = Mathf.SmoothDampAngle(yaw, pt.eulerAngles.y, ref yawVel, FacingTurnTime);
                            float rest = chase ? Plugin.ChasePitch.Value : Plugin.OrbitPitch.Value;
                            pitch = Mathf.SmoothDamp(pitch, Mathf.Clamp(rest, minPitch, maxPitch), ref pitchVel, FacingTurnTime);
                        }
                        else
                        {
                            yaw += lookInput.x;
                            pitch = Mathf.Clamp(pitch + lookInput.y, minPitch, maxPitch);
                            pitchVel = 0f;
                        }

                        if (chase && lockOn == null && !FocusHeld())
                        {
                            Vector3 step = feet - lastPlayerPos; step.y = 0f;
                            float speed = step.magnitude / Mathf.Max(Time.deltaTime, 1e-4f);
                            if (Time.time - lastLookInputTime > 0.6f && speed > 0.5f)
                                yaw = Mathf.SmoothDampAngle(yaw, pt.eulerAngles.y, ref yawVel, Plugin.ChaseFollowTime.Value);
                        }

                        rot = Quaternion.Euler(pitch, yaw, 0f);
                        Vector3 back = rot * Vector3.back;
                        float d = Plugin.CameraCollision.Value ? Collide(pivot, back, distance, pt) : distance;
                        // Snap in front of walls immediately; ease back out.
                        collisionDist = d < collisionDist ? d : Mathf.SmoothDamp(collisionDist, d, ref collisionVel, CollisionOutTime);
                        pos = pivot + back * collisionDist;
                        near = 0.1f;
                        break;
                    }
                    case CamMode.FirstPerson:
                    {
                        fov = Mathf.Clamp(fov - zoomInput * 40f * Time.deltaTime, 30f, 110f);
                        Vector3 eye = HeadPosition(player, feet);
                        pivot = Vector3.SmoothDamp(pivot, eye, ref pivotVel, FirstPersonSmoothTime);
                        if (lockOn is Vector3 tgt)
                        {
                            Vector3 to = tgt - pivot;
                            float horiz = new Vector2(to.x, to.z).magnitude;
                            if (horiz > 0.1f)
                                yaw = Mathf.SmoothDampAngle(yaw, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, ref yawVel, LockTurnTime);
                            float wantPitch = Mathf.Clamp(-Mathf.Atan2(to.y, Mathf.Max(horiz, 0.01f)) * Mathf.Rad2Deg, -75f, 80f);
                            pitch = Mathf.SmoothDamp(pitch, wantPitch, ref pitchVel, LockTurnTime);
                        }
                        else
                        {
                            yaw += lookInput.x;
                            pitch = Mathf.Clamp(pitch + lookInput.y, -75f, 80f);
                            pitchVel = 0f;
                        }
                        rot = Quaternion.Euler(pitch, yaw, 0f);
                        pos = pivot;
                        near = 0.03f;
                        // Hide the fox only once the camera has nearly arrived in its head.
                        if (Time.unscaledTime >= nextHideScan && tp > 0.85f) HidePlayer(pt);
                        break;
                    }
                    case CamMode.Default:
                        // Gliding back to the game's camera: aim at wherever the game has it now.
                        pos = gamePos; rot = gameRot;
                        break;
                    default:
                        throw new InvalidOperationException("Unhandled camera mode " + mode);
                }

                lastPlayerPos = feet;

                float outFov = mode == CamMode.Orbit ? Plugin.OrbitFov.Value
                             : mode == CamMode.Chase ? Plugin.ChaseFov.Value
                             : fov;

                if (transitioning)
                {
                    if (tp >= 1f)
                    {
                        transitioning = false;
                        if (mode == CamMode.Default) { Exit(); return; }
                    }
                    else
                    {
                        var target = mode == CamMode.Default
                            ? GamePose(cam)
                            : new CamPose { pos = pos, rot = rot, fov = outFov, near = near, far = far };
                        float s = tp * tp * (3f - 2f * tp);   // ease in and out
                        BlendPoses(transFrom, target, s, feet + Vector3.up * Plugin.PivotHeight.Value,
                                   out pos, out rot, out outFov, out near, out far);
                    }
                }
                else if (mode == CamMode.Default) { Exit(); return; }

                Apply(cc, cam, pos, rot, outFov, near, far);
                lastApplied = new CamPose { pos = pos, rot = rot, fov = outFov, near = near, far = far };
                haveLastApplied = true;

                // Movement direction follows the camera in rotating modes; vanilla heading otherwise.
                CameraPatches.OverrideYaw = rot.eulerAngles.y;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Camera mode {mode} failed, returning to Default: {e}");
                SetMode(0);
                Exit();
            }
        }

        void InitMode(Camera cam, Transform pt)
        {
            modeNeedsInit = false;
            Vector3 e = gameRot.eulerAngles;
            Vector3 feet = pt.position;
            yawVel = 0f; pitchVel = 0f; pivotVel = Vector3.zero; collisionVel = 0f;
            lastPlayerPos = feet;

            switch (mode)
            {
                case CamMode.Orbit:
                    yaw = e.y; pitch = Plugin.OrbitPitch.Value; distance = Plugin.OrbitDistance.Value;
                    pivot = feet + Vector3.up * Plugin.PivotHeight.Value; collisionDist = distance; break;
                case CamMode.Chase:
                    yaw = pt.eulerAngles.y; pitch = Plugin.ChasePitch.Value; distance = Plugin.ChaseDistance.Value;
                    pivot = feet + Vector3.up * Plugin.PivotHeight.Value; collisionDist = distance; break;
                case CamMode.FirstPerson:
                    yaw = pt.eulerAngles.y; pitch = 5f; fov = Plugin.FirstPersonFov.Value;
                    pivot = feet + Vector3.up * Plugin.FirstPersonEyeHeight.Value;
                    nextHideScan = 0f; break;
            }
        }

        // ---- mode transitions ----
        // A camera pose is described around a focus point (where the view axis passes the player): the half-height of
        // the view there, the field of view, and how much is visible in front of / behind it. Blending those (rather
        // than raw positions) keeps the player framed throughout, and lets the game's orthographic camera take part:
        // it's matched by a very narrow perspective view from far away, which widens and moves in.
        struct CamPose { public Vector3 pos; public Quaternion rot; public float fov, near, far, orthoSize; public bool ortho; }
        const float FakeOrthoFov = 3f;
        bool pendingTransition, transitioning, haveLastApplied;
        float transStart;
        CamPose transFrom, lastApplied;

        void BeginTransition(bool wasActive, Camera cam)
        {
            transFrom = wasActive && haveLastApplied ? lastApplied : GamePose(cam);
            transStart = Time.unscaledTime;
            transitioning = true;
        }

        /// <summary>The game's own camera pose, as it would be with the mod off.</summary>
        CamPose GamePose(Camera cam)
        {
            var p = new CamPose { pos = gamePos, rot = gameRot, fov = cam.fieldOfView, near = cam.nearClipPlane, far = cam.farClipPlane, orthoSize = cam.orthographicSize };
            if (savedCams.Count > 0)
            {
                var c = savedCams[0];   // the main camera's real settings, saved before we took over
                p.ortho = c.ortho; p.fov = c.fov; p.near = c.near; p.far = c.far;
            }
            else p.ortho = cam.orthographic;
            return p;
        }

        static void Describe(CamPose p, Vector3 focusRef, out Vector3 focus, out float halfHeight, out float fovEff, out float frontSlack, out float backSlack)
        {
            Vector3 fwd = p.rot * Vector3.forward;
            float d = Mathf.Max(0f, Vector3.Dot(focusRef - p.pos, fwd));
            focus = p.pos + fwd * d;
            fovEff = p.ortho ? FakeOrthoFov : Mathf.Clamp(p.fov, 1f, 170f);
            halfHeight = p.ortho ? p.orthoSize : d * Mathf.Tan(fovEff * 0.5f * Mathf.Deg2Rad);
            frontSlack = d - p.near;   // visible depth in front of the focus
            backSlack = p.far - d;     // and behind it
        }

        static void BlendPoses(CamPose a, CamPose b, float s, Vector3 focusRef,
                               out Vector3 pos, out Quaternion rot, out float fieldOfView, out float near, out float far)
        {
            Describe(a, focusRef, out var fa, out var ha, out var va, out var nfa, out var bka);
            Describe(b, focusRef, out var fb, out var hb, out var vb, out var nfb, out var bkb);
            rot = Quaternion.Slerp(a.rot, b.rot, s);
            fieldOfView = Mathf.Exp(Mathf.Lerp(Mathf.Log(va), Mathf.Log(vb), s));   // even feel across narrow and wide
            float h = Mathf.Lerp(ha, hb, s);
            float dist = h / Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            pos = Vector3.Lerp(fa, fb, s) - rot * Vector3.forward * dist;
            near = Mathf.Max(0.03f, dist - Mathf.Lerp(nfa, nfb, s));
            far = Mathf.Max(near + 1f, dist + Mathf.Lerp(bka, bkb, s));
        }

        /// <summary>Is the player focusing (LT held, or focus toggled on), with or without a target?</summary>
        static bool FocusHeld()
        {
            try { if (PlayerCharacter.StrafeTargetting) return true; } catch { }
            try
            {
                var set = PlayerInput.Actions;
                if (set != null && set.Focus != null && set.Focus.IsPressed) return true;
            }
            catch { }
            return false;
        }

        /// <summary>World position of the LT-locked target (enemy or grapple point), or null if not locked on.</summary>
        static Vector3? LockTargetPosition(PlayerCharacter player)
        {
            try
            {
                var zt = player.zTarget;
                if (zt == null || !zt.isActiveAndEnabled) return null;
                return zt.WorldPosition;
            }
            catch { return null; }
        }

        Vector3 HeadPosition(PlayerCharacter player, Vector3 feet)
        {
            try
            {
                var bone = player.headTrackingBone;
                if (bone != null) return bone.transform.position + Vector3.up * 0.08f;
            }
            catch { }
            return feet + Vector3.up * Plugin.FirstPersonEyeHeight.Value;
        }

        float Collide(Vector3 from, Vector3 dir, float maxDist, Transform player)
        {
            float best = maxDist;
            var hits = Physics.SphereCastAll(from, CollisionRadius, dir, maxDist, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.distance <= 0f) continue;                    // started inside: ignore
                var col = h.collider;
                if (col == null || col.transform.IsChildOf(player)) continue;
                if (h.distance < best) best = h.distance;
            }
            return Mathf.Max(best - 0.1f, 0.5f);
        }

        // ---- applying / restoring ----

        void Enter(CameraController cc, Camera cam)
        {
            active = true;
            modeNeedsInit = true;
            savedCams.Clear();
            foreach (var c in AllCameras(cc, cam))
                savedCams.Add(new CamState { cam = c, ortho = c.orthographic, fov = c.fieldOfView, near = c.nearClipPlane, far = c.farClipPlane });

            haveSavedEffects = false;
            try
            {
                var dof = cc.cachedDepthOfField;
                savedDof = dof != null && dof.enabled;
                haveSavedEffects = true;
                if (Plugin.DisableDepthOfField.Value && dof != null) dof.enabled = false;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Couldn't adjust camera effects: " + e.Message); }

            gamePos = cam.transform.position; gameRot = cam.transform.rotation;
            lastWrittenPos = gamePos; lastWrittenRot = gameRot;
        }

        /// <summary>
        /// The game moves the player in FixedUpdate and (vanilla) moves its camera there too, so the two stay in
        /// lockstep. We render a free camera every frame instead, so ask Unity to interpolate the player's
        /// rigidbody between physics ticks; otherwise it only moves on frames that happen to contain a tick.
        /// </summary>
        void EnableInterpolation(PlayerCharacter player)
        {
            if (!Plugin.SmoothPlayerMotion.Value) return;
            try
            {
                var rb = player.cachedRigidbody;
                if (rb == null) return;
                // While the game has time stopped (it does on arrival in an area, while lighting settles) no physics
                // step runs, so an interpolated body keeps snapping the player back to where physics last had it:
                // the game's placement at the arrival point is lost and the fox falls out of the world. So no
                // interpolation then, nor for a moment after an area change.
                if (Time.timeScale <= 0f || Time.unscaledTime < sceneChangeTime + 1.5f) { RestoreInterpolation(); return; }
                if (haveSavedInterp && rb == interpBody)
                {
                    // The game moved the player by its transform (a teleport): make physics agree right away.
                    Vector3 tp = player.transform.position;
                    if ((rb.position - tp).sqrMagnitude > 9f) rb.position = tp;
                    return;
                }
                RestoreInterpolation();
                interpBody = rb; savedInterp = rb.interpolation; haveSavedInterp = true;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Couldn't enable player interpolation: " + e.Message);
                Plugin.SmoothPlayerMotion.Value = false;
            }
        }

        float sceneChangeTime;

        void RestoreInterpolation()
        {
            if (!haveSavedInterp) return;
            try { if (interpBody != null) interpBody.interpolation = savedInterp; } catch { }
            interpBody = null; haveSavedInterp = false;
        }

        void Exit()
        {
            active = false;
            transitioning = false; haveLastApplied = false;
            CameraPatches.OverrideYaw = null;
            ViewportPatches.ClearView();
            UnhidePlayer();
            ShowFogPlanes();
            RestoreInterpolation();
            foreach (var s in savedCams)
            {
                try
                {
                    if (s.cam == null) continue;
                    s.cam.orthographic = s.ortho; s.cam.fieldOfView = s.fov;
                    s.cam.nearClipPlane = s.near; s.cam.farClipPlane = s.far;
                }
                catch { }
            }
            savedCams.Clear();

            try
            {
                var cc = CameraController.instance;
                if (cc != null && haveSavedEffects)
                {
                    var dof = cc.cachedDepthOfField;
                    if (dof != null) dof.enabled = savedDof;
                }
                // Put the vanilla camera straight back where it belongs instead of letting it drift.
                if (cc != null)
                {
                    var t = cc.cachedCamera.transform;
                    t.SetPositionAndRotation(gamePos, gameRot);
                }
            }
            catch { }
            haveSavedEffects = false;
        }

        void Apply(CameraController cc, Camera cam, Vector3 pos, Quaternion rot, float fieldOfView, float near, float far)
        {
            foreach (var c in AllCameras(cc, cam))
            {
                // Child cameras inherit the pose; only move independent ones.
                if (c == cam || !c.transform.IsChildOf(cam.transform)) c.transform.SetPositionAndRotation(pos, rot);
                c.orthographic = false;
                c.fieldOfView = fieldOfView;
                c.nearClipPlane = near;
                c.farClipPlane = far;
            }
            lastWrittenPos = cam.transform.position;
            lastWrittenRot = cam.transform.rotation;
            ViewportPatches.SetView(pos, rot, fieldOfView, cam.aspect);
        }

        IEnumerable<Camera> AllCameras(CameraController cc, Camera main)
        {
            yield return main;
            Camera p = null, fx = null, mask = null;
            try { p = cc.perspectiveCamera; } catch { }
            try { fx = cc.effectsCamera; } catch { }
            try { mask = cc.maskCamera; } catch { }
            if (p != null && p != main) yield return p;
            if (fx != null && fx != main && fx != p) yield return fx;
            if (mask != null && mask != main && mask != p && mask != fx) yield return mask;
        }

        /// <summary>
        /// Hide the fox's own renderers (they still cast shadows). Only renderers physically near the fox are hidden,
        /// so anything parented to the player but drawn elsewhere (e.g. a targeting reticle) stays visible.
        /// Rescanned often so newly equipped items get hidden and things that move away are restored.
        /// </summary>
        // ---- Fog planes ----------------------------------------------------------------------------------------
        // Levels black out the space around rooms with big "black fog plane" sheets (shader Custom/Fogplane) placed for
        // the fixed default camera. From other angles they cover the view, so custom modes switch them off
        // (forceRenderingOff, which doesn't touch the game's own enabled flag).
        readonly List<Renderer> hiddenFog = new List<Renderer>();
        float nextFogScan;

        void HideFogPlanesNow()
        {
            nextFogScan = Time.unscaledTime + 2f;   // rooms stream in; pick up new ones
            if (!Plugin.HideFogPlanes.Value) { ShowFogPlanes(); return; }
            try
            {
                int added = 0;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(UnhollowerRuntimeLib.Il2CppType.Of<Renderer>()))
                {
                    var r = o.Cast<Renderer>();
                    if (r.forceRenderingOff) continue;
                    var m = r.sharedMaterial;
                    if (m == null || m.shader == null || m.shader.name != "Custom/Fogplane") continue;
                    r.forceRenderingOff = true;
                    hiddenFog.Add(r);
                    added++;
                }
                if (added > 0) Plugin.Logger.LogInfo($"Hid {added} fog plane(s) for the custom camera.");
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Fog plane scan failed: " + e.Message); nextFogScan = float.MaxValue; }
        }

        void ShowFogPlanes()
        {
            foreach (var r in hiddenFog)
                try { if (r != null) r.forceRenderingOff = false; } catch { }
            hiddenFog.Clear();
            nextFogScan = 0f;
        }

        void HidePlayer(Transform player)
        {
            nextHideScan = Time.unscaledTime + 0.2f;
            Vector3 center = player.position + Vector3.up * 0.6f;
            var rs = player.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null) continue;
                bool near = (r.bounds.center - center).sqrMagnitude <= HideRadius * HideRadius;
                bool isHidden = hidden.TryGetValue(r.Pointer, out var h);
                if (near && !isHidden)
                {
                    hidden[r.Pointer] = new HiddenRenderer { r = r, original = r.shadowCastingMode };
                    r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;   // invisible, but still casts a shadow
                }
                else if (!near && isHidden)
                {
                    r.shadowCastingMode = h.original;
                    hidden.Remove(r.Pointer);
                }
            }
        }

        void UnhidePlayer()
        {
            foreach (var h in hidden.Values)
            {
                try { if (h.r != null) h.r.shadowCastingMode = h.original; } catch { }
            }
            hidden.Clear();
        }

        // =====================================================================================
        // UI + diagnostics
        // =====================================================================================

        void OnGUI()
        {
            if (guiBroken || !Plugin.ShowModeName.Value || banner == null || Time.unscaledTime > bannerUntil) return;
            try
            {
                if (bannerStyle == null)
                {
                    bannerStyle = new GUIStyle { fontSize = 26, alignment = TextAnchor.UpperCenter };
                    bannerStyle.normal.textColor = Color.white;
                }
                var rect = new Rect(0, Screen.height * 0.06f, Screen.width, 40);
                var shadow = new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height);
                var c = bannerStyle.normal.textColor;
                bannerStyle.normal.textColor = new Color(0, 0, 0, 0.7f);
                GUI.Label(shadow, banner, bannerStyle);
                bannerStyle.normal.textColor = c;
                GUI.Label(rect, banner, bannerStyle);
            }
            catch (Exception e)
            {
                guiBroken = true;
                Plugin.Logger.LogWarning("On-screen mode name disabled: " + e.Message);
            }
        }

        void TryLogBindings()
        {
            PlayerCharacterActionSet set = null;
            try { set = PlayerInput.Actions; } catch { }
            if (set == null) return;
            bindingsLogged = true;
            try
            {
                var actions = set.Actions;
                Plugin.Logger.LogInfo($"Game bindings ({actions.Count} actions):");
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    var names = new List<string>();
                    var bs = a.Bindings;
                    for (int j = 0; j < bs.Count; j++) names.Add(bs[j].Name);
                    Plugin.Logger.LogInfo($"  {a.Name}: {string.Join(", ", names)}");
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Couldn't read game bindings: " + e.Message); }
        }

        /// <summary>
        /// R3 belongs to the camera: remove it from any game action (TUNIC binds it to ToggleFocus by default;
        /// Caps Lock and other bindings stay). Re-run periodically because the game can reload its bindings,
        /// e.g. after the controls menu.
        /// </summary>
        void StripR3Bindings()
        {
            nextR3Strip = Time.unscaledTime + 2f;
            if (!Plugin.TakeOverR3.Value || r3StripBroken) return;
            PlayerCharacterActionSet set = null;
            try { set = PlayerInput.Actions; } catch { }
            if (set == null) return;
            try
            {
                var actions = set.Actions;
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    var remove = new List<BindingSource>();
                    var bs = a.Bindings;
                    for (int j = 0; j < bs.Count; j++)
                    {
                        var dev = bs[j].TryCast<DeviceBindingSource>();
                        if (dev != null && dev.Control == InputControlType.RightStickButton) remove.Add(bs[j]);
                    }
                    foreach (var b in remove)
                    {
                        a.HardRemoveBinding(b);
                        Plugin.Logger.LogInfo($"Took R3 away from game action '{a.Name}' (camera modes use it).");
                    }
                }
            }
            catch (Exception e)
            {
                r3StripBroken = true;
                Plugin.Logger.LogWarning("Couldn't remove the game's R3 binding; R3 will also trigger it: " + e.Message);
            }
        }

        /// <summary>
        /// F9: list every renderer covering the view (a 9x9 grid of rays from the camera), most-covering first, with
        /// what it is. Used to find level objects that only make sense from the vanilla camera.
        /// </summary>
        void LogViewBlockers()
        {
            try
            {
                var log = Plugin.Logger;
                var cc = CameraController.instance;
                var cam = cc != null ? cc.cachedCamera : null;
                if (cam == null) return;
                var player = PlayerCharacter.instance;
                float playerDist = player != null ? Vector3.Distance(cam.transform.position, player.transform.position) : 50f;

                const int N = 9;
                var rays = new List<Ray>();
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                        rays.Add(cam.ViewportPointToRay(new Vector3((x + 0.5f) / N, (y + 0.5f) / N, 0f)));

                var hits = new List<(int count, float near, Renderer r)>();
                foreach (var o in UnityEngine.Object.FindObjectsOfType(UnhollowerRuntimeLib.Il2CppType.Of<Renderer>()))
                {
                    var r = o.Cast<Renderer>();
                    if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                    if (((1 << r.gameObject.layer) & cam.cullingMask) == 0) continue;
                    var b = r.bounds;
                    int count = 0; float near = float.MaxValue;
                    foreach (var ray in rays)
                        if (b.IntersectRay(ray, out float d) && d < 300f) { count++; near = Mathf.Min(near, d); }
                    if (count > 0) hits.Add((count, near, r));
                }
                hits.Sort((a, b) => b.count != a.count ? b.count.CompareTo(a.count) : a.near.CompareTo(b.near));

                log.LogInfo($"--- View blockers (camera {cam.transform.position}, player {playerDist:F1} away; rays hit of {N * N}) ---");
                for (int i = 0; i < hits.Count && i < 80; i++)
                {
                    var (count, near, r) = hits[i];
                    string mat = "none", shader = "-"; int queue = 0;
                    try
                    {
                        var m = r.sharedMaterial;
                        if (m != null) { mat = m.name; queue = m.renderQueue; if (m.shader != null) shader = m.shader.name; }
                    }
                    catch { }
                    var comps = new List<string>();
                    try
                    {
                        foreach (var c in r.GetComponents<Component>())
                        {
                            string n = c.GetIl2CppType().Name;
                            if (n != "Transform" && n != "MeshFilter" && n != "MeshRenderer") comps.Add(n);
                        }
                    }
                    catch { }
                    log.LogInfo($"  {count} rays, {near:F1}m: '{Path(r.transform)}' layer={LayerMask.LayerToName(r.gameObject.layer)} " +
                                $"type={r.GetIl2CppType().Name} mat='{mat}' shader='{shader}' queue={queue} shadows={r.shadowCastingMode} " +
                                $"size={r.bounds.size} [{string.Join(",", comps)}]");
                }
                if (hits.Count == 0) log.LogInfo("  (nothing)");
                try
                {
                    log.LogInfo($"  fog={RenderSettings.fog} mode={RenderSettings.fogMode} color={RenderSettings.fogColor} " +
                                $"start={RenderSettings.fogStartDistance} end={RenderSettings.fogEndDistance} density={RenderSettings.fogDensity}; " +
                                $"ambient={RenderSettings.ambientMode} {RenderSettings.ambientLight} x{RenderSettings.ambientIntensity}");
                    log.LogInfo($"  camera clear={cam.clearFlags} bg={cam.backgroundColor} near={cam.nearClipPlane} far={cam.farClipPlane} hidden fog planes={hiddenFog.Count}");
                }
                catch (Exception e) { log.LogInfo("  render settings: " + e.Message); }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("View blocker scan failed: " + e); }
        }

        void LogPostEffects()
        {
            try
            {
                var log = Plugin.Logger;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(UnhollowerRuntimeLib.Il2CppType.Of<AmplifyColorEffect>()))
                {
                    var a = o.Cast<AmplifyColorEffect>();
                    string T(Texture t) => t == null ? "none" : $"'{t.name}'";
                    int entered = -1; string vol = "none";
                    try { entered = a.enteredVolumes != null ? a.enteredVolumes.Count : 0; } catch { }
                    try { if (a.currentVolumeLut != null) vol = a.currentVolumeLut.name; } catch { }
                    log.LogInfo($"  AmplifyColor on '{a.name}' enabled={a.enabled} lut={T(a.LutTexture)} blendLut={T(a.LutBlendTexture)} blend={a.BlendAmount} " +
                                $"mask={T(a.MaskTexture)} depthMask={a.UseDepthMask} volumes={a.UseVolumes} entered={entered} currentVolume={vol} " +
                                $"proxy={(a.TriggerVolumeProxy == null ? "none" : a.TriggerVolumeProxy.name)} exposure={a.Exposure} tonemap={a.Tonemapper}");
                }
            }
            catch (Exception e) { Plugin.Logger.LogInfo("  AmplifyColor state: " + e.Message); }
        }

        void LogDiagnostics()
        {
            try
            {
                var log = Plugin.Logger;
                log.LogInfo($"--- TunicCamera diagnostics (mode {mode}, active {active}) ---");
                log.LogInfo($"MainCameraForward calls: {CameraPatches.ForwardCalls}, MainCameraRight calls: {CameraPatches.RightCalls}");
                var cc = CameraController.instance;
                if (cc == null) { log.LogInfo("No CameraController"); return; }
                log.LogInfo($"CameraController on '{Path(cc.transform)}', cutsceneFocusMode={cc.cutsceneFocusMode}, useMask={cc.useMask}");
                foreach (var c in AllCameras(cc, cc.cachedCamera))
                    log.LogInfo($"  Camera '{Path(c.transform)}' enabled={c.enabled} ortho={c.orthographic} size={c.orthographicSize:F2} fov={c.fieldOfView:F1} near={c.nearClipPlane:F2} far={c.farClipPlane:F1} depth={c.depth} pos={c.transform.position} rot={c.transform.eulerAngles}");
                var p = PlayerCharacter.instance;
                if (p != null && p.cachedRigidbody != null) { var rb = p.cachedRigidbody; log.LogInfo($"Player rigidbody: interpolation={rb.interpolation} kinematic={rb.isKinematic} fixedDeltaTime={Time.fixedDeltaTime:F4}"); }
                if (p != null) { var lt = LockTargetPosition(p); log.LogInfo($"LT target: {(lt.HasValue ? p.zTarget.name + " at " + lt.Value : "none")}, preview: {(p.previewZTarget != null ? p.previewZTarget.name : "none")}"); }
                log.LogInfo($"WorldToViewportPoint overrides so far: {ViewportPatches.Calls}");
                if (p != null) log.LogInfo($"Player at {p.transform.position}, facing {p.transform.eulerAngles.y:F1}, head bone: {(p.headTrackingBone != null ? Path(p.headTrackingBone.transform) : "none")}");
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Diagnostics failed: " + e); }
        }

        static string Path(Transform t)
        {
            string s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }
    }
}
