using System;
using System.Collections.Generic;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace TunicCamera
{
    /// <summary>
    /// Half side-by-side stereo.
    ///
    /// TUNIC draws the world with several cameras ("Main", "_FinishlineDisplay", a depth-only "Post Processing" camera
    /// with full-image effects) into a world render texture; "Camera 4 - Blit" copies that to the screen and the
    /// "_GameGUI" camera draws every menu/HUD canvas on top. Each frame (LateUpdate):
    ///   1. Put the previous frame's two eyes into the world texture's halves, and the previous frame's UI image into
    ///      both halves on top, so the screen shows them this frame. Give all cameras back.
    ///   2. Render the right eye now; point the world cameras at the left-eye texture with the left-eye view, and the
    ///      GUI camera at a UI texture, so Unity's normal pass renders those.
    /// Display is one frame behind, in exchange for using no hooks into the game or Unity's render callbacks (both
    /// crashed this IL2CPP build).
    ///
    /// Eyes render at the world texture's full size, and keep their contents from frame to frame (and across area
    /// changes): the ghosting effect blends each frame with what's already in its target, exactly as it does with the
    /// world texture normally. A brand-new eye starts as a copy of the world texture, never as uninitialized memory.
    /// The eye offset is a small rotation about the player (gives depth for the orthographic vanilla camera too),
    /// applied through worldToCameraMatrix only, so the game's camera logic never sees it.
    /// </summary>
    internal class StereoRenderer
    {
        class Held { public Camera cam; public RenderTexture target; }
        readonly List<Held> held = new List<Held>();          // world cameras currently redirected to the left-eye texture
        RenderTexture eyeL, eyeR, halfL, halfR, eyeUi, world;
        bool pending;                                          // eyes rendered, not yet in the world texture
        bool on, failed, lastSetting, loggedCams;
        bool uiBroken;                                         // drawing the UI per eye failed; leave it alone

        // Every camera in the scene, rescanned now and then (finds world cameras the rig doesn't list, and the GUI camera).
        readonly List<Camera> sceneCams = new List<Camera>();
        float nextScan;
        Camera guiCam;
        bool guiHeld; int guiCull;

        /// <summary>Call at the very end of LateUpdate, after the camera pose for this frame is final.</summary>
        public void Frame(CameraController cc, IEnumerable<Camera> rig, Vector3? focus)
        {
            Composite();

            bool setting = Plugin.Stereo3D.Value;
            if (setting != lastSetting) { lastSetting = setting; failed = false; uiBroken = false; }   // toggling retries after a failure
            if (!setting || failed) { if (on || eyeL != null) Stop(true); return; }
            if (cc == null) { if (on) Stop(); return; }
            try
            {
                Camera main = cc.cachedCamera;
                if (main == null) return;
                if (!on) { on = true; Plugin.Logger.LogInfo("3D (half SBS) on"); }
                Camera mask = null;
                try { mask = cc.maskCamera; } catch { }
                var target = main.targetTexture;
                if (target == null)
                {
                    Plugin.Logger.LogWarning("3D: main camera draws straight to the screen; this 3D path needs TUNIC's world render texture.");
                    failed = true; Stop(true); return;
                }

                if (Time.unscaledTime >= nextScan) ScanScene();

                // Every enabled camera drawing into the world texture, in draw order. (Camera.allCameras is not used:
                // in this BepInEx build its array is sometimes reported as already garbage-collected.)
                var cams = new List<Camera>();
                var seen = new List<Camera>();
                foreach (var c in Candidates(rig))
                {
                    try
                    {
                        seen.Add(c);
                        var tt = c.targetTexture;
                        if (c.isActiveAndEnabled && (mask == null || c.Pointer != mask.Pointer) && tt != null && tt.Pointer == target.Pointer)
                            cams.Add(c);
                    }
                    catch { }
                }
                if (!loggedCams) { loggedCams = true; LogCams(seen, cams, target); }
                if (cams.Count == 0) return;
                cams.Sort((a, b) => a.depth.CompareTo(b.depth));

                EnsureEyes(target);

                Transform mt = main.transform;
                Vector3 fwd = mt.forward;
                float conv = focus.HasValue ? Vector3.Dot(focus.Value - mt.position, fwd) : 0f;
                if (conv < 3f) conv = main.orthographic ? 30f : 3f;
                Vector3 pivot = mt.position + fwd * conv;
                float halfAngle = main.orthographic
                    ? Plugin.StereoOrthoAngle.Value * 0.5f
                    : Mathf.Atan2(Plugin.StereoSeparation.Value * 0.5f, conv) * Mathf.Rad2Deg;
                // Positive rotation about "up" swings a camera behind the pivot to the left, so the right eye is -angle.
                Matrix4x4 rightE = EyeInverse(pivot, mt.up, -halfAngle);
                Matrix4x4 leftE = EyeInverse(pivot, mt.up, halfAngle);

                var baseViews = new Matrix4x4[cams.Count];
                for (int i = 0; i < cams.Count; i++)
                {
                    cams[i].ResetWorldToCameraMatrix();
                    baseViews[i] = cams[i].worldToCameraMatrix;
                }

                // Right eye now.
                for (int i = 0; i < cams.Count; i++)
                {
                    var c = cams[i];
                    c.targetTexture = eyeR;
                    c.worldToCameraMatrix = baseViews[i] * rightE;
                    c.Render();
                }
                // Left eye: Unity's normal pass renders it into eyeL.
                for (int i = 0; i < cams.Count; i++)
                {
                    var c = cams[i];
                    held.Add(new Held { cam = c, target = target });
                    c.targetTexture = eyeL;
                    c.worldToCameraMatrix = baseViews[i] * leftE;
                }
                HoldGui();
                world = target;
                pending = true;

            }
            catch (Exception e)
            {
                // Pause for this session but keep the user's setting; toggling 3D again retries.
                Plugin.Logger.LogError("3D rendering failed, pausing it (toggle 3D to retry): " + e);
                failed = true;
                Stop(true);
            }
        }

        IEnumerable<Camera> Candidates(IEnumerable<Camera> rig)
        {
            var ptrs = new HashSet<IntPtr>();
            if (rig != null)
                foreach (var c in rig)
                    if (c != null && ptrs.Add(c.Pointer)) yield return c;
            foreach (var c in sceneCams)
                if (c != null && ptrs.Add(c.Pointer)) yield return c;
        }

        void ScanScene()
        {
            nextScan = Time.unscaledTime + 1f;
            sceneCams.Clear();
            guiCam = null;
            try
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Camera>()))
                {
                    var c = o.Cast<Camera>();
                    sceneCams.Add(c);
                    if (guiCam == null && c.name.StartsWith("_GameGUI")) guiCam = c;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("3D: camera scan failed: " + e.Message); }
        }

        /// <summary>
        /// Keep the GUI camera from drawing full-width onto the screen this frame (it draws nothing with an empty
        /// culling mask); next LateUpdate it's rendered by hand on top of each eye instead.
        /// </summary>
        void HoldGui()
        {
            try
            {
                if (uiBroken || guiCam == null || !guiCam.isActiveAndEnabled || guiCam.targetTexture != null) return;
                guiCull = guiCam.cullingMask;
                guiCam.cullingMask = 0;
                guiHeld = true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("3D: couldn't redirect the UI: " + e.Message); guiHeld = false; }
        }

        /// <summary>Put last frame's eyes (and UI) into the world texture's halves and give the cameras back.</summary>
        void Composite()
        {
            bool withUi = guiHeld;
            Release();
            if (!pending) return;
            pending = false;
            try
            {
                if (world == null || eyeL == null || eyeR == null) return;
                // Draw the UI over a copy of each full-size eye (so UI effects that use the picture behind them see
                // the real picture), scale to half width (Blit keeps orientation right), then copy into place.
                // (Graphics.DrawTexture is stripped from this game's build.)
                if (!(withUi && DrawEyeWithUi(eyeL, halfL) && DrawEyeWithUi(eyeR, halfR)))
                {
                    Graphics.Blit(eyeL, halfL);
                    Graphics.Blit(eyeR, halfR);
                }
                int hw = halfL.width, h = halfL.height;
                Graphics.CopyTexture(halfL, 0, 0, 0, 0, hw, h, world, 0, 0, 0, 0);
                Graphics.CopyTexture(halfR, 0, 0, 0, 0, hw, h, world, 0, 0, world.width - hw, 0);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("3D compositing failed, pausing it (toggle 3D to retry): " + e);
                failed = true;
            }
        }

        /// <summary>
        /// Copy an eye, render the GUI camera over the copy exactly as it would draw over the screen, and scale the
        /// result into the half-width texture. The eye itself stays clean (the ghosting effect blends with it).
        /// </summary>
        bool DrawEyeWithUi(RenderTexture eye, RenderTexture half)
        {
            if (uiBroken || guiCam == null) return false;
            try
            {
                if (eyeUi == null || eyeUi.width != eye.width || eyeUi.height != eye.height || eyeUi.format != eye.format)
                {
                    FreeRT(ref eyeUi);
                    var d = eye.descriptor;
                    if (d.depthBufferBits < 24) d.depthBufferBits = 24;   // UI masks use the stencil buffer
                    eyeUi = Make(d, "TunicCamera Eye + UI");
                }
                Graphics.Blit(eye, eyeUi);
                guiCam.targetTexture = eyeUi;
                try { guiCam.Render(); }
                finally { guiCam.targetTexture = null; }
                Graphics.Blit(eyeUi, half);
                return true;
            }
            catch (Exception e)
            {
                // Keep 3D running; the UI goes back to drawing full-width on the screen.
                uiBroken = true;
                Plugin.Logger.LogWarning("3D: couldn't put the UI into both halves; leaving it full-width: " + e.Message);
                return false;
            }
        }

        /// <summary>Give the cameras back their real targets and default matrices.</summary>
        void Release()
        {
            foreach (var h in held)
            {
                try
                {
                    if (h.cam == null) continue;
                    // Only undo our redirect; if the game assigned a new target meanwhile, keep the game's.
                    var tt = h.cam.targetTexture;
                    if (tt != null && eyeL != null && tt.Pointer == eyeL.Pointer) h.cam.targetTexture = h.target;
                    h.cam.ResetWorldToCameraMatrix();
                }
                catch { }
            }
            held.Clear();

            if (guiHeld)
            {
                guiHeld = false;
                try
                {
                    if (guiCam != null && guiCam.cullingMask == 0) guiCam.cullingMask = guiCull;
                }
                catch { }
            }
        }

        void EnsureEyes(RenderTexture target)
        {
            int w = target.width, h = target.height;
            // (Each is checked: if one was ever deleted out from under us, rebuild them all.)
            if (eyeL != null && eyeR != null && halfL != null && halfR != null
                && eyeL.width == w && eyeL.height == h && eyeL.format == target.format) return;
            bool anyLeft = eyeL != null || eyeR != null || halfL != null || halfR != null;
            bool allLeft = eyeL != null && eyeR != null && halfL != null && halfR != null;
            if (anyLeft && !allLeft) Plugin.Logger.LogWarning("  3D: some eye textures went missing; rebuilding them.");
            FreeEyes();
            var d = target.descriptor;
            eyeL = Make(d, "TunicCamera Left Eye");
            eyeR = Make(d, "TunicCamera Right Eye");
            // Start from what the screen currently shows, never from uninitialized memory: effects that blend with the
            // previous frame would otherwise blend with garbage.
            Graphics.Blit(target, eyeL);
            Graphics.Blit(target, eyeR);
            d.width = Mathf.Max(1, w / 2);
            d.depthBufferBits = 0;
            d.msaaSamples = 1;
            halfL = Make(d, "TunicCamera Left Half");
            halfR = Make(d, "TunicCamera Right Half");
            Plugin.Logger.LogInfo($"  3D: eye textures {w}x{h} {target.format} msaa={target.antiAliasing}");
        }

        static RenderTexture Make(RenderTextureDescriptor d, string name)
        {
            // Unity's "unload unused assets" (run on every scene load) can't see references held by the mod, so
            // without this flag it deletes any of these textures no camera happens to point at.
            var rt = new RenderTexture(d) { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            rt.Create();
            return rt;
        }

        static void FreeRT(ref RenderTexture rt)
        {
            try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
            rt = null;
        }

        void FreeEyes()
        {
            FreeRT(ref eyeL); FreeRT(ref eyeR); FreeRT(ref halfL); FreeRT(ref halfR);
        }

        /// <summary>Inverse of "rotate the camera by angle about axis through pivot", for post-multiplying a view matrix.</summary>
        static Matrix4x4 EyeInverse(Vector3 pivot, Vector3 axis, float angle)
        {
            return Matrix4x4.Translate(pivot) * Matrix4x4.Rotate(Quaternion.AngleAxis(-angle, axis)) * Matrix4x4.Translate(-pivot);
        }

        /// <summary>
        /// Stop drawing in 3D. On an area change (freeTextures false) the eye textures are kept, so each eye's
        /// frame-to-frame history carries on; they're freed when 3D is switched off.
        /// </summary>
        public void Stop(bool freeTextures = false)
        {
            pending = false;
            Release();
            world = null;
            nextScan = 0f;
            sceneCams.Clear();
            guiCam = null;
            if (freeTextures) { FreeEyes(); FreeRT(ref eyeUi); }
            if (!on) return;
            on = false; loggedCams = false;
            Plugin.Logger.LogInfo("3D (half SBS) off");
        }

        static void LogCams(List<Camera> seen, List<Camera> used, RenderTexture target)
        {
            Plugin.Logger.LogInfo($"  3D: world texture = '{target.name}' {target.width}x{target.height}; eye cameras: " +
                                  string.Join(", ", used.ConvertAll(c => $"'{c.name}' (depth {c.depth})")));
            if (used.Count == 0) Plugin.Logger.LogWarning("  3D: no cameras to render - 3D has no effect.");
        }
    }
}
