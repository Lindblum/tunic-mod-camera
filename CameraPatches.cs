using HarmonyLib;
using UnityEngine;

namespace TunicCamera
{
    /// <summary>
    /// The game converts stick input into world movement using CameraController.MainCameraForward/Right.
    /// When a custom mode has rotated the camera, re-aim those vectors to our yaw so "up on the stick"
    /// means "away from the camera". The rewrite is idempotent: it keeps the original's vertical component
    /// and only replaces the horizontal heading, so it's correct whether the original getter read the
    /// game's pose or our overridden transform.
    /// </summary>
    [HarmonyPatch]
    internal static class CameraPatches
    {
        /// <summary>Set by CameraModeController each frame. Null = vanilla, don't touch.</summary>
        internal static float? OverrideYaw;

        /// <summary>Diagnostics: confirms the game is actually calling these getters.</summary>
        internal static int ForwardCalls, RightCalls;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CameraController), nameof(CameraController.MainCameraForward), MethodType.Getter)]
        private static void ForwardPostfix(ref Vector3 __result)
        {
            ForwardCalls++;
            if (OverrideYaw is float yaw) __result = WithHeading(__result, yaw);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CameraController), nameof(CameraController.MainCameraRight), MethodType.Getter)]
        private static void RightPostfix(ref Vector3 __result)
        {
            RightCalls++;
            if (OverrideYaw is float yaw) __result = WithHeading(__result, yaw + 90f);
        }

        private static Vector3 WithHeading(Vector3 v, float yawDegrees)
        {
            float h = Mathf.Sqrt(v.x * v.x + v.z * v.z);
            float y = v.y;
            // A camera looking straight up/down has no horizontal heading; treat it as level.
            if (h < 0.1f) { h = 1f; y = 0f; }
            float r = yawDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(r) * h, y, Mathf.Cos(r) * h);
        }
    }
}
