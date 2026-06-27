using System.Collections.Generic;
using Mirage;
using Mirage.Serialization;
using SimpleWSO.Core;
using SimpleWSO.Gunner;
using UnityEngine;

namespace SimpleWSO.Net
{
    /// <summary>
    /// Custom Mirage transport for gunner input.
    ///
    /// Design (deliberately simple/robust for small co-op sessions):
    ///   gunner client --Send--> server --SendToAll--> every client
    ///   each client applies ONLY if it owns the referenced aircraft (Unit.LocalSim == true).
    ///
    /// We register serializers by hand via Writer&lt;T&gt;.Write / Reader&lt;T&gt;.Read because the
    /// mod is built without the Mirage weaver. Everything is wrapped in guards so a networking
    /// failure degrades to "solo only" rather than crashing the mod.
    /// Two-client test checklist: README.
    /// </summary>
    public static class SimpleWsoNet
    {
        private const int MaxSharedTargets = 32;
        public static bool Initialized { get; private set; }

        private static bool _serializersRegistered;
        private static object _registeredClientMessageHandler;
        private static object _registeredServerMessageHandler;

        private static NetworkClient _client;
        private static NetworkServer _server;

        // Owner-side desired state, applied every FixedUpdate by OwnerTick().
        // key = (netId << 8) | station
        private static readonly Dictionary<long, Vector3> _ownerAim = new Dictionary<long, Vector3>();
        private static readonly Dictionary<long, uint[]> _ownerTargetNetIds = new Dictionary<long, uint[]>();
        private static readonly HashSet<long> _ownerFiring = new HashSet<long>();
        private static readonly HashSet<long> _remoteGunnerStations = new HashSet<long>();
        private static readonly Dictionary<long, float> _lastAimSent = new Dictionary<long, float>();
        private static readonly Dictionary<long, Vector3> _lastAimDirSent = new Dictionary<long, Vector3>();
        private static readonly Dictionary<uint, Aircraft> _ownerSubscribedAircraft = new Dictionary<uint, Aircraft>();

        // Last target persistentID we replicated per station. The turret-target Cmd is
        // rate-limited by the game, so we only re-send when the gunner's target changes.
        private static readonly Dictionary<long, uint> _ownerAppliedTargetId = new Dictionary<long, uint>();

        private static long Key(uint netId, byte station) => ((long)netId << 8) | station;

        public static bool ClientReady => _client != null && _client.Active;

        public static void TryInit()
        {
            if (Initialized) return;

            // Get client/server handles via any spawned networked aircraft.
            var ac = StationDiscovery.FindAircraft();
            NetworkIdentity anyIdentity = null;
            foreach (var a in ac)
            {
                if (a != null && a.Identity != null) { anyIdentity = a.Identity; break; }
            }
            if (anyIdentity == null) return; // not in a networked session yet

            _client = anyIdentity.Client;
            _server = anyIdentity.Server; // null on non-host clients

            if (_client == null) return;

            try
            {
                RegisterSerializers();
                RegisterHandlers();
                Initialized = true;
                Plugin.Log.LogInfo($"[Net] Initialized. server={( _server != null)} client={_client.Active}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"[Net] Init failed, multicrew will be solo-only: {e}");
            }
        }

        public static bool IsRemoteGunnerStation(uint netId, byte station)
        {
            var key = Key(netId, station);
            return _remoteGunnerStations.Contains(key) ||
                   _ownerAim.ContainsKey(key) ||
                   _ownerFiring.Contains(key);
        }

        /// <summary>Latest networked aim direction for a remote-gunner station on this owner client.</summary>
        public static bool TryGetRemoteGunnerAim(uint aircraftNetId, byte station, out Vector3 aimDir)
        {
            return _ownerAim.TryGetValue(Key(aircraftNetId, station), out aimDir);
        }

        /// <summary>
        /// Owner-side desired target for a remote-gunner station. Zero means free-aim/no target.
        /// </summary>
        public static uint GetRemoteGunnerTargetId(uint aircraftNetId, byte station)
        {
            if (_ownerTargetNetIds.TryGetValue(Key(aircraftNetId, station), out var ids) &&
                ids != null &&
                ids.Length > 0)
            {
                return ids[0];
            }

            return 0u;
        }

        public static void Reset()
        {
            Initialized = false;
            _client = null;
            _server = null;
            UnsubscribeOwnerAircraft();
            _ownerAim.Clear();
            _ownerTargetNetIds.Clear();
            _ownerFiring.Clear();
            _remoteGunnerStations.Clear();
            _lastAimSent.Clear();
            _lastAimDirSent.Clear();
            _ownerAppliedTargetId.Clear();
        }

        // ---- serializer registration (manual, no weaver) ----
        private static void RegisterSerializers()
        {
            if (_serializersRegistered) return;
            _serializersRegistered = true;

            Writer<GunnerJoinMsg>.Write = (w, m) => { w.WriteUInt32(m.AircraftNetId); w.WriteByte(m.Station); };
            Reader<GunnerJoinMsg>.Read = r => new GunnerJoinMsg { AircraftNetId = r.ReadUInt32(), Station = r.ReadByte() };

            Writer<GunnerLeaveMsg>.Write = (w, m) => { w.WriteUInt32(m.AircraftNetId); w.WriteByte(m.Station); };
            Reader<GunnerLeaveMsg>.Read = r => new GunnerLeaveMsg { AircraftNetId = r.ReadUInt32(), Station = r.ReadByte() };

            Writer<GunnerFireMsg>.Write = (w, m) =>
            {
                w.WriteUInt32(m.AircraftNetId);
                w.WriteByte(m.Station);
                w.WriteBoolean(m.Firing);
                w.WriteSingle(m.X);
                w.WriteSingle(m.Y);
                w.WriteSingle(m.Z);
                int count = m.TargetNetIds != null ? Mathf.Min(m.TargetNetIds.Length, MaxSharedTargets) : 0;
                w.WriteByte((byte)count);
                for (int i = 0; i < count; i++)
                    w.WriteUInt32(m.TargetNetIds[i]);
            };
            Reader<GunnerFireMsg>.Read = r =>
            {
                var msg = new GunnerFireMsg
                {
                    AircraftNetId = r.ReadUInt32(),
                    Station = r.ReadByte(),
                    Firing = r.ReadBoolean(),
                    X = r.ReadSingle(),
                    Y = r.ReadSingle(),
                    Z = r.ReadSingle(),
                };
                int count = Mathf.Min(r.ReadByte(), MaxSharedTargets);
                msg.TargetNetIds = new uint[count];
                for (int i = 0; i < count; i++)
                    msg.TargetNetIds[i] = r.ReadUInt32();
                return msg;
            };

            Writer<TargetShareMsg>.Write = (w, m) =>
            {
                w.WriteUInt32(m.AircraftNetId);
                w.WriteByte(m.Direction);
                w.WriteBoolean(m.Replace);
                int count = m.TargetNetIds != null ? Mathf.Min(m.TargetNetIds.Length, MaxSharedTargets) : 0;
                w.WriteByte((byte)count);
                for (int i = 0; i < count; i++)
                    w.WriteUInt32(m.TargetNetIds[i]);
            };
            Reader<TargetShareMsg>.Read = r =>
            {
                var msg = new TargetShareMsg
                {
                    AircraftNetId = r.ReadUInt32(),
                    Direction = r.ReadByte(),
                    Replace = r.ReadBoolean()
                };
                int count = Mathf.Min(r.ReadByte(), MaxSharedTargets);
                msg.TargetNetIds = new uint[count];
                for (int i = 0; i < count; i++)
                    msg.TargetNetIds[i] = r.ReadUInt32();
                return msg;
            };
        }

        private static void RegisterHandlers()
        {
            if (_client?.MessageHandler != null && !ReferenceEquals(_registeredClientMessageHandler, _client.MessageHandler))
            {
                var handler = _client.MessageHandler;
                _registeredClientMessageHandler = handler;
                handler.RegisterHandler<GunnerJoinMsg>((p, m) => OnJoin(m), true);
                handler.RegisterHandler<GunnerLeaveMsg>((p, m) => OnLeave(m), true);
                handler.RegisterHandler<GunnerFireMsg>((p, m) => OnFire(m), true);
                handler.RegisterHandler<TargetShareMsg>((p, m) => OnTargetShare(m), true);
            }

            if (_server?.MessageHandler != null && !ReferenceEquals(_registeredServerMessageHandler, _server.MessageHandler))
            {
                var handler = _server.MessageHandler;
                _registeredServerMessageHandler = handler;
                handler.RegisterHandler<GunnerJoinMsg>((p, m) => Relay(m), true);
                handler.RegisterHandler<GunnerLeaveMsg>((p, m) => Relay(m), true);
                handler.RegisterHandler<GunnerFireMsg>((p, m) => Relay(m), true);
                handler.RegisterHandler<TargetShareMsg>((p, m) => Relay(m), true);
            }
        }

        private static void Relay<T>(T msg)
        {
            if (_server == null) return;
            // authenticatedOnly: false, excludeLocalPlayer: false (host owner must receive via loopback)
            _server.SendToAll(msg, false, false, Channel.Reliable);
        }

        // ---- gunner-side senders (client -> server) ----
        public static void SendJoin(uint netId, byte station)
            => Send(new GunnerJoinMsg { AircraftNetId = netId, Station = station }, Channel.Reliable);

        public static void SendLeave(uint netId, byte station)
            => Send(new GunnerLeaveMsg { AircraftNetId = netId, Station = station }, Channel.Reliable);

        public static void SendAim(uint netId, byte station, Vector3 dir, bool firing, IList<Unit> targets)
        {
            long key = Key(netId, station);
            float now = Time.unscaledTime;
            if (_lastAimSent.TryGetValue(key, out float last) && now - last < 1f / 30f)
                return;

            _lastAimSent[key] = now;
            SendControlFrame(netId, station, dir, firing, targets);
        }

        public static void SendFire(uint netId, byte station, bool firing, IList<Unit> targets)
        {
            long key = Key(netId, station);
            Vector3 dir = _lastAimDirSent.TryGetValue(key, out var cached) ? cached : Vector3.forward;
            SendControlFrame(netId, station, dir, firing, targets);
        }

        private static void SendControlFrame(uint netId, byte station, Vector3 dir, bool firing, IList<Unit> targets)
        {
            if (dir.sqrMagnitude < 1e-6f)
                dir = Vector3.forward;
            dir.Normalize();

            _lastAimDirSent[Key(netId, station)] = dir;

            // Use the reliable fire/control message for aim as well. Fire frames are known
            // to reach the aircraft owner in the pilot/gunner station handoff matrix.
            Send(new GunnerFireMsg
            {
                AircraftNetId = netId,
                Station = station,
                Firing = firing,
                X = dir.x,
                Y = dir.y,
                Z = dir.z,
                TargetNetIds = BuildTargetIds(targets),
            }, Channel.Reliable);
        }

        public static void ShareTargets(Aircraft aircraft, byte direction, bool replace, IList<Unit> targets)
        {
            if (aircraft == null) return;
            uint[] ids = BuildTargetIds(targets);
            Send(new TargetShareMsg
            {
                AircraftNetId = aircraft.NetId,
                Direction = direction,
                Replace = replace,
                TargetNetIds = ids
            }, Channel.Reliable);
            Plugin.Log.LogInfo($"[Targets] Shared {ids.Length} target(s), direction={direction}, replace={replace}, aircraft={aircraft.NetId}.");
        }

        private static void Send<T>(T msg, Channel channel)
        {
            if (!ClientReady) return;
            try { _client.Send(msg, channel); }
            catch (System.Exception e) { Plugin.Log.LogError($"[Net] Send<{typeof(T).Name}> failed: {e.Message}"); }
        }

        // ---- owner-side handlers ----
        private static void OnJoin(GunnerJoinMsg m)
        {
            var ts = ResolveOwned(m.AircraftNetId, m.Station);
            if (ts == null) return;
            SubscribeOwnerAircraft(ts.Aircraft);
            _remoteGunnerStations.Add(Key(m.AircraftNetId, m.Station));
            TurretController.EngageManual(ts);
            TurretController.ReleaseTurretTargetLock(ts);
            Plugin.Log.LogInfo($"[Net] Owner engaged station {m.Station} for remote gunner.");
        }

        private static void OnLeave(GunnerLeaveMsg m)
        {
            var ts = ResolveOwned(m.AircraftNetId, m.Station, allowDisabled: true);
            CleanupOwnerStation(m.AircraftNetId, m.Station, ts, restoreStationActive: true);
            if (!HasOwnerStateForAircraft(m.AircraftNetId))
                UnsubscribeOwnerAircraft(m.AircraftNetId);
        }

        private static void OnFire(GunnerFireMsg m)
        {
            if (ResolveOwned(m.AircraftNetId, m.Station) == null) return;
            var k = Key(m.AircraftNetId, m.Station);
            var aimDir = new Vector3(m.X, m.Y, m.Z);
            if (aimDir.sqrMagnitude > 1e-6f)
                _ownerAim[k] = aimDir;
            if (m.TargetNetIds != null)
                _ownerTargetNetIds[k] = m.TargetNetIds;
            if (m.Firing) _ownerFiring.Add(k); else _ownerFiring.Remove(k);
        }

        private static void OnTargetShare(TargetShareMsg m)
        {
            if (m.Direction == TargetShareMsg.GunnerToPilot)
            {
                var ac = ResolveOwnedAircraft(m.AircraftNetId);
                if (ac == null) return;
                ApplyTargetsToPilot(ac, ResolveTargets(m.TargetNetIds), m.Replace);
                return;
            }

            if (m.Direction == TargetShareMsg.PilotToGunner)
            {
                if (!GunnerState.Active || GunnerState.TargetAircraft == null) return;
                if (GunnerState.TargetAircraft.NetId != m.AircraftNetId) return;
                ApplyTargetsToGunner(ResolveTargets(m.TargetNetIds), m.Replace);
            }
        }

        /// <summary>Called every FixedUpdate on every client; only does work for owned aircraft.</summary>
        public static void OwnerTick()
        {
            if (_ownerAim.Count == 0 && _ownerFiring.Count == 0) return;

            var stale = new List<long>();
            var keys = new HashSet<long>(_ownerAim.Keys);
            foreach (var k in _ownerFiring)
                keys.Add(k);

            foreach (var k in keys)
            {
                if (!ApplyOwnerStation(k, _ownerAim.TryGetValue(k, out var dir) ? dir : Vector3.forward, fire: _ownerFiring.Contains(k)))
                    stale.Add(k);
            }

            foreach (var k in stale)
            {
                uint netId = (uint)(k >> 8);
                byte station = (byte)(k & 0xFF);
                var ts = ResolveOwned(netId, station, allowDisabled: true);
                CleanupOwnerStation(netId, station, ts, restoreStationActive: false);
                if (!HasOwnerStateForAircraft(netId))
                    UnsubscribeOwnerAircraft(netId);
            }
        }

        private static void SubscribeOwnerAircraft(Aircraft aircraft)
        {
            if (aircraft == null || _ownerSubscribedAircraft.ContainsKey(aircraft.NetId))
                return;

            aircraft.onDisableUnit += OnOwnedAircraftDisabled;
            _ownerSubscribedAircraft[aircraft.NetId] = aircraft;
        }

        private static void UnsubscribeOwnerAircraft(uint netId)
        {
            if (!_ownerSubscribedAircraft.TryGetValue(netId, out var aircraft))
                return;

            if (aircraft != null)
                aircraft.onDisableUnit -= OnOwnedAircraftDisabled;
            _ownerSubscribedAircraft.Remove(netId);
        }

        private static void UnsubscribeOwnerAircraft()
        {
            foreach (var pair in new List<KeyValuePair<uint, Aircraft>>(_ownerSubscribedAircraft))
            {
                if (pair.Value != null)
                    pair.Value.onDisableUnit -= OnOwnedAircraftDisabled;
            }
            _ownerSubscribedAircraft.Clear();
        }

        // Vanilla invokes onDisableUnit from inside Unit.UnitDisabled (synchronously, during crash /
        // ReturnToInventory teardown). If our cleanup throws here it unwinds vanilla's disable
        // sequence before WaitRemoveAircraft()/Destroy(), leaving the airframe alive forever. Never
        // let this handler throw back into vanilla.
        private static void OnOwnedAircraftDisabled(Unit unit)
        {
            try
            {
                if (unit is Aircraft aircraft)
                    CleanupOwnerAircraft(aircraft, "disabled");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Net] onDisableUnit cleanup error (ignored): {e.GetType().Name}: {e.Message}");
            }
        }

        private static void CleanupOwnerAircraft(Aircraft aircraft, string reason)
        {
            if (aircraft == null) return;

            uint netId = aircraft.NetId;
            var stationLookup = new Dictionary<byte, TurretStation>();
            foreach (var station in StationDiscovery.GetGunnerStations(aircraft))
            {
                if (station != null)
                    stationLookup[station.Number] = station;
            }

            var keys = new HashSet<long>();
            CollectOwnerKeysForAircraft(_remoteGunnerStations, netId, keys);
            CollectOwnerKeysForAircraft(_ownerAim.Keys, netId, keys);
            CollectOwnerKeysForAircraft(_ownerTargetNetIds.Keys, netId, keys);
            CollectOwnerKeysForAircraft(_ownerFiring, netId, keys);
            CollectOwnerKeysForAircraft(_ownerAppliedTargetId.Keys, netId, keys);

            foreach (long key in keys)
            {
                byte stationNumber = (byte)(key & 0xFF);
                stationLookup.TryGetValue(stationNumber, out var station);
                CleanupOwnerStation(netId, stationNumber, station, restoreStationActive: false);
            }

            UnsubscribeOwnerAircraft(netId);

            if (keys.Count > 0)
                Plugin.Log.LogInfo($"[Net] Cleared {keys.Count} remote gunner station(s) for aircraft {netId} ({reason}).");
        }

        private static void CollectOwnerKeysForAircraft(IEnumerable<long> source, uint netId, HashSet<long> keys)
        {
            foreach (long key in source)
            {
                if ((uint)(key >> 8) == netId)
                    keys.Add(key);
            }
        }

        private static bool HasOwnerStateForAircraft(uint netId)
        {
            foreach (long key in _remoteGunnerStations)
                if ((uint)(key >> 8) == netId) return true;
            foreach (long key in _ownerAim.Keys)
                if ((uint)(key >> 8) == netId) return true;
            foreach (long key in _ownerTargetNetIds.Keys)
                if ((uint)(key >> 8) == netId) return true;
            foreach (long key in _ownerFiring)
                if ((uint)(key >> 8) == netId) return true;
            foreach (long key in _ownerAppliedTargetId.Keys)
                if ((uint)(key >> 8) == netId) return true;

            return false;
        }

        private static void CleanupOwnerStation(uint netId, byte station, TurretStation ts, bool restoreStationActive)
        {
            long key = Key(netId, station);
            _ownerAim.Remove(key);
            _ownerTargetNetIds.Remove(key);
            _ownerFiring.Remove(key);
            _remoteGunnerStations.Remove(key);
            bool hadTarget = _ownerAppliedTargetId.TryGetValue(key, out var appliedId) && appliedId != 0u;
            _ownerAppliedTargetId.Remove(key);

            if (ts == null)
                return;

            // Drop the replicated lock so the turret doesn't keep engaging the gunner's
            // last target after the gunner leaves; vanilla AI then resumes normally.
            if (hadTarget && ts.Aircraft != null && !ts.Aircraft.disabled)
                TurretController.ApplyStationTarget(ts, null);

            TurretController.ReleaseManual(ts);
            if (restoreStationActive &&
                ts.Aircraft != null &&
                !ts.Aircraft.disabled &&
                ts.Aircraft?.weaponManager?.currentWeaponStation == ts.Station)
            {
                ts.Station.SetStationActive(ts.Aircraft, true);
            }
        }

        private static bool ApplyOwnerStation(long key, Vector3 aimDir, bool fire)
        {
            uint netId = (uint)(key >> 8);
            byte station = (byte)(key & 0xFF);
            var ts = ResolveOwned(netId, station);
            if (ts == null) return false;

            List<Unit> targets = ResolveOwnerTargets(key);
            Unit primary = targets.Count > 0 ? targets[0] : null;
            if (ts.HasTurret)
            {
                // Replicate the gunner's target choice through the game's own networked
                // turret-target path, but only when it changes (the Cmd is rate-limited).
                // Every client then tracks the target locally with the vanilla aim solver,
                // so locked aiming is smooth and frame-rate independent (no vector streaming).
                uint desiredId = primary != null ? primary.persistentID.Id : 0u;
                uint appliedId = _ownerAppliedTargetId.TryGetValue(key, out var prev) ? prev : 0u;
                if (desiredId != appliedId ||
                    !TurretController.StationTurretTargetsMatch(ts, desiredId))
                {
                    _ownerAppliedTargetId[key] = desiredId;
                    TurretController.ApplyStationTarget(ts, primary);
                }

                // Free-aim is the only case that needs a streamed look direction. While a
                // target is locked, vanilla tracking owns the aim on every client.
                if (primary == null)
                    TurretController.Aim(ts, aimDir, null, driveTurretAim: true);
            }
            if (fire)
                GunnerWeaponFire.FireStation(ts, primary, targets);
            return true;
        }

        /// <summary>Resolve an aircraft by NetId, but ONLY if this client owns/locally-sims it.</summary>
        private static TurretStation ResolveOwned(uint netId, byte station, bool allowDisabled = false)
        {
            var ac = ResolveOwnedAircraft(netId, allowDisabled);
            if (ac == null) return null;

            foreach (var ts in StationDiscovery.GetGunnerStations(ac))
                if (ts.Number == station) return ts;
            return null;
        }

        private static Aircraft ResolveOwnedAircraft(uint netId, bool allowDisabled = false)
        {
            if (_client == null || _client.World == null) return null;
            if (!_client.World.TryGetIdentity(netId, out var identity) || identity == null) return null;

            var ac = identity.GetComponent<Aircraft>();
            return ac != null && (allowDisabled || !ac.disabled) && ac.LocalSim ? ac : null;
        }

        private static List<Unit> ResolveOwnerTargets(long key)
        {
            if (!_ownerTargetNetIds.TryGetValue(key, out var netIds) || netIds == null || netIds.Length == 0)
                return new List<Unit>();
            return ResolveTargets(netIds);
        }

        private static Unit ResolveOwnerTarget(long key)
        {
            var targets = ResolveOwnerTargets(key);
            return targets.Count > 0 ? targets[0] : null;
        }

        private static Unit ResolveTarget(uint netId)
        {
            if (netId == 0 || _client == null || _client.World == null) return null;
            if (!_client.World.TryGetIdentity(netId, out var identity) || identity == null) return null;

            var unit = identity.GetComponent<Unit>();
            return unit != null && !unit.disabled ? unit : null;
        }

        private static uint[] BuildTargetIds(IList<Unit> targets)
            => TargetListUtil.BuildPersistentIds(targets, MaxSharedTargets);

        private static List<Unit> ResolveTargets(uint[] targetNetIds)
            => TargetListUtil.ResolvePersistentIds(targetNetIds);

        private static void ApplyTargetsToPilot(Aircraft aircraft, List<Unit> targets, bool replace)
        {
            if (aircraft == null || aircraft.weaponManager == null) return;

            var hud = CombatHUD.i;
            bool hudIsGunnerView = GunnerState.Active && GunnerState.TargetAircraft == aircraft;
            bool useHud = !hudIsGunnerView && hud != null && hud.aircraft == aircraft;
            if (replace)
            {
                if (useHud) hud.DeselectAll(false);
                else aircraft.weaponManager.ClearTargetList();
            }

            var current = aircraft.weaponManager.GetTargetList();
            foreach (var target in targets)
            {
                if (target == null || current.Contains(target)) continue;
                if (useHud) hud.SelectUnit(target);
                else aircraft.weaponManager.AddTargetList(target);
            }

            Plugin.Log.LogInfo($"[Targets] Applied {targets.Count} shared target(s) to pilot list. replace={replace}");
        }

        private static void ApplyTargetsToGunner(List<Unit> targets, bool replace)
        {
            var hud = CombatHUD.i;
            bool useHud = hud != null && GunnerState.TargetAircraft != null && hud.aircraft == GunnerState.TargetAircraft;
            if (replace)
            {
                if (useHud) hud.DeselectAll(false);
                else GunnerState.TargetList.Clear();
            }

            foreach (var target in targets)
            {
                if (target == null || GunnerState.TargetList.Contains(target)) continue;
                if (useHud) hud.SelectUnit(target);
                else GunnerState.TargetList.Add(target);
            }

            Plugin.Log.LogInfo($"[Targets] Applied {targets.Count} shared target(s) to gunner list. replace={replace}");
        }
    }
}
