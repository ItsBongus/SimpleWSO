using HarmonyLib;
using SimpleWSO.Core;
using SimpleWSO.Gunner;
using SimpleWSO.Net;
using UnityEngine;

namespace SimpleWSO.Patches
{
    /// <summary>
    /// On the aircraft owner, drive gunner-controlled turrets from networked aim and keep
    /// them in manual free-aim (no target lock fighting the gunner look direction).
    /// </summary>
    [HarmonyPatch(typeof(Turret), "FixedUpdate")]
    public static class TurretRemoteGunnerAimPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Turret __instance)
        {
            if (__instance == null) return true;

            if (!Reflect.TryGetField<Unit>(__instance, "attachedUnit", out Unit attachedUnit) || attachedUnit == null)
                return true;

            var aircraft = attachedUnit as Aircraft;
            if (aircraft == null || aircraft.disabled || !aircraft.LocalSim)
                return true;

            if (!TryGetStation(aircraft, __instance, out TurretStation station))
                return true;

            byte stationNumber = station.Number;
            if (!SimpleWsoNet.IsRemoteGunnerStation(aircraft.NetId, stationNumber))
                return true;

            uint desiredTargetId = SimpleWsoNet.GetRemoteGunnerTargetId(aircraft.NetId, stationNumber);
            if (Reflect.TryGetField<Unit>(__instance, "target", out Unit target) &&
                target != null &&
                !target.disabled)
            {
                if (desiredTargetId != 0u && target.persistentID.Id == desiredTargetId)
                {
                    TurretController.PrepareManualTurret(station, __instance, clearTargetLock: false);
                    return true;
                }

                // A gunner-owned station may only track the gunner's selected target. If
                // vanilla/pilot/AI state writes another target, reject it and return to the
                // gunner's streamed free-aim path until the owner reasserts the desired lock.
                TurretController.ClearTurretTargetLock(__instance, stationNumber);
            }

            if (!SimpleWsoNet.TryGetRemoteGunnerAim(aircraft.NetId, stationNumber, out Vector3 aimDir) ||
                aimDir.sqrMagnitude < 1e-6f)
                return false;

            aimDir.Normalize();
            TurretController.PrepareManualTurret(station, __instance, clearTargetLock: true);
            __instance.SetVector(aimDir);
            TurretController.RunAimTurret(__instance, aimDir);
            TurretController.PublishTurretAim(aircraft, stationNumber, aimDir);
            return false;
        }

        private static bool TryGetStation(Aircraft aircraft, Turret turret, out TurretStation turretStation)
        {
            turretStation = null;
            if (aircraft?.weaponStations == null || turret == null)
                return false;

            foreach (var station in StationDiscovery.GetGunnerStations(aircraft))
            {
                if (station == null) continue;
                foreach (var t in station.Turrets)
                {
                    if (t == turret)
                    {
                        turretStation = station;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
