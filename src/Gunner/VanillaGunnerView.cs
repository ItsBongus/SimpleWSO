using System;
using System.Collections;
using System.Reflection;
using SimpleWSO.Core;
using UnityEngine;

namespace SimpleWSO.Gunner
{
    /// <summary>
    /// Puts the local player into the game's OWN cockpit camera for a chosen aircraft, using
    /// CameraStateManager. This reuses the vanilla freelook + zoom + turret-tracking view that
    /// the game already provides for gunship cockpits, instead of building a custom camera.
    ///
    /// The gunner's look direction (camera forward) is what we relay to the aircraft owner so
    /// the owner can aim the turret where the gunner is looking.
    /// </summary>
    public class VanillaGunnerView
    {
        private Unit _restoreUnit;
        private CameraBaseState _restoreState;
        private Aircraft _restoreHudAircraft;
        private WeaponStation _restoreHudStation;
        private System.Collections.Generic.List<Unit> _restoreHudTargetList;
        private Aircraft _boundGunnerAircraft;
        private System.Collections.Generic.List<Unit> _restoreLaserTargetList;
        private GameObject _spawnedStatusDisplay;
        private Aircraft _cockpitUiAircraft;
        private bool _restoreMapMaximized;
        private bool _restoreCombatHud;
        private bool _active;

        public bool Active => _active;

        /// <summary>World-space direction the gunner is looking (the vanilla camera forward).</summary>
        public Vector3 AimForward
        {
            get
            {
                var csm = CameraStateManager.i;
                if (csm != null && csm.mainCamera != null)
                    return csm.mainCamera.transform.forward;
                return Vector3.forward;
            }
        }

        public void Enter(TurretStation ts)
        {
            if (_active || ts == null || ts.Aircraft == null) return;

            Aircraft ac = ts.Aircraft;

            var csm = CameraStateManager.i;
            if (csm == null)
            {
                Plugin.Log.LogWarning("[View] CameraStateManager.i is null; cannot switch view.");
                return;
            }

            try
            {
                // Remember where to send the player back when they leave.
                _restoreUnit = csm.followingUnit;
                Reflect.TryGetField<CameraBaseState>(csm, "<currentState>k__BackingField", out _restoreState);
                _restoreMapMaximized = DynamicMap.mapMaximized;
                _restoreCombatHud = ShouldRestoreCombatHud(csm);
                Plugin.LogVerbose($"[ViewState] Enter capture follow={DescribeUnit(_restoreUnit)} state={DescribeState(_restoreState)} mapMax={_restoreMapMaximized} cursor={CursorManager.GetFlags()} target={DescribeUnit(ac)}");

                // Bind CombatHUD before SetFollowingUnit so UnitDebug (spectator intel overlay)
                // sees followed unit == CombatHUD.aircraft and stays hidden.
                BindCombatHud(ts);
                csm.SetFollowingUnit(ac);
                if (csm.cockpitState != null)
                    csm.SwitchState(csm.cockpitState);
                SetupCockpitUi(ts);

                _active = true;
                Plugin.Log.LogInfo($"[View] Entered cockpit view of unit {ac.NetId} and bound HUD to station {ts.Number}.");
                Plugin.LogVerbose($"[ViewState] Enter after follow={DescribeUnit(csm.followingUnit)} state={DescribeState(csm.currentState)} mapMax={DynamicMap.mapMaximized} cursor={CursorManager.GetFlags()}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"[View] Enter failed: {e}");
            }
        }

        public void Exit()
        {
            if (!_active) return;
            _active = false;

            var csm = CameraStateManager.i;

            try
            {
                DestroyCockpitUi();

                if (csm != null)
                {
                    Plugin.LogVerbose($"[ViewState] Exit before follow={DescribeUnit(csm.followingUnit)} state={DescribeState(csm.currentState)} restoreFollow={DescribeUnit(_restoreUnit)} restoreState={DescribeState(_restoreState)} restoreMapMax={_restoreMapMaximized} mapMax={DynamicMap.mapMaximized} cursor={CursorManager.GetFlags()}");

                    Unit restoreUnit = ResolveRestoreUnit(csm);
                    if (restoreUnit != null)
                    {
                        csm.SetFollowingUnit(restoreUnit);
                    }
                    else if (_restoreUnit != null)
                    {
                        Plugin.Log.LogInfo($"[ViewLifecycle] Clearing camera follow instead of restoring invalid unit {DescribeUnit(_restoreUnit)}.");
                        csm.SetFollowingUnit(null);
                    }

                    CameraBaseState restoreState = ResolveRestoreState(csm);
                    Plugin.LogVerbose($"[ViewState] Exit resolved restoreState={DescribeState(restoreState)} followAfterSet={DescribeUnit(csm.followingUnit)}");
                    if (restoreState != null)
                        csm.SwitchState(restoreState);
                    else if (csm.cockpitState != null)
                        csm.SwitchState(csm.cockpitState);

                    ForceSpectatorUiState(_restoreMapMaximized);
                    Plugin.LogVerbose($"[ViewState] Exit after force follow={DescribeUnit(csm.followingUnit)} state={DescribeState(csm.currentState)} mapMax={DynamicMap.mapMaximized} cursor={CursorManager.GetFlags()}");
                }

                RestoreCombatHud();

                Plugin.Log.LogInfo("[View] Restored previous camera/HUD.");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"[View] Exit failed: {e}");
            }
            finally
            {
                _restoreUnit = null;
                _restoreState = null;
                _restoreHudAircraft = null;
                _restoreHudStation = null;
                _restoreHudTargetList = null;
                _restoreMapMaximized = false;
                _restoreCombatHud = false;
                DestroyCockpitUi();
                RestoreLaserTargetList();
            }
        }

        private static bool ShouldRestoreCombatHud(CameraStateManager csm)
        {
            if (csm == null) return false;
            Aircraft localAircraft = StationDiscovery.FindLocalAircraft();
            return localAircraft != null && csm.followingUnit == localAircraft && csm.currentState == csm.cockpitState;
        }

        private CameraBaseState ResolveRestoreState(CameraStateManager csm)
        {
            if (csm == null) return _restoreState;

            if (IsInvalidRestoreUnit(_restoreUnit) && csm.freeState != null)
            {
                Plugin.LogVerbose("[ViewState] Restore unit is invalid; using freeState.");
                return csm.freeState;
            }

            // If gunner was entered from vanilla map/unit targeting, don't restore that
            // interaction mode. Return to a normal spectator orbit of the followed unit.
            if (_restoreState == csm.selectionState && csm.orbitState != null)
            {
                Plugin.LogVerbose("[ViewState] Restore state was selectionState; using orbitState.");
                return csm.orbitState;
            }

            if (_restoreState == csm.cockpitState && _restoreUnit != StationDiscovery.FindLocalAircraft() && csm.orbitState != null)
            {
                Plugin.LogVerbose("[ViewState] Restore state was remote cockpitState; using orbitState.");
                return csm.orbitState;
            }

            return _restoreState;
        }

        private Unit ResolveRestoreUnit(CameraStateManager csm)
        {
            if (!IsInvalidRestoreUnit(_restoreUnit))
                return _restoreUnit;

            return null;
        }

        private static bool IsInvalidRestoreUnit(Unit unit)
        {
            if (unit == null) return false;
            var aircraft = unit as Aircraft;
            return aircraft != null && (aircraft.disabled || aircraft.HasEjected());
        }

        private static string DescribeState(CameraBaseState state)
            => state != null ? state.GetType().Name : "null";

        private static string DescribeUnit(Unit unit)
        {
            if (unit == null) return "null";
            string name = !string.IsNullOrEmpty(unit.unitName) ? unit.unitName : unit.name;
            return $"{name}/net={unit.NetId}";
        }

        private void BindCombatHud(TurretStation ts)
        {
            var hud = CombatHUD.i;
            if (hud == null) return;

            if (_restoreHudAircraft == null)
            {
                _restoreHudAircraft = hud.aircraft;
                _restoreHudStation = hud.GetWeaponStation();
                Reflect.TryGetField<System.Collections.Generic.List<Unit>>(hud, "targetList", out _restoreHudTargetList);
            }

            if (hud.aircraft != ts.Aircraft)
            {
                if (_cockpitUiAircraft != null && _cockpitUiAircraft != ts.Aircraft)
                    DestroyCockpitUi();

                CleanupHudAircraft(hud.aircraft);
                if (hud.aircraft != null)
                    hud.RemoveAircraft();
                hud.SetAircraft(ts.Aircraft);
            }

            hud.ShowWeaponStation(ts.Station);
            ApplyGunnerTargetingMode(ts.Station, ts.Aircraft);
            BindGunnerTargetList(hud, ts.Aircraft);
            HideUnitDebug();
        }

        /// <summary>
        /// ShowWeaponStation already calls HUDOptions.AutomaticToggle (A2A/A2G/LOG/etc).
        /// Push filter updates immediately instead of waiting for HUDOptions' 1s debounce.
        /// </summary>
        private static void ApplyGunnerTargetingMode(WeaponStation station, Aircraft aircraft)
        {
            if (station == null || aircraft == null) return;

            var options = HUDOptions.i;
            if (options != null)
                options.ApplyHUDSettings();

            var selector = TargetListSelector.i;
            if (selector != null)
                selector.SetFilters();
        }

        /// <summary>
        /// Spectator follow overlay (g-force, missile attack count, coords, etc.).
        /// Vanilla hides it when the followed unit equals CombatHUD.aircraft; we also
        /// force-hide after binding since SetFollowingUnit may have fired earlier.
        /// </summary>
        private static void HideUnitDebug()
        {
            var debug = UnityEngine.Object.FindObjectOfType<UnitDebug>();
            if (debug == null) return;
            debug.enabled = false;
            debug.gameObject.SetActive(false);
        }

        /// <summary>
        /// Update local HUD to the gunner's selected station without touching
        /// WeaponManager.currentWeaponStation (pilot selection stays independent).
        /// </summary>
        public void RefreshWeaponStation(TurretStation ts)
        {
            if (!_active || ts == null) return;
            BindGunnerCockpitUi(ts);
        }

        public bool IsAttachedTo(Aircraft aircraft)
        {
            if (!_active || aircraft == null) return false;
            var csm = CameraStateManager.i;
            return csm != null &&
                   csm.followingUnit == aircraft &&
                   csm.currentState == csm.cockpitState;
        }

        public void Reattach(TurretStation ts)
        {
            if (!_active || ts == null || ts.Aircraft == null) return;
            var csm = CameraStateManager.i;
            if (csm == null) return;

            BindCombatHud(ts);
            csm.SetFollowingUnit(ts.Aircraft);
            if (csm.cockpitState != null)
                csm.SwitchState(csm.cockpitState);
            SetupCockpitUi(ts);
            Plugin.Log.LogInfo($"[View] Reattached cockpit view to unit {ts.Aircraft.NetId}.");
        }

        /// <summary>
        /// UI-only slice of vanilla SetupLocalPlayerAndUI: CombatHUD, FlightHud,
        /// StatusDisplay, and minimap — without pilot attach or flight controls.
        /// </summary>
        private void BindGunnerCockpitUi(TurretStation ts)
        {
            if (ts == null || ts.Aircraft == null) return;
            BindCombatHud(ts);
            SetupCockpitUi(ts);
        }

        private void SetupCockpitUi(TurretStation ts)
        {
            if (ts == null || ts.Aircraft == null) return;

            Aircraft ac = ts.Aircraft;
            if (_cockpitUiAircraft != ac)
            {
                DestroyCockpitUi();
                SpawnCockpitUi(ac);
                _cockpitUiAircraft = ac;
            }

            RefreshCockpitUiState(ac);
            ForceCockpitUiState();
            RefreshSupplementalCockpitUi(ac);
            ResetGunnerCameraEffects();
        }

        private void SpawnCockpitUi(Aircraft ac)
        {
            if (ac == null) return;

            AircraftParameters parameters = ac.GetAircraftParameters();
            if (parameters == null || parameters.StatusDisplay == null) return;

            _spawnedStatusDisplay = UnityEngine.Object.Instantiate(
                parameters.StatusDisplay, Vector3.zero, Quaternion.identity);
            StatusDisplay statusDisplay = _spawnedStatusDisplay.GetComponent<StatusDisplay>();
            if (statusDisplay != null)
                statusDisplay.Initialize(ac);
        }

        private void RefreshSupplementalCockpitUi(Aircraft ac)
        {
            if (ac == null || _spawnedStatusDisplay == null) return;

            try
            {
                // Some airframe-specific displays (notably capacitor/charge indicators)
                // subscribe through CombatHUD's aircraft binding. We bind the HUD before
                // spawning the UI-only status display, so refresh those components once.
                foreach (ChargeIndicator indicator in _spawnedStatusDisplay.GetComponentsInChildren<ChargeIndicator>(true))
                {
                    if (indicator == null) continue;
                    TryCall(indicator, "ChargeIndicator_OnSetAircraft", CombatHUD.i);
                    TryCall(indicator, "UpdateDisplay");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[View] Supplemental cockpit UI refresh failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private void DestroyCockpitUi()
        {
            if (_spawnedStatusDisplay != null)
            {
                DetachStatusDisplayFromDisable(_spawnedStatusDisplay, _cockpitUiAircraft);
                UnityEngine.Object.Destroy(_spawnedStatusDisplay);
                _spawnedStatusDisplay = null;
            }
            _cockpitUiAircraft = null;
        }

        /// <summary>
        /// Vanilla StatusDisplay.Initialize() does aircraft.onDisableUnit += StatusDisplay_OnDisable,
        /// and StatusDisplay.OnDestroy() does NOT remove it — vanilla expects the display to live
        /// until the aircraft disables and self-destructs in StatusDisplay_OnDisable. We destroy our
        /// spawned display when leaving the seat, so we must remove that subscription ourselves.
        /// Otherwise the stale delegate fires on a destroyed component when the airframe later
        /// disables (NullReferenceException in get_gameObject), which unwinds vanilla's UnitDisabled
        /// / ReturnToInventory before WaitRemoveAircraft() and the airframe never despawns.
        /// </summary>
        private static void DetachStatusDisplayFromDisable(GameObject statusDisplayGo, Aircraft aircraft)
        {
            if (statusDisplayGo == null || aircraft == null) return;

            try
            {
                StatusDisplay statusDisplay = statusDisplayGo.GetComponent<StatusDisplay>();
                if (statusDisplay == null) return;

                RemoveEventHandler(aircraft, "remove_onDisableUnit", statusDisplay, "StatusDisplay_OnDisable");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[View] StatusDisplay onDisableUnit detach failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void RefreshCockpitUiState(Aircraft ac)
        {
            if (ac == null) return;

            FlightHud flightHud = FlightHud.i;
            if (flightHud != null)
            {
                flightHud.SetAircraft(ac);
                FlightHud.EnableCanvas(true);
            }

            if (!DynamicMap.mapMaximized && DynamicMap.i != null)
                Reflect.Call(DynamicMap.i, "Minimize");
        }

        private static void ForceCockpitUiState()
        {
            try
            {
                DynamicMap.AllowedToOpen = true;
                if (DynamicMap.i != null)
                {
                    Plugin.LogVerbose($"[ViewState] ForceCockpit before mapMax={DynamicMap.mapMaximized} cursor={CursorManager.GetFlags()}");
                    Reflect.Call(DynamicMap.i, "Minimize");
                }
                else
                {
                    DynamicMap.EnableCanvas(true);
                }

                FlightHud.EnableCanvas(true);
                CursorManager.SetFlag(CursorFlags.Map, false);
                CursorManager.Refresh();
                Plugin.LogVerbose($"[ViewState] ForceCockpit after mapMax={DynamicMap.mapMaximized} cursor={CursorManager.GetFlags()}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[View] Cockpit UI reset failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void ResetGunnerCameraEffects()
        {
            try
            {
                foreach (GLOC gloc in UnityEngine.Object.FindObjectsOfType<GLOC>())
                    Reflect.Call(gloc, "ResetGLOC");

                var csm = CameraStateManager.i;
                object blackout = csm != null ? Reflect.Call(csm, "GetBlackoutImage") : null;
                if (blackout != null)
                {
                    var colorProperty = blackout.GetType().GetProperty("color");
                    if (colorProperty == null) return;

                    Color color = (Color)colorProperty.GetValue(blackout, null);
                    color.a = 0f;
                    colorProperty.SetValue(blackout, color, null);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[View] Camera effect reset failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void ForceSpectatorUiState(bool restoreMapMaximized)
        {
            try
            {
                DynamicMap.AllowedToOpen = true;
                if (DynamicMap.i != null)
                {
                    Plugin.LogVerbose($"[ViewState] ForceSpectator before restoreMapMax={restoreMapMaximized} mapMax={DynamicMap.mapMaximized} cursor={CursorManager.GetFlags()}");
                    TryCall(DynamicMap.i, "UnselectAll");
                    Reflect.Call(DynamicMap.i, restoreMapMaximized ? "Maximize" : "Minimize");
                }
                else
                {
                    DynamicMap.EnableCanvas(true);
                }

                CursorManager.SetFlag(CursorFlags.SelectionMenu, false);
                if (!restoreMapMaximized)
                    CursorManager.SetFlag(CursorFlags.Map, false);
                CursorManager.Refresh();
                Plugin.LogVerbose($"[ViewState] ForceSpectator after restoreMapMax={restoreMapMaximized} mapMax={DynamicMap.mapMaximized} cursor={CursorManager.GetFlags()}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[View] Spectator UI reset failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private void RestoreCombatHud()
        {
            var hud = CombatHUD.i;
            if (hud == null) return;

            CleanupHudAircraft(hud.aircraft);
            if (hud.aircraft != null)
                hud.RemoveAircraft();

            if (_restoreCombatHud && _restoreHudAircraft != null)
            {
                hud.SetAircraft(_restoreHudAircraft);
                hud.ShowWeaponStation(_restoreHudStation);
                if (_restoreHudTargetList != null)
                    Reflect.SetField(hud, "targetList", _restoreHudTargetList);

                FlightHud flightHud = FlightHud.i;
                if (flightHud != null)
                {
                    flightHud.SetAircraft(_restoreHudAircraft);
                    FlightHud.EnableCanvas(true);
                }
            }
            else
            {
                FlightHud.EnableCanvas(false);
                Plugin.LogVerbose("[ViewState] Detached gunner HUD for spectator return.");
            }
        }

        private void BindGunnerTargetList(CombatHUD hud, Aircraft aircraft)
        {
            Reflect.SetField(hud, "targetList", GunnerState.TargetList);

            if (_boundGunnerAircraft == aircraft) return;

            RestoreLaserTargetList();
            _boundGunnerAircraft = aircraft;

            var designator = aircraft != null ? aircraft.GetLaserDesignator() : null;
            if (designator == null) return;

            Reflect.TryGetField<System.Collections.Generic.List<Unit>>(designator, "targetList", out _restoreLaserTargetList);
            Reflect.SetField(designator, "targetList", GunnerState.TargetList);
        }

        private void RestoreLaserTargetList()
        {
            if (_boundGunnerAircraft == null) return;

            var designator = _boundGunnerAircraft.GetLaserDesignator();
            if (designator != null && _restoreLaserTargetList != null)
                Reflect.SetField(designator, "targetList", _restoreLaserTargetList);

            _boundGunnerAircraft = null;
            _restoreLaserTargetList = null;
        }

        public static void CleanupHudAircraft(Aircraft aircraft)
        {
            if (aircraft == null) return;

            try
            {
                var hud = CombatHUD.i;
                if (hud == null) return;

                // CombatHUD.RemoveAircraft cleans onJam, but ThreatList.SetAircraft subscribes
                // missile-warning callbacks and owns the looping incoming-missile alarms.
                if (!Reflect.TryGetField<ThreatList>(hud, "threatList", out var threatList) || threatList == null)
                    return;

                DisableThreatList(threatList, aircraft);
                ClearThreatItems(threatList);

                var missileWarning = aircraft.GetMissileWarningSystem();
                if (missileWarning != null)
                {
                    RemoveEventHandler(missileWarning, "remove_onMissileWarning", threatList, "ThreatList_OnMissileWarning");
                    RemoveEventHandler(missileWarning, "remove_offMissileWarning", threatList, "ThreatList_OffMissileWarning");
                }

                RemoveEventHandler(aircraft, "remove_onDisableUnit", threatList, "ThreatList_OnAircraftDisable");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[View] Threat cleanup failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void DisableThreatList(ThreatList threatList, Aircraft aircraft)
        {
            // Vanilla path: destroys alarm AudioSources and unsubscribes missile-warning events.
            try
            {
                Reflect.Call(threatList, "ThreatList_OnAircraftDisable", aircraft);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[View] Vanilla threat disable cleanup failed: {e.GetType().Name}: {e.Message}");
                if (Reflect.TryGetField<Array>(threatList, "alarmTypes", out var alarms) && alarms != null)
                {
                    foreach (object alarm in alarms)
                    {
                        if (alarm == null) continue;
                        TryCall(alarm, "Remove");
                    }
                }
            }
        }

        private static void ClearThreatItems(ThreatList threatList)
        {
            if (!Reflect.TryGetField<IDictionary>(threatList, "itemLookup", out var items) || items == null)
                return;

            foreach (DictionaryEntry entry in items)
            {
                if (entry.Value is Component component)
                    UnityEngine.Object.Destroy(component.gameObject);
                else if (entry.Value is GameObject gameObject)
                    UnityEngine.Object.Destroy(gameObject);
            }
            items.Clear();
        }

        private static void TryCall(object instance, string method)
        {
            try
            {
                Reflect.Call(instance, method);
            }
            catch
            {
                // Best-effort cleanup; missing methods should not block leaving the seat.
            }
        }

        private static void TryCall(object instance, string method, params object[] args)
        {
            try
            {
                Reflect.Call(instance, method, args);
            }
            catch
            {
                // Best-effort refresh; optional airframe widgets vary by aircraft.
            }
        }

        private static void RemoveEventHandler(object source, string removeMethodName, object target, string handlerName)
        {
            MethodInfo remove = source.GetType().GetMethod(removeMethodName, BindingFlags.Public | BindingFlags.Instance);
            MethodInfo handler = target.GetType().GetMethod(handlerName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (remove == null || handler == null) return;

            Type delegateType = remove.GetParameters()[0].ParameterType;
            Delegate del = Delegate.CreateDelegate(delegateType, target, handler);
            remove.Invoke(source, new object[] { del });
        }
    }
}
