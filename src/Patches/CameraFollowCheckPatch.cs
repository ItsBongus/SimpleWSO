using HarmonyLib;
using SimpleWSO.Core;

namespace SimpleWSO.Patches
{
    /// <summary>
    /// CameraStateManager.FollowCheck is spectator-oriented: for non-local aircraft it can clear
    /// followingUnit after a short validation interval. In gunner mode that looks like "camera
    /// disconnects but the mod still thinks I'm a gunner". While we deliberately follow the
    /// gunner target aircraft, suppress that spectator cleanup.
    /// </summary>
    [HarmonyPatch(typeof(CameraStateManager), "FollowCheck")]
    public static class CameraFollowCheckPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(CameraStateManager __instance)
        {
            if (!GunnerState.Active || GunnerState.TargetAircraft == null || __instance == null)
                return true;

            return __instance.followingUnit != GunnerState.TargetAircraft;
        }
    }
}
