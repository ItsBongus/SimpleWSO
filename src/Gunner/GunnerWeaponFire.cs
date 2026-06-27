using System.Collections;
using System.Collections.Generic;
using SimpleWSO.Core;
using SimpleWSO.Net;
using UnityEngine;

namespace SimpleWSO.Gunner
{
    /// <summary>
    /// Single authorized fire path for gunner-selected stations. WeaponStation.Fire is
    /// patched so vanilla WeaponManager / turret auto-fire cannot double-tap the same station.
    /// </summary>
    public static class GunnerWeaponFire
    {
        private static int _authorizeDepth;
        private static float _lastFireLog;
        private static int _cleanupToken;
        private static readonly HashSet<WeaponStation> ActiveSalvos = new HashSet<WeaponStation>();

        public static bool IsAuthorized => _authorizeDepth > 0;

        public static bool IsGunnerControlledStation(WeaponStation station, Aircraft owner)
        {
            if (station == null || owner == null || owner.disabled) return false;

            if (GunnerState.Active &&
                GunnerState.TargetAircraft == owner &&
                GunnerState.Current != null &&
                GunnerState.Current.Station == station)
            {
                return true;
            }

            return SimpleWsoNet.IsRemoteGunnerStation(owner.NetId, station.Number);
        }

        public static bool IsTurretCapableStation(WeaponStation station)
        {
            if (station == null) return false;
            if (station.HasTurret()) return true;
            return Reflect.TryGetField<List<Turret>>(station, "Turrets", out var turrets) &&
                   turrets != null &&
                   turrets.Count > 0;
        }

        public static bool AllowVanillaFire(WeaponStation station, Aircraft owner)
        {
            if (IsAuthorized) return true;
            return !IsTurretCapableStation(station) || !IsGunnerControlledStation(station, owner);
        }

        public static void FireStation(TurretStation ts, Unit targetOverride = null, IList<Unit> salvoTargets = null)
        {
            if (ts == null || ts.Station == null || ts.Aircraft == null) return;
            if (!ts.Station.Ready()) return;
            if (StationSafetyBlocksFire(ts)) return;

            _authorizeDepth++;
            try
            {
                int before = ts.Station.Ammo;
                Unit target = targetOverride ?? GunnerState.PrimaryTarget();
                List<Unit> targets = salvoTargets != null
                    ? TargetListUtil.ValidTargets(salvoTargets)
                    : TargetListUtil.ValidTargets(GunnerState.TargetList);
                string path;

                WeaponInfo info = ts.Station.WeaponInfo;
                if (info != null && !info.gun && info.fireInterval != 0f && !info.sling)
                {
                    if (targets.Count > 1)
                    {
                        if (!ts.Station.SalvoInProgress && Plugin.Instance != null)
                        {
                            ts.Station.SalvoInProgress = true;
                            ActiveSalvos.Add(ts.Station);
                            Plugin.Instance.StartCoroutine(SalvoLaunch(ts, targets, info.fireInterval * 1.1f, _cleanupToken));
                        }
                        path = $"SalvoLaunch({targets.Count})";
                    }
                    else
                    {
                        if (target != null)
                            TurretController.ApplyWeaponTargets(ts, target);
                        LaunchSingleMount(ts, target);
                        path = "LaunchMount";
                    }
                }
                else
                {
                    if (target != null)
                        TurretController.ApplyWeaponTargets(ts, target);
                    ClearGunnerGunSafety(ts);
                    ts.Station.Fire(ts.Aircraft, target);
                    path = "WeaponStation.Fire";
                }

                if (Time.time - _lastFireLog > 1f)
                {
                    _lastFireLog = Time.time;
                    Plugin.LogVerbose($"[Fire] {path} station={ts.Number} target={(target != null ? target.name : "none")} ammoBefore={before} ammoAfter={ts.Station.Ammo}");
                }
            }
            finally
            {
                _authorizeDepth--;
            }
        }

        public static void CleanupAll()
        {
            _cleanupToken++;

            foreach (var station in new List<WeaponStation>(ActiveSalvos))
            {
                if (station != null) station.SalvoInProgress = false;
            }
            ActiveSalvos.Clear();
        }

        private static IEnumerator SalvoLaunch(TurretStation ts, List<Unit> targets, float interval, int token)
        {
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    if (token != _cleanupToken) yield break;
                    if (ts == null || ts.Station == null || ts.Aircraft == null) yield break;
                    Unit target = targets[i];
                    if (target != null && !target.disabled && ts.Station.Ready())
                    {
                        if (StationSafetyBlocksFire(ts)) yield break;
                        int before = ts.Station.Ammo;
                        LaunchSingleMount(ts, target);
                        Plugin.LogVerbose($"[Fire] SalvoLaunch station={ts.Number} target={target.name} ammoBefore={before} ammoAfter={ts.Station.Ammo}");
                    }

                    if (i < targets.Count - 1)
                        yield return new WaitForSeconds(interval);
                }
            }
            finally
            {
                if (ts != null && ts.Station != null)
                {
                    ts.Station.SalvoInProgress = false;
                    ActiveSalvos.Remove(ts.Station);
                }
            }
        }

        private static void LaunchSingleMount(TurretStation ts, Unit target)
        {
            _authorizeDepth++;
            try
            {
                GlobalPosition aimpoint = GlobalPositionExtensions.GlobalPosition(ts.Aircraft) +
                                          ts.Aircraft.transform.forward * 50000f;
                ts.Station.LaunchMount(ts.Aircraft, target, aimpoint);
            }
            finally
            {
                _authorizeDepth--;
            }
        }

        private static bool StationSafetyBlocksFire(TurretStation ts)
        {
            if (ts?.Station == null || ts.Aircraft == null)
                return true;

            // Ibis-style multi-turret gun stations intentionally keep vanilla per-weapon
            // Safety so only the side(s) with an aim solution fire.
            if (IsMultiTurretGunStation(ts))
                return false;

            try
            {
                object result = Reflect.Call(ts.Station, "SafetyIsOn", ts.Aircraft);
                if (result is bool safetyOn && safetyOn)
                {
                    if (Time.time - _lastFireLog > 1f)
                    {
                        _lastFireLog = Time.time;
                        Plugin.LogVerbose($"[Fire] blocked by station safety station={ts.Number}");
                    }
                    return true;
                }
            }
            catch (System.Exception e)
            {
                Plugin.LogVerbose($"[Fire] Safety check failed station={ts.Number}: {e.GetType().Name}");
            }

            return false;
        }

        private static void ClearGunnerGunSafety(TurretStation ts)
        {
            if (ts?.Station?.WeaponInfo == null || !ts.Station.WeaponInfo.gun || ts.Station.Weapons == null)
                return;

            // Multi-turret gun stations (Ibis door guns) use each turret's aim solution to
            // set weapon Safety. Preserving that lets vanilla fire only the side(s) on target.
            if (IsMultiTurretGunStation(ts))
                return;

            foreach (var weapon in ts.Station.Weapons)
            {
                if (weapon != null)
                    weapon.Safety = false;
            }
        }

        private static bool IsMultiTurretGunStation(TurretStation ts)
        {
            return ts?.Station?.WeaponInfo != null &&
                   ts.Station.WeaponInfo.gun &&
                   ts.Turrets != null &&
                   ts.Turrets.Count > 1;
        }
    }
}
