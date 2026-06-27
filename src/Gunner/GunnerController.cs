using System.Collections.Generic;
using SimpleWSO.Core;
using SimpleWSO.Net;
using UnityEngine;

namespace SimpleWSO.Gunner
{
    /// <summary>
    /// Owns the local player's gunner session: camera, station selection, and routing of
    /// aim/fire either directly (we own the aircraft) or over the network (remote owner).
    /// Weapon selection is tracked in GunnerState and shown via CombatHUD locally — it does
    /// NOT change WeaponManager.currentWeaponStation, so the pilot keeps their own weapon.
    /// </summary>
    public class GunnerController
    {
        private readonly VanillaGunnerView _view = new VanillaGunnerView();
        private bool _isOwner;
        private bool _firing;
        private Aircraft _subscribedAircraft;
        private bool _leaving;
        private float _nextViewRepairLog;
        private CameraBaseState _lastGunnerCameraState;
        private string _pendingLeaveReason;

        public void TakeAircraft(Aircraft ac)
        {
            var stations = StationDiscovery.GetGunnerStations(ac);
            if (stations.Count == 0)
            {
                Plugin.Log.LogWarning("[Gunner] Aircraft has no weapon stations.");
                return;
            }

            int stationIndex = 0;
            for (int i = 0; i < stations.Count; i++)
            {
                if (stations[i].HasTurret)
                {
                    stationIndex = i;
                    break;
                }
            }

            TakeStation(ac, stationIndex, stations);
        }

        private void TakeStation(Aircraft ac, int stationIndex, List<TurretStation> stations = null)
        {
            if (ac == null) return;

            stations = stations ?? StationDiscovery.GetGunnerStations(ac);
            if (stations.Count == 0)
            {
                Plugin.Log.LogWarning("[Gunner] Aircraft has no weapon stations.");
                return;
            }
            stationIndex = Mathf.Clamp(stationIndex, 0, stations.Count - 1);

            if (GunnerState.Active) Leave("switching aircraft");

            GunnerState.Active = true;
            GunnerState.TargetAircraft = ac;
            GunnerState.Stations = stations;
            GunnerState.CameraPositionIndex = 0;
            _pendingLeaveReason = null;

            _isOwner = ac.LocalSim;
            SubscribeAircraft(ac);

            SimpleWsoConfig.EnsureCameraOffsetsFor(ac);

            _view.Enter(stations[stationIndex]);
            SelectStation(stationIndex, skipViewRefresh: true);
            _lastGunnerCameraState = CameraStateManager.i != null ? CameraStateManager.i.currentState : null;

            var ts = GunnerState.Current;
            var ownPlane = StationDiscovery.FindLocalAircraft();
            string ownInfo = ownPlane != null
                ? $"netId={ownPlane.NetId} (your stick still flies YOUR plane, not the gunner target)"
                : "none";
            Plugin.Log.LogInfo($"[Gunner] Took {ts.Label} on aircraft netId={ac.NetId}. isOwner={_isOwner}.");
            Plugin.Log.LogInfo($"[Gunner] Weapon cycle: vanilla 'Next Weapon' / 'Previous Weapon'. Fire: vanilla 'Fire'. Your aircraft: {ownInfo}.");
        }

        public void CycleStation(int delta)
        {
            if (!GunnerState.Active || GunnerState.Stations.Count == 0) return;
            int count = GunnerState.Stations.Count;
            int next = (GunnerState.StationIndex + delta + count) % count;
            SelectStation(next);
        }

        public void Leave() => Leave("manual");

        public void Leave(string reason)
        {
            if (_leaving) return;
            if (!GunnerState.Active && _subscribedAircraft == null && !_view.Active) return;
            _leaving = true;

            try
            {
                SetFiring(false);
                ReleaseCurrentStation();
                UnsubscribeAircraft();
                TurretController.CleanupAll();

                _view.Exit();
                GunnerState.Reset();
                Plugin.Log.LogInfo($"[Gunner] Left station ({reason}).");
            }
            finally
            {
                _isOwner = false;
                _firing = false;
                _lastGunnerCameraState = null;
                _pendingLeaveReason = null;
                _leaving = false;
            }
        }

        public void Update()
        {
            if (!GunnerState.Active) return;

            if (_pendingLeaveReason != null)
            {
                string reason = _pendingLeaveReason;
                _pendingLeaveReason = null;
                Leave(reason);
                return;
            }

            if (!ValidateSession())
                return;

            // Vanilla weapon-cycle actions (same names PilotPlayerState uses).
            if (RewiredInput.GetActionTimedPressUp("Next Weapon"))
            {
                CycleStation(+1);
                return;
            }
            if (RewiredInput.GetActionTimedPressUp("Previous Weapon"))
            {
                CycleStation(-1);
                return;
            }
            if (RewiredInput.GetKeyDown(SimpleWsoConfig.CycleCameraPositionKey.Value))
            {
                CycleCameraPosition();
                return;
            }

            var ts = GunnerState.Current;
            if (ts == null) return;

            var csm = CameraStateManager.i;
            if (!IsValidGunnerCameraState(csm))
            {
                if (csm == null || csm.followingUnit != GunnerState.TargetAircraft)
                {
                    Leave("camera left gunner aircraft");
                    return;
                }

                _view.Reattach(ts);
                if (Time.time >= _nextViewRepairLog)
                {
                    _nextViewRepairLog = Time.time + 5f;
                    Plugin.Log.LogWarning("[View] Cockpit camera was detached unexpectedly; reattached.");
                }
            }
            else
            {
                RefreshCockpitUiAfterViewCycle(csm, ts);
            }

            Vector3 dir = _view.AimForward;

            if (_isOwner)
            {
                TurretController.ApplyGunnerStationTargets(ts);
            }
            else
            {
                SimpleWsoNet.SendAim(ts.Aircraft.NetId, ts.Number, dir, _firing, GunnerState.TargetList);
                Unit target = GunnerState.PrimaryTarget();
                TurretController.ApplyLocalStationTarget(ts, target);
                if (ts.HasTurret)
                {
                    TurretController.Aim(ts, dir, target, driveTurretAim: target == null);
                }
            }

            if (ts.HasTurret && _isOwner)
            {
                Unit target = GunnerState.PrimaryTarget();
                TurretController.Aim(ts, dir, target, driveTurretAim: target == null);
            }

            bool fireHeld = RewiredInput.GetAction("Fire");
            if (fireHeld != _firing) SetFiring(fireHeld);

            if (_firing && _isOwner)
                GunnerWeaponFire.FireStation(ts);
        }

        private bool ValidateSession()
        {
            var ac = GunnerState.TargetAircraft;
            if (ac == null)
            {
                Leave("target aircraft destroyed");
                return false;
            }

            if (ac.disabled)
            {
                Leave("target aircraft disabled");
                return false;
            }

            if (!IsGunnerCockpitViable(ac))
            {
                Leave("pilot ejected or cockpit unavailable");
                return false;
            }

            var ts = GunnerState.Current;
            if (ts == null || ts.Aircraft == null || ts.Station == null)
            {
                Leave("selected station invalid");
                return false;
            }

            return true;
        }

        private static bool IsValidGunnerCameraState(CameraStateManager csm)
        {
            if (csm == null || GunnerState.TargetAircraft == null)
                return false;

            if (csm.followingUnit != GunnerState.TargetAircraft)
                return false;

            CameraBaseState state = csm.currentState;
            return state == csm.cockpitState ||
                   state == csm.orbitState ||
                   state == csm.TVState ||
                   state == csm.chaseState;
        }

        private void RefreshCockpitUiAfterViewCycle(CameraStateManager csm, TurretStation ts)
        {
            CameraBaseState previousState = _lastGunnerCameraState;
            CameraBaseState currentState = csm != null ? csm.currentState : null;
            _lastGunnerCameraState = currentState;

            if (currentState == null || currentState != csm.cockpitState || previousState == csm.cockpitState)
                return;

            _view.RefreshWeaponStation(ts);
            Plugin.LogVerbose("[View] Refreshed gunner cockpit UI after camera cycle.");
        }

        /// <summary>
        /// Vanilla cockpit cam bails to freeState when pilots[0].dead; detached/ejected
        /// airframes also break cockpit follow. Leave once instead of reattach-fighting.
        /// </summary>
        private static bool IsGunnerCockpitViable(Aircraft ac)
        {
            if (ac == null) return false;
            if (ac.HasEjected()) return false;
            if (ac.pilots == null || ac.pilots.Length == 0) return true;
            Pilot pilot = ac.pilots[0];
            return pilot == null || !pilot.dead;
        }

        private void SelectStation(int stationIndex, bool skipViewRefresh = false)
        {
            stationIndex = Mathf.Clamp(stationIndex, 0, GunnerState.Stations.Count - 1);
            if (GunnerState.StationIndex == stationIndex && GunnerState.Current != null && !skipViewRefresh)
                return;

            ReleaseCurrentStation();

            GunnerState.StationIndex = stationIndex;
            var ts = GunnerState.Current;
            if (ts == null) return;

            if (!skipViewRefresh)
                _view.RefreshWeaponStation(ts);

            if (_isOwner)
            {
                if (ts.HasTurret)
                    TurretController.EngageManual(ts);
            }
            else
            {
                SimpleWsoNet.SendJoin(ts.Aircraft.NetId, ts.Number);
            }

            Plugin.Log.LogInfo($"[Gunner] Selected {ts.Label}");
        }

        private void CycleCameraPosition()
        {
            int count = SimpleWsoConfig.GetCameraPositionCount(GunnerState.TargetAircraft);
            if (count <= 1)
            {
                GunnerState.CameraPositionIndex = 0;
                Plugin.Log.LogInfo("[View] This aircraft has only one configured gunner camera position.");
                return;
            }

            GunnerState.CameraPositionIndex = (GunnerState.CameraPositionIndex + 1) % count;
            Plugin.Log.LogInfo($"[View] Gunner camera position {GunnerState.CameraPositionIndex + 1}/{count}.");
        }

        private void ReleaseCurrentStation()
        {
            var ts = GunnerState.Current;
            if (ts == null) return;
            try
            {
                if (_isOwner)
                    TurretController.ReleaseManual(ts);
                else if (ts.Aircraft != null)
                    SimpleWsoNet.SendLeave(ts.Aircraft.NetId, ts.Number);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Gunner] Release station failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private void SetFiring(bool firing)
        {
            if (_firing == firing) return;
            _firing = firing;
            var ts = GunnerState.Current;
            if (ts == null) return;

            Plugin.LogVerbose($"[Fire] {(firing ? "PRESSED" : "released")} - {TurretController.DescribeFire(ts)}");

            if (!_isOwner)
                SimpleWsoNet.SendFire(ts.Aircraft.NetId, ts.Number, firing, GunnerState.TargetList);
        }

        private void SubscribeAircraft(Aircraft ac)
        {
            UnsubscribeAircraft();
            _subscribedAircraft = ac;
            if (_subscribedAircraft != null)
                _subscribedAircraft.onDisableUnit += OnTargetAircraftDisabled;
        }

        private void UnsubscribeAircraft()
        {
            if (_subscribedAircraft != null)
            {
                _subscribedAircraft.onDisableUnit -= OnTargetAircraftDisabled;
            }
            _subscribedAircraft = null;
        }

        /// <summary>
        /// Vanilla fires onDisableUnit from INSIDE Unit.UnitDisabled, which runs synchronously
        /// during the crash / ReturnToInventory teardown. Running our full Leave() teardown here
        /// (camera state changes, HUD detach, turret cleanup) — or worse, throwing — unwinds
        /// vanilla's own disable sequence before it reaches WaitRemoveAircraft()/Destroy(), so the
        /// airframe is never removed. Record the intent only; the next Update() tick performs the
        /// actual Leave() outside vanilla's call stack. Never let this method throw into vanilla.
        /// </summary>
        private void OnTargetAircraftDisabled(Unit unit)
        {
            try
            {
                _pendingLeaveReason = "target aircraft shot down";
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Gunner] onDisableUnit handler error (ignored): {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
