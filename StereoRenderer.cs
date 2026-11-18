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
    /// with full-image effects) into a world render texture; "Camera 4 - Blit" copies that to the screen, and then
    /// "screen overlay" cameras draw on top: "_GameGUI" (every menu/HUD canvas), "_Map Camera" (instruction booklet),
    /// and so on. Each frame (LateUpdate):
    ///   1. Composite the previous frame: for each eye, copy the eye image, render the overlay cameras over the copy
    ///      (exactly as they'd draw over the screen), scale to half width and copy into the world texture's half.
    ///      Give all cameras back.
    ///   2. Render the right eye now; point the world cameras at the left-eye texture with the left-eye view so Unity's
    ///      normal pass renders it; keep the overlay cameras from drawing onto the screen this frame.
    /// If the world cameras are off (some menus stop the world rendering), the last eye images are reused, so menus
    /// still come out side-by-side.
    /// Display is one frame behind, in exchange for using no hooks into the game or Unity's render callbacks (both
    /// crashed this IL2CPP build).
    ///
    /// Eyes render at the world texture's full size and keep their contents from frame to frame (the ghosting effect
    /// blends each frame with what's already in its target). The eye offset is a small rotation about the player
    /// (gives depth for the orthographic vanilla camera too), applied through worldToCameraMatrix only, so the game's
    /// camera logic never sees it.
    /// </summary>
    internal class StereoRenderer
    {
        class Held { public Camera cam; public RenderTexture target; }
        class HeldOverlay { public Camera cam; public int cull; public CameraClearFlags clear; }
        class ConvertedCanvas { public Canvas canvas; public float plane; }

        readonly List<Held> held = new List<Held>();                // world cameras redirected to the left-eye texture
        readonly List<HeldOverlay> overlays = new List<HeldOverlay>(); // overlay cameras kept off the screen this frame
        RenderTexture eyeL, eyeR, halfL, halfR, eyeUi, world;
        bool pending;                                          // eyes rendered, not yet in the world texture
        bool on, failed, lastSetting, loggedCams;
        bool overlaysBroken;                                   // drawing overlays per eye failed; leave them alone
        string lastOverlayNames = "";

        // Every camera in the scene, rescanned now and then (world cameras the rig doesn't list, overlay cameras).
        readonly List<Camera> sceneCams = new List<Camera>();
        float nextScan;
        Camera guiCam, blitCam;
        readonly List<ConvertedCanvas> converted = new List<ConvertedCanvas>();

        /// <summary>Call at the very end of LateUpdate, after the camera pose for this frame is final.</summary>
        public void Frame(CameraController cc, IEnumerable<Camera> rig, Vector3? focus)
        {
            Composite();
            ReleaseFlat();

            bool setting = Plugin.Stereo3D.Value;
            if (setting != lastSetting) { lastSetting = setting; failed = false; overlaysBroken = false; flatBroken = false; }   // toggling retries
            if (!setting || failed) { if (on || eyeL != null || flatOn || flat != null) Stop(true); return; }
            if (cc == null)
            {
                // No game camera rig (logo screens, loading): show the plain screen image in both halves.
                if (on) Stop();
                FlatFrame();
                return;
            }
            if (flatOn) StopFlat();
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
                foreach (var c in Candidates(rig))
                {
                    try
                    {
                        var tt = c.targetTexture;
                        if (c.isActiveAndEnabled && (mask == null || c.Pointer != mask.Pointer) && tt != null && tt.Pointer == target.Pointer)
                            cams.Add(c);
                    }
                    catch { }
                }
                if (!loggedCams) { loggedCams = true; LogCams(cams, target); }
                cams.Sort((a, b) => a.depth.CompareTo(b.depth));

                var overlayCams = PickOverlays();

                if (cams.Count > 0)
                {
                    EnsureEyes(target);
                    RenderEyes(main, cams, target, focus);
                }
                else if (eyeL == null || overlayCams.Count == 0)
                {
                    return;   // nothing to show in 3D yet / nothing being drawn at all
                }
                // else: the world isn't rendering (a menu froze it); reuse the last eye images under the overlays.

                HoldOverlays(overlayCams);
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

        void RenderEyes(Camera main, List<Camera> cams, RenderTexture target, Vector3? focus)
        {
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
            nextScan = Time.unscaledTime + 0.5f;   // often enough to catch menus opening their own camera
            sceneCams.Clear();
            guiCam = null; blitCam = null;
            try
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Camera>()))
                {
                    var c = o.Cast<Camera>();
                    sceneCams.Add(c);
                    if (guiCam == null && c.name.StartsWith("_GameGUI")) guiCam = c;
                    // The camera that copies the world texture to the screen ("Camera 4 - Blit"). Matched by name too:
                    // looking its component up by type doesn't find it in this build.
                    bool isBlit = c.name.Contains("Blit");
                    if (!isBlit) try { isBlit = c.GetComponent<BlitRTexToDisplay>() != null; } catch { }
                    if (blitCam == null && isBlit && c.targetTexture == null) blitCam = c;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("3D: camera scan failed: " + e.Message); }
            ConvertOverlayCanvases();
        }

        /// <summary>
        /// Canvases in "Screen Space - Overlay" mode draw straight onto the screen after every camera, where they can't
        /// be put into both halves. Move any onto the GUI camera while 3D is on (restored when 3D stops).
        /// </summary>
        void ConvertOverlayCanvases()
        {
            if (guiCam == null || overlaysBroken) return;
            try
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Canvas>()))
                {
                    var cv = o.Cast<Canvas>();
                    if (!cv.isRootCanvas || cv.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                    converted.Add(new ConvertedCanvas { canvas = cv, plane = cv.planeDistance });
                    cv.renderMode = RenderMode.ScreenSpaceCamera;
                    cv.worldCamera = guiCam;
                    cv.planeDistance = Mathf.Lerp(guiCam.nearClipPlane, guiCam.farClipPlane, 0.05f);
                    Plugin.Logger.LogInfo($"  3D: drawing overlay canvas '{cv.name}' through the GUI camera");
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("3D: couldn't move overlay canvases: " + e.Message); }
        }

        void RestoreCanvases()
        {
            foreach (var c in converted)
            {
                try
                {
                    if (c.canvas == null) continue;
                    c.canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    c.canvas.planeDistance = c.plane;
                }
                catch { }
            }
            converted.Clear();
        }

        /// <summary>Enabled cameras that draw onto the screen after the world image is copied there, in draw order.</summary>
        List<Camera> PickOverlays()
        {
            var list = new List<Camera>();
            if (overlaysBroken) return list;
            if (blitCam == null) return list;   // can't tell what draws after the world image; leave overlays alone
            float after = 0f;
            try { after = blitCam.depth; } catch { return list; }
            foreach (var c in sceneCams)
            {
                try
                {
                    if (c == null || !c.isActiveAndEnabled || c.targetTexture != null) continue;
                    if (blitCam != null && c.Pointer == blitCam.Pointer) continue;
                    if (c.name.Contains("Blit")) continue;
                    if (c.depth <= after) continue;
                    list.Add(c);
                }
                catch { }
            }
            list.Sort((a, b) => a.depth.CompareTo(b.depth));

            string names = string.Join(", ", list.ConvertAll(c => c.name));
            if (names != lastOverlayNames)
            {
                lastOverlayNames = names;
                Plugin.Logger.LogInfo("  3D: screen overlay cameras: " + (names.Length == 0 ? "none" : names));
            }
            return list;
        }

        /// <summary>
        /// Keep overlay cameras from drawing full-width onto the screen this frame (nothing to draw, nothing to clear);
        /// next LateUpdate they're rendered by hand on top of each eye instead.
        /// </summary>
        void HoldOverlays(List<Camera> cams)
        {
            foreach (var c in cams)
            {
                try
                {
                    overlays.Add(new HeldOverlay { cam = c, cull = c.cullingMask, clear = c.clearFlags });
                    c.cullingMask = 0;
                    c.clearFlags = CameraClearFlags.Nothing;
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"3D: couldn't redirect '{c.name}': " + e.Message); }
            }
        }

        /// <summary>Put last frame's eyes (and overlays) into the world texture's halves and give the cameras back.</summary>
        void Composite()
        {
            var ov = new List<Camera>();
            foreach (var h in overlays) if (h.cam != null) ov.Add(h.cam);
            Release();
            if (!pending) return;
            pending = false;
            try
            {
                if (world == null || eyeL == null || eyeR == null) return;
                // Draw the overlays over a copy of each full-size eye (so UI effects that use the picture behind them
                // see the real picture), scale to half width (Blit keeps orientation right), then copy into place.
                // (Graphics.DrawTexture is stripped from this game's build.)
                if (!(ov.Count > 0 && DrawEyeWithOverlays(eyeL, halfL, ov) && DrawEyeWithOverlays(eyeR, halfR, ov)))
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
        /// Copy an eye, render the overlay cameras over the copy exactly as they'd draw over the screen, and scale the
        /// result into the half-width texture. The eye itself stays clean (the ghosting effect blends with it).
        /// </summary>
        bool DrawEyeWithOverlays(RenderTexture eye, RenderTexture half, List<Camera> cams)
        {
            if (overlaysBroken) return false;
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
                foreach (var c in cams)
                {
                    if (c == null) continue;
                    c.targetTexture = eyeUi;
                    try { c.Render(); }
                    finally { c.targetTexture = null; }
                }
                Graphics.Blit(eyeUi, half);
                return true;
            }
            catch (Exception e)
            {
                // Keep 3D running; menus/HUD go back to drawing full-width on the screen.
                overlaysBroken = true;
                RestoreCanvases();
                Plugin.Logger.LogWarning("3D: couldn't put menus/HUD into both halves; leaving them full-width: " + e.Message);
                return false;
            }
        }

        /// <summary>Give the cameras back their real targets, matrices and settings.</summary>
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

            foreach (var o in overlays)
            {
                try
                {
                    if (o.cam == null) continue;
                    // Only undo our change; if the game set its own values meanwhile, keep the game's.
                    if (o.cam.cullingMask == 0) o.cam.cullingMask = o.cull;
                    if (o.cam.clearFlags == CameraClearFlags.Nothing) o.cam.clearFlags = o.clear;
                }
                catch { }
            }
            overlays.Clear();
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
            RestoreCanvases();
            world = null;
            nextScan = 0f;
            sceneCams.Clear();
            guiCam = null; blitCam = null;
            lastOverlayNames = "";
            if (freeTextures) { FreeEyes(); FreeRT(ref eyeUi); }
            StopFlat(freeTextures);
            if (!on) return;
            on = false; loggedCams = false;
            Plugin.Logger.LogInfo("3D (half SBS) off");
        }

        // ---- Screens without the game's camera rig (logo screens, loading) ----
        // Every camera drawing to the screen is pointed at one screen-sized texture, "Screen Space - Overlay" canvases
        // are drawn into it by a helper camera, and a top-most overlay canvas of the mod's own shows that texture
        // squeezed into each half. All within the same frame, so no delay.

        RenderTexture flat;
        Camera flatClear, flatUiCam;   // clears the texture first / draws the moved canvases last
        GameObject flatHost, flatUi;
        Canvas flatCanvas;
        UnityEngine.UI.RawImage flatL, flatR;
        bool flatOn, flatBroken;
        float nextFlatScan;
        int flatLayers;
        readonly List<Camera> flatCams = new List<Camera>();
        readonly List<Camera> flatHeld = new List<Camera>();
        readonly List<ConvertedCanvas> flatCanvases = new List<ConvertedCanvas>();

        void FlatFrame()
        {
            if (flatBroken) return;
            try
            {
                int w = Screen.width, h = Screen.height;
                if (w < 2 || h < 2) return;
                EnsureFlatObjects();
                if (flat == null || flat.width != w || flat.height != h)
                {
                    FreeRT(ref flat);
                    // No alpha channel: the texture is shown opaque, whatever alpha the cameras leave behind.
                    var fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float)
                        ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.ARGB32;
                    flat = new RenderTexture(w, h, 24, fmt) { name = "TunicCamera Flat Screen", hideFlags = HideFlags.DontUnloadUnusedAsset };
                    flat.Create();
                }
                flatL.texture = flat; flatR.texture = flat;
                flatClear.targetTexture = flat; flatUiCam.targetTexture = flat;
                if (!flatOn)
                {
                    flatOn = true;
                    flatUi.SetActive(true);
                    flatClear.enabled = true; flatUiCam.enabled = true;
                    Plugin.Logger.LogInfo("3D: no game camera rig on this screen (logo/loading); showing it in both halves.");
                }
                if (Time.unscaledTime >= nextFlatScan) FlatScan();

                foreach (var c in flatCams)
                {
                    try
                    {
                        if (c == null || !c.isActiveAndEnabled || c.targetTexture != null) continue;
                        c.targetTexture = flat;
                        flatHeld.Add(c);
                    }
                    catch { }
                }
            }
            catch (Exception e)
            {
                flatBroken = true;
                Plugin.Logger.LogWarning("3D: couldn't show this screen side-by-side; leaving it as is: " + e);
                StopFlat(true);
            }
        }

        void EnsureFlatObjects()
        {
            if (flatHost != null && flatUi != null && flatClear != null && flatUiCam != null && flatL != null && flatR != null) return;
            DestroyFlatObjects();

            // Helper cameras, far away and looking at nothing.
            flatHost = new GameObject("TunicCamera Flat Screen Cameras");
            UnityEngine.Object.DontDestroyOnLoad(flatHost);
            flatHost.hideFlags = HideFlags.DontUnloadUnusedAsset;
            flatHost.transform.position = new Vector3(0f, -100000f, 0f);
            flatHost.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            flatClear = flatHost.AddComponent<Camera>();
            flatClear.clearFlags = CameraClearFlags.SolidColor;
            flatClear.backgroundColor = Color.black;
            flatClear.cullingMask = 0;
            flatClear.depth = -10000f;
            flatClear.enabled = false;
            var uiHost = new GameObject("TunicCamera Flat Screen UI Camera");
            uiHost.transform.SetParent(flatHost.transform, false);
            flatUiCam = uiHost.AddComponent<Camera>();
            flatUiCam.clearFlags = CameraClearFlags.Depth;
            flatUiCam.cullingMask = 0;
            flatUiCam.depth = 10000f;
            flatUiCam.nearClipPlane = 0.3f; flatUiCam.farClipPlane = 1000f;
            flatUiCam.enabled = false;

            // The display: a top-most overlay canvas with the texture in each half.
            flatUi = new GameObject("TunicCamera SBS Display");
            UnityEngine.Object.DontDestroyOnLoad(flatUi);
            flatUi.hideFlags = HideFlags.DontUnloadUnusedAsset;
            flatUi.SetActive(false);
            flatCanvas = flatUi.AddComponent<Canvas>();
            flatCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            flatCanvas.sortingOrder = 32767;
            flatL = MakeHalf("Left", 0f);
            flatR = MakeHalf("Right", 0.5f);
        }

        UnityEngine.UI.RawImage MakeHalf(string name, float x)
        {
            var go = new GameObject("TunicCamera SBS " + name);
            go.transform.SetParent(flatUi.transform, false);
            var img = go.AddComponent<UnityEngine.UI.RawImage>();
            img.raycastTarget = false;
            var rt = img.rectTransform;
            rt.anchorMin = new Vector2(x, 0f);
            rt.anchorMax = new Vector2(x + 0.5f, 1f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return img;
        }

        void FlatScan()
        {
            nextFlatScan = Time.unscaledTime + 0.5f;
            flatCams.Clear();
            IntPtr a = flatClear.Pointer, b = flatUiCam.Pointer, mine = flatCanvas.Pointer;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Camera>()))
            {
                var c = o.Cast<Camera>();
                if (c.Pointer != a && c.Pointer != b) flatCams.Add(c);
            }
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Canvas>()))
            {
                var cv = o.Cast<Canvas>();
                if (cv.Pointer == mine || !cv.isRootCanvas || cv.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                flatCanvases.Add(new ConvertedCanvas { canvas = cv, plane = cv.planeDistance });
                cv.renderMode = RenderMode.ScreenSpaceCamera;
                cv.worldCamera = flatUiCam;
                cv.planeDistance = 100f;
                flatLayers |= 1 << cv.gameObject.layer;
                Plugin.Logger.LogInfo($"  3D: drawing overlay canvas '{cv.name}' into both halves");
            }
            flatUiCam.cullingMask = flatLayers;
        }

        /// <summary>Give the screen cameras back (they're redirected again each frame while needed).</summary>
        void ReleaseFlat()
        {
            foreach (var c in flatHeld)
            {
                try
                {
                    if (c == null) continue;
                    var tt = c.targetTexture;
                    if (tt != null && flat != null && tt.Pointer == flat.Pointer) c.targetTexture = null;
                }
                catch { }
            }
            flatHeld.Clear();
        }

        void StopFlat(bool freeTexture = false)
        {
            ReleaseFlat();
            foreach (var c in flatCanvases)
            {
                try
                {
                    if (c.canvas == null) continue;
                    c.canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    c.canvas.planeDistance = c.plane;
                }
                catch { }
            }
            flatCanvases.Clear();
            flatCams.Clear();
            flatLayers = 0;
            nextFlatScan = 0f;
            try
            {
                if (flatUi != null) flatUi.SetActive(false);
                if (flatClear != null) flatClear.enabled = false;
                if (flatUiCam != null) { flatUiCam.enabled = false; flatUiCam.cullingMask = 0; }
            }
            catch { }
            if (freeTexture)
            {
                DestroyFlatObjects();
                FreeRT(ref flat);
            }
            if (flatOn) { flatOn = false; Plugin.Logger.LogInfo("3D: game camera rig back (or 3D off); flat-screen mode ended."); }
        }

        void DestroyFlatObjects()
        {
            try { if (flatHost != null) UnityEngine.Object.Destroy(flatHost); } catch { }
            try { if (flatUi != null) UnityEngine.Object.Destroy(flatUi); } catch { }
            flatHost = null; flatUi = null; flatClear = null; flatUiCam = null; flatCanvas = null; flatL = null; flatR = null;
        }

        static void LogCams(List<Camera> used, RenderTexture target)
        {
            Plugin.Logger.LogInfo($"  3D: world texture = '{target.name}' {target.width}x{target.height}; eye cameras: " +
                                  string.Join(", ", used.ConvertAll(c => $"'{c.name}' (depth {c.depth})")));
            if (used.Count == 0) Plugin.Logger.LogWarning("  3D: no world cameras active right now.");
        }
    }
}
