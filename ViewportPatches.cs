using System;
using HarmonyLib;
using UnityEngine;

namespace TunicCamera
{
    /// <summary>
    /// The game places screen-space UI (the LT/grapple target indicator, HP bars, ...) with its own
    /// CameraController.WorldToViewportPoint, which projects through the vanilla camera. In a custom mode,
    /// project through the pose we actually render with instead. The pose is stored here rather than read from
    /// the camera transform because the game rewrites that transform before its UI updates run.
    /// </summary>
    [HarmonyPatch]
    internal static class ViewportPatches
    {
        static bool hasView;
        static Vector3 viewPos;
        static Quaternion viewRotInv;
        static float tanHalfFov, aspect;

        internal static int Calls;

        internal static void SetView(Vector3 pos, Quaternion rot, float fov, float camAspect)
        {
            viewPos = pos; viewRotInv = Quaternion.Inverse(rot);
            tanHalfFov = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad); aspect = camAspect;
            hasView = true;
        }

        internal static void ClearView() => hasView = false;

        static Vector3 Project(Vector3 world)
        {
            Calls++;
            Vector3 v = viewRotInv * (world - viewPos);
            // Behind the camera: report it as off-screen to the side it's on rather than mirrored.
            if (v.z <= 0.01f) return new Vector3(v.x >= 0f ? 2f : -1f, 0.5f, v.z);
            return new Vector3(0.5f + 0.5f * v.x / (v.z * tanHalfFov * aspect),
                               0.5f + 0.5f * v.y / (v.z * tanHalfFov),
                               v.z);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CameraController), nameof(CameraController.WorldToViewportPoint), new Type[] { typeof(Vector3) })]
        static void Overload1(Vector3 pos, ref Vector3 __result) { if (hasView) __result = Project(pos); }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CameraController), nameof(CameraController.WorldToViewportPoint), new Type[] { typeof(Vector3), typeof(bool) })]
        static void Overload2(Vector3 pos, ref Vector3 __result) { if (hasView) __result = Project(pos); }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CameraController), "worldToViewportPoint", new Type[] { typeof(bool), typeof(Vector3) })]
        static void Inner(Vector3 point3D, ref Vector3 __result) { if (hasView) __result = Project(point3D); }
    }
}
