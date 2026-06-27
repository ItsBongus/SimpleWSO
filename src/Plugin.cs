using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using SimpleWSO.Core;
using SimpleWSO.Gunner;
using SimpleWSO.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimpleWSO
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.bongus.simplewso";
        public const string Name = "SimpleWSO";
        public const string Version = "1.0.0";

        public static ManualLogSource Log;
        public static Plugin Instance;

        public static void LogVerbose(string message)
        {
            if (SimpleWsoConfig.VerboseLogging != null && SimpleWsoConfig.VerboseLogging.Value)
                Log?.LogInfo(message);
        }

        private Harmony _harmony;
        private GunnerController _gunner;
        private PilotGunnerTurretReticleOverlay _pilotGunnerReticle;
        private float _netRetry;
        private bool _heartbeat;
        private bool _loggedUpdateError;
        private bool _cleaningUp;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            SimpleWsoConfig.Bind(Config);

            _gunner = new GunnerController();
            _pilotGunnerReticle = new PilotGunnerTurretReticleOverlay();

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            SceneManager.sceneUnloaded += OnSceneUnloaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;

            Log.LogInfo($"{Name} {Version} loaded.");
        }

        private void Update()
        {
            try
            {
                if (!_heartbeat)
                {
                    _heartbeat = true;
                    LogVerbose($"[Heartbeat] Update loop running. Rewired ready={RewiredInput.Ready}");
                }

                // Lazily hook the network layer once a session exists; retry periodically.
                if (!SimpleWsoNet.Initialized)
                {
                    _netRetry -= Time.unscaledDeltaTime;
                    if (_netRetry <= 0f)
                    {
                        _netRetry = 2f;
                        SimpleWsoNet.TryInit();
                    }
                }

                if (RewiredInput.GetKeyDown(SimpleWsoConfig.ToggleGunnerKey.Value))
                    ToggleGunnerQuick();

                if (RewiredInput.GetKeyDown(SimpleWsoConfig.ShareTargetsKey.Value))
                    ShareTargetsQuick();

                _gunner.Update();
                _pilotGunnerReticle.Update();
            }
            catch (System.Exception e)
            {
                if (!_loggedUpdateError)
                {
                    _loggedUpdateError = true;
                    Log.LogError($"[Update] Exception (logged once): {e}");
                }
            }
        }

        private void FixedUpdate()
        {
            // Owner applies any remote gunner input it has received.
            if (SimpleWsoNet.Initialized)
                SimpleWsoNet.OwnerTick();
        }

        private void OnDestroy()
        {
            CleanupAll("plugin destroyed");
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            _harmony?.UnpatchSelf();
            if (Instance == this) Instance = null;
        }

        private void OnDisable()
        {
            CleanupAll("plugin disabled");
        }

        private void OnApplicationQuit()
        {
            CleanupAll("application quit");
        }

        private void OnSceneUnloaded(Scene scene)
        {
            CleanupAll($"scene unloaded: {scene.name}");
        }

        private void OnActiveSceneChanged(Scene from, Scene to)
        {
            CleanupAll($"scene changed: {from.name} -> {to.name}");
        }

        private void CleanupAll(string reason)
        {
            if (_cleaningUp) return;
            _cleaningUp = true;
            try
            {
                _gunner?.Leave(reason);
                _pilotGunnerReticle?.Clear();
                TurretController.CleanupAll();
                SimpleWsoNet.Reset();
            }
            catch (System.Exception e)
            {
                Log?.LogWarning($"[Cleanup] {reason} failed: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                _cleaningUp = false;
            }
        }

        /// <summary>
        /// Quick helper: if the camera is already following a valid friendly aircraft,
        /// enter that aircraft as gunner. Falls back to the local aircraft for solo testing.
        /// </summary>
        private void ToggleGunnerQuick()
        {
            var csm = CameraStateManager.i;
            LogVerbose($"[GunnerInput] {SimpleWsoConfig.ToggleGunnerKey.Value} pressed active={GunnerState.Active} piloting={StationDiscovery.IsLocalPlayerPiloting()} follow={(csm?.followingUnit != null ? $"{csm.followingUnit.unitName}/net={csm.followingUnit.NetId}" : "null")} state={(csm?.currentState != null ? csm.currentState.GetType().Name : "null")} mapMax={DynamicMap.mapMaximized} cursor={CursorManager.GetFlags()}");

            if (StationDiscovery.IsLocalPlayerPiloting())
            {
                Log.LogInfo("[Gunner] Ignoring quick-toggle while piloting local aircraft.");
                return;
            }

            if (GunnerState.Active)
            {
                _gunner.Leave();
                return;
            }

            var followed = StationDiscovery.FindFollowedAircraft();
            if (StationDiscovery.CanJoinAsGunner(followed))
            {
                Log.LogInfo($"[Gunner] Joining followed aircraft: {StationDiscovery.GetAircraftLabel(followed)}.");
                _gunner.TakeAircraft(followed);
                return;
            }

            var ac = StationDiscovery.FindLocalAircraft();
            if (ac == null)
            {
                Log.LogWarning("[Gunner] No valid followed aircraft or local aircraft found.");
                return;
            }

            if (!StationDiscovery.CanJoinAsGunner(ac))
            {
                Log.LogWarning($"[Gunner] Local aircraft is not valid for gunner mode: {StationDiscovery.GetAircraftLabel(ac)}.");
                return;
            }

            Log.LogInfo($"[Gunner] No valid followed aircraft; falling back to local aircraft: {StationDiscovery.GetAircraftLabel(ac)}.");
            _gunner.TakeAircraft(ac);
        }

        private void ShareTargetsQuick()
        {
            bool replace = SimpleWsoConfig.ReplaceSharedTargets.Value;

            if (GunnerState.Active && GunnerState.TargetAircraft != null)
            {
                SimpleWsoNet.ShareTargets(GunnerState.TargetAircraft, TargetShareMsg.GunnerToPilot, replace, GunnerState.TargetList);
                return;
            }

            if (!GameManager.GetLocalAircraft(out Aircraft aircraft) || aircraft == null || aircraft.weaponManager == null)
            {
                Log.LogInfo("[Targets] No local pilot aircraft found for target sharing.");
                return;
            }

            SimpleWsoNet.ShareTargets(aircraft, TargetShareMsg.PilotToGunner, replace, aircraft.weaponManager.GetTargetList());
        }
    }
}
