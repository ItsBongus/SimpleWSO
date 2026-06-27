using HarmonyLib;
using SimpleWSO.Core;
using SimpleWSO.Gunner;
using SimpleWSO.Net;
using System.Collections.Generic;
using UnityEngine;

namespace SimpleWSO.Patches
{
    /// <summary>
    /// Gunner-station target handling.
    ///
    /// The turret-specific target path (Turret.SetTarget / WeaponStation.SetStationTurretTarget /
    /// Aircraft.SetStationTurretTarget) is intentionally left UNPATCHED for gunner stations: the
    /// owner replicates the gunner's chosen target through the game's own networked turret-target
    /// RPC so every client tracks it with the vanilla aim solver. Gunner stations are forced into
    /// manual mode, so vanilla turret AI never assigns targets here; the only writes are
    /// gunner-authoritative (local or replicated). The pilot's weapon-manager target LIST path
    /// (SetStationTargets) stays blocked below so the two seats keep separate target lists.
    /// </summary>
    [HarmonyPatch(typeof(WeaponStation), "SetStationActive")]
    public static class GunnerStationActivePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(WeaponStation __instance, Aircraft aircraft, bool isActive)
        {
            if (aircraft == null || aircraft.disabled || __instance == null ||
                !SimpleWsoNet.IsRemoteGunnerStation(aircraft.NetId, __instance.Number))
            {
                return true;
            }

            if (Reflect.TryGetField<List<Turret>>(__instance, "Turrets", out var turrets) && turrets != null)
            {
                foreach (var turret in turrets)
                {
                    if (turret == null) continue;
                    turret.SetManual(true);
                    TurretController.ClearTurretTargetLock(turret, __instance.Number);
                }
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(WeaponStation), "SetStationTargets")]
    public static class GunnerWeaponStationTargetsPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(WeaponStation __instance)
        {
            Aircraft ac = StationDiscovery.GetAircraft(__instance);
            return ac == null || ac.disabled || !SimpleWsoNet.IsRemoteGunnerStation(ac.NetId, __instance.Number);
        }
    }

    [HarmonyPatch(typeof(Aircraft), "SetStationTargets")]
    public static class GunnerAircraftStationTargetsPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Aircraft __instance, byte stationIndex)
            => __instance == null || __instance.disabled || !SimpleWsoNet.IsRemoteGunnerStation(__instance.NetId, stationIndex);
    }

    [HarmonyPatch(typeof(Aircraft), "SetTurretVector")]
    public static class GunnerAircraftTurretVectorPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Aircraft __instance, byte weaponStationIndex, Vector3 direction)
        {
            if (TurretController.AllowTurretAimAuthority)
                return true;

            return __instance == null ||
                   __instance.disabled ||
                   !SimpleWsoNet.IsRemoteGunnerStation(__instance.NetId, weaponStationIndex);
        }
    }
}
