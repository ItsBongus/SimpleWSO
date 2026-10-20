using System.Collections.Generic;
using UnityEngine;

namespace SimpleWSO.Core
{
    /// <summary>
    /// A turret-capable weapon station on an aircraft, plus the handles needed to drive it.
    /// </summary>
    public class TurretStation
    {
        public Aircraft Aircraft;
        public WeaponStation Station;
        public byte Number;
        public List<Turret> Turrets = new List<Turret>();
        public string Label;

        public bool HasTurret => Station != null && (Turrets.Count > 0 || Station.HasTurret());
    }

    public static class StationDiscovery
    {
        /// <summary>
        /// All player-flyable aircraft currently in the scene. Includes remote units.
        /// </summary>
        public static List<Aircraft> FindAircraft()
        {
            var result = new List<Aircraft>();
            foreach (var ac in Object.FindObjectsOfType<Aircraft>())
            {
                if (ac != null) result.Add(ac);
            }
            return result;
        }

        public static Aircraft FindFollowedAircraft()
        {
            var csm = CameraStateManager.i;
            if (csm == null) return null;
            return csm.followingUnit as Aircraft;
        }

        public static bool IsFriendlyAircraft(Aircraft ac)
        {
            if (ac == null) return false;
            var hq = GetLocalFaction();
            return hq == null || ac.NetworkHQ == hq;
        }

        public static bool CanJoinAsGunner(Aircraft ac)
        {
            return ac != null &&
                   !ac.disabled &&
                   IsFriendlyAircraft(ac) &&
                   GetGunnerStations(ac).Count > 0;
        }

        public static bool IsLocalPlayerPiloting()
        {
            if (GameManager.GetLocalAircraft(out Aircraft localAircraft) &&
                localAircraft != null &&
                !localAircraft.disabled)
            {
                return true;
            }

            var fallbackAircraft = FindLocalAircraft();
            return fallbackAircraft != null && !fallbackAircraft.disabled;
        }

        public static FactionHQ GetLocalFaction()
        {
            var localAircraft = FindLocalAircraft();
            if (localAircraft != null)
                return localAircraft.NetworkHQ;

            // Seatless/spectator fallback: use the local Player object if one exists.
            foreach (var player in Object.FindObjectsOfType<NuclearOption.Networking.Player>())
            {
                if (player != null && player.IsLocalPlayer)
                    return player.HQ;
            }

            return null;
        }

        public static string GetAircraftLabel(Aircraft ac)
        {
            if (ac == null) return "(null aircraft)";

            string vehicleName = null;
            if (ac.definition != null)
                vehicleName = ac.definition.unitName;
            if (string.IsNullOrEmpty(vehicleName))
                vehicleName = ac.unitName;
            if (string.IsNullOrEmpty(vehicleName))
                vehicleName = ac.UniqueName;
            if (string.IsNullOrEmpty(vehicleName))
                vehicleName = ac.name;

            var player = ac.Player;
            string playerName = player != null
                ? player.GetDisplayName(NuclearOption.Networking.PlayerNameContext.Other)
                : null;
            if (!string.IsNullOrEmpty(playerName))
                return $"{playerName} - {vehicleName}";

            return $"AI - {vehicleName}";
        }

        /// <summary>
        /// The local player's own aircraft: player-controlled and locally simulated (owner).
        /// Used by the solo proof and the gunner camera fallback.
        /// </summary>
        public static Aircraft FindLocalAircraft()
        {
            if (GameManager.GetLocalAircraft(out Aircraft ac) && ac != null && ac.LocalSim)
                return ac;
            return null;
        }

        /// <summary>
        /// All weapon stations the gunner may select independently of the pilot's
        /// WeaponManager.currentWeaponStation. Includes guns, missiles, etc.
        /// </summary>
        public static List<TurretStation> GetGunnerStations(Aircraft ac)
        {
            var stations = new List<TurretStation>();
            if (ac == null) return stations;

            var wm = ac.weaponManager;
            if (wm == null) return stations;

            var list = ac.weaponStations;
            if (list == null) return stations;

            foreach (var ws in list)
            {
                if (ws == null) continue;
                stations.Add(BuildStation(ac, ws));
            }

            return stations;
        }

        private static TurretStation BuildStation(Aircraft ac, WeaponStation ws)
        {
            var ts = new TurretStation
            {
                Aircraft = ac,
                Station = ws,
                Number = ws.Number,
            };

            if (Reflect.TryGetField<List<Turret>>(ws, "Turrets", out var turrets) && turrets != null)
            {
                foreach (var t in turrets)
                    if (t != null) ts.Turrets.Add(t);
            }
            else
            {
                var single = ws.GetTurret();
                if (single != null) ts.Turrets.Add(single);
            }

            string weaponName = ws.WeaponInfo != null ? ws.WeaponInfo.weaponName : "Unknown";
            if (ts.HasTurret)
                ts.Label = $"{weaponName} (station {ts.Number}, {ts.Turrets.Count} turret{(ts.Turrets.Count == 1 ? "" : "s")})";
            else
                ts.Label = $"{weaponName} (station {ts.Number})";

            return ts;
        }

        public static Aircraft GetAircraft(WeaponStation station)
        {
            if (station?.Weapons == null) return null;
            foreach (var weapon in station.Weapons)
            {
                if (weapon == null) continue;
                if (weapon.attachedUnit is Aircraft ac)
                    return ac;
            }
            return null;
        }
    }
}
