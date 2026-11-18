using BepInEx;
using BepInEx.Configuration;
using BepInEx.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TunicCamera
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "tunic.camera.modes";
        public const string Name = "TUNIC Camera Modes";
        public const string Version = "1.0.0";

        internal static ManualLogSource Logger;

        // General
        internal static ConfigEntry<string> ModeCycle;
        internal static ConfigEntry<KeyCode> CycleKey;
        internal static ConfigEntry<bool> TakeOverR3;
        internal static ConfigEntry<bool> ShowModeName;
        internal static ConfigEntry<bool> DisableDepthOfField;
        internal static ConfigEntry<bool> DisableOcclusionMask;
        internal static ConfigEntry<bool> InvertY;
        internal static ConfigEntry<float> StickYawSpeed;
        internal static ConfigEntry<float> StickPitchSpeed;
        internal static ConfigEntry<float> MouseSensitivity;
        internal static ConfigEntry<bool> CameraCollision;
        internal static ConfigEntry<bool> SmoothPlayerMotion;
        internal static ConfigEntry<float> TransitionTime;
        internal static ConfigEntry<bool> HideFogPlanes;

        // Per mode
        internal static ConfigEntry<float> OrbitDistance;
        internal static ConfigEntry<float> OrbitPitch;
        internal static ConfigEntry<float> OrbitFov;
        internal static ConfigEntry<float> ChaseDistance;
        internal static ConfigEntry<float> ChasePitch;
        internal static ConfigEntry<float> ChaseFov;
        internal static ConfigEntry<float> ChaseFollowTime;
        internal static ConfigEntry<float> FirstPersonFov;
        internal static ConfigEntry<float> FirstPersonEyeHeight;
        internal static ConfigEntry<float> PivotHeight;

        // 3D
        internal static ConfigEntry<bool> Stereo3D;
        internal static ConfigEntry<KeyCode> Stereo3DKey;
        internal static ConfigEntry<float> StereoSeparation;
        internal static ConfigEntry<float> StereoOrthoAngle;

        public override void Load()
        {
            Logger = Log;

            ModeCycle = Config.Bind("General", "ModeCycle", "Default,Orbit,Chase,FirstPerson",
                "Modes cycled by tapping R3 (or the keyboard key), in order. Valid: Default, Orbit (3rd Person Orbit), Chase (3rd Person Chase), FirstPerson (1st Person).");
            TakeOverR3 = Config.Bind("General", "TakeOverR3", true,
                "Remove R3 from the game's own actions (by default Toggle Focus; Caps Lock still toggles focus) so R3 only controls the camera.");
            CycleKey = Config.Bind("General", "CycleKey", KeyCode.F5, "Keyboard key that cycles camera modes.");
            ShowModeName = Config.Bind("General", "ShowModeName", true, "Briefly show the mode name on screen when switching.");
            DisableDepthOfField = Config.Bind("General", "DisableDepthOfField", true,
                "Turn off the game's depth-of-field blur in custom modes (it's tuned for the vanilla camera and blurs everything in perspective).");
            DisableOcclusionMask = Config.Bind("General", "DisableOcclusionMask", true,
                "Turn off the game's 'see the player through walls' mask in custom modes (it's aligned to the vanilla camera).");
            InvertY = Config.Bind("General", "InvertY", false, "Invert vertical camera look.");
            StickYawSpeed = Config.Bind("General", "StickYawSpeed", 150f, "Right stick horizontal turn speed, degrees/second.");
            StickPitchSpeed = Config.Bind("General", "StickPitchSpeed", 100f, "Right stick vertical turn speed, degrees/second.");
            MouseSensitivity = Config.Bind("General", "MouseSensitivity", 3f, "Mouse look sensitivity while holding the right mouse button.");
            PivotHeight = Config.Bind("General", "PivotHeight", 0.8f, "Height above the player's feet that Orbit/Chase cameras look at.");
            CameraCollision = Config.Bind("General", "CameraCollision", true,
                "Pull Orbit/Chase cameras in front of walls. Turn off if invisible level boundaries keep yanking the camera close.");
            SmoothPlayerMotion = Config.Bind("General", "SmoothPlayerMotion", true,
                "Interpolate the player's physics body in custom modes so the fox doesn't judder. Turn off if the player behaves oddly.");
            TransitionTime = Config.Bind("General", "TransitionTime", 1f,
                "Seconds for the camera to glide between modes when you switch (0 = cut instantly).");
            HideFogPlanes = Config.Bind("General", "HideFogPlanes", true,
                "Hide the black fog planes levels use to black out space around rooms for the default camera (they cover the view from other angles, e.g. in the sewers/catacombs).");

            OrbitDistance = Config.Bind("Orbit", "Distance", 10f, "Starting distance from the player.");
            OrbitPitch = Config.Bind("Orbit", "Pitch", 35f, "Starting downward angle, degrees.");
            OrbitFov = Config.Bind("Orbit", "Fov", 50f, "Field of view.");
            ChaseDistance = Config.Bind("Chase", "Distance", 6f, "Distance behind the player.");
            ChasePitch = Config.Bind("Chase", "Pitch", 18f, "Downward angle, degrees.");
            ChaseFov = Config.Bind("Chase", "Fov", 55f, "Field of view.");
            ChaseFollowTime = Config.Bind("Chase", "FollowTime", 0.5f, "Seconds for the camera to swing behind the player when you're not steering it.");
            FirstPersonFov = Config.Bind("FirstPerson", "Fov", 70f, "Field of view.");
            FirstPersonEyeHeight = Config.Bind("FirstPerson", "EyeHeight", 0.9f, "Eye height above the feet, used if the head bone can't be found.");

            Stereo3D = Config.Bind("3D", "Enabled", false,
                "Half side-by-side stereo 3D (for 3D TVs / VR video players). Also toggled in Options > Graphics.");
            Stereo3DKey = Config.Bind("3D", "ToggleKey", KeyCode.F6, "Keyboard key that toggles 3D.");
            StereoSeparation = Config.Bind("3D", "Separation", 0.15f,
                "Eye separation in world units for Orbit/Chase/FirstPerson. Higher = stronger depth. The fox is roughly 1 unit tall.");
            StereoOrthoAngle = Config.Bind("3D", "OrthoAngle", 1.5f,
                "Angle between the eyes, degrees, for the default (orthographic) camera. Higher = stronger depth.");

            var harmony = new Harmony(Guid);
            try { harmony.PatchAll(typeof(CameraPatches)); }
            catch (System.Exception e) { Log.LogError("Movement-direction patch failed; camera modes will work but movement may not follow the camera: " + e); }
            try { harmony.PatchAll(typeof(ViewportPatches)); }
            catch (System.Exception e) { Log.LogError("Screen-projection patch failed; target indicators may be misplaced in custom modes: " + e); }
            try { harmony.PatchAll(typeof(OptionsPatches)); }
            catch (System.Exception e) { Log.LogError("Options-menu patch failed; use " + Stereo3DKey.Value + " to toggle 3D: " + e); }
            AddComponent<CameraModeController>();

            Log.LogInfo($"{Name} {Version} loaded. Tap R3 or press {CycleKey.Value} to cycle camera modes.");
        }
    }
}
