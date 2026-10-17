using System;
using HarmonyLib;

namespace aimlock
{
    [HarmonyPatch(typeof(PlayerCamera), "SetCamPosRot")]
    internal static class Patch_PlayerCamera_SetCamPosRot
    {
        private static void Prefix(PlayerCamera __instance)
        {
            try
            {
                FishAimLock.Tick(__instance);
            }
            catch (Exception ex)
            {
                if (FishAimLock.Log != null)
                    FishAimLock.Log.LogError("[aimlock] Tick 异常: " + ex);
            }
        }
    }
}
