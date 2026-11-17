using System;
using HarmonyLib;
using UnhollowerRuntimeLib;

namespace TunicCamera
{
    /// <summary>Adds a "3D" (half side-by-side) Off/On row to the game's Options > Graphics page.</summary>
    [HarmonyPatch]
    internal static class OptionsPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(OptionsGUI), nameof(OptionsGUI.page_graphics))]
        static void AddStereoToggle(OptionsGUI __instance)
        {
            try
            {
                var action = DelegateSupport.ConvertDelegate<OptionsGUIMultiSelect.MultiSelectAction>(
                    new Action<int>(i => Plugin.Stereo3D.Value = i == 1));
                var row = __instance.addToggle("3D", "Off", "On", Plugin.Stereo3D.Value ? 1 : 0, action);
                // page_graphics adds Return last; move our row up one so it sits above it.
                var t = row.transform;
                int i = t.GetSiblingIndex();
                if (i > 0) t.SetSiblingIndex(i - 1);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Couldn't add 3D toggle to Graphics options: " + e.Message); }
        }
    }
}
