using BepInEx.Configuration;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace SimpleWSO.Core
{
    /// <summary>
    /// All tunables for the mod. Bound from BepInEx config so users (and ConfigManager)
    /// can rebind keys without recompiling.
    /// </summary>
    public static class SimpleWsoConfig
    {
        public static ConfigEntry<KeyCode> ToggleGunnerKey;
        public static ConfigEntry<KeyCode> ShareTargetsKey;
        public static ConfigEntry<KeyCode> CycleCameraPositionKey;

        public static ConfigEntry<bool> ReplaceSharedTargets;
        public static ConfigEntry<bool> VerboseLogging;
        private const int MaxCameraPositions = 4;
        /// <summary>Positions 2..N pre-bound so they appear in BepInEx Configuration Manager (F1).</summary>
        private const int ConfigManagerCameraSlots = 4;
        private const string CameraOffsetsSection = "CameraOffsets";

        private static ConfigFile _configFile;
        private static readonly Dictionary<string, ConfigEntry<string>> CameraOffsetEntries =
            new Dictionary<string, ConfigEntry<string>>();
        /// <summary>
        /// Per-airframe default gunner camera position 1 (aircraft-local meters from origin).
        /// </summary>
        private static readonly Dictionary<string, Vector3> BaselineCameraOffsets =
            new Dictionary<string, Vector3>
            {
                { "CI-22_Cricket", new Vector3(0f, 0.15f, -0.9f) },
                { "T_A-30_Compass", new Vector3(0f, 0.25f, -1f) },
                { "UH-90_Ibis", new Vector3(0.93f, 0f, 0f) },
                { "SAH-46_Chicane", new Vector3(0f, -0.4f, 1.5f) },
                { "VL-49_Tarantula", new Vector3(1.34f, 0f, 0f) },
                { "EW-25_Medusa", new Vector3(0.85f, 0f, 0f) },
                { "SFB-81_Darkreach", new Vector3(1f, 0f, 0f) },
                { "Alkyon_AB-4", new Vector3(0.8f, 0f, 0f) },
            };

        /// <summary>
        /// Default gunner camera positions 2+ (aircraft-local meters from origin).
        /// </summary>
        private static readonly Dictionary<string, string[]> ExtraCameraPositionDefaults =
            new Dictionary<string, string[]>
            {
                { "CI-22_Cricket", new[] { "0,-1.5,1.9" } },
                { "T_A-30_Compass", new[] { "0,-1.3,2.9" } },
                { "UH-90_Ibis", new[] { "-0.4,0,-1.2", "1.4,0,-1.2" } },
                { "SAH-46_Chicane", new[] { "0,-1.9,3.2" } },
                { "VL-49_Tarantula", new[] { "-2,-1.9,-12" } },
            };

        public static void Bind(ConfigFile cfg)
        {
            _configFile = cfg;

            ToggleGunnerKey = cfg.Bind("Keys", "ToggleGunnerKey", KeyCode.H,
                "Spectate/follow an aircraft, then press to possess a gunner seat. Press again to leave.");
            ShareTargetsKey = cfg.Bind("Keys", "ShareTargetsKey", KeyCode.U,
                "Share current targets with the other seat. Gunner sends to pilot; pilot sends to gunner.");
            CycleCameraPositionKey = cfg.Bind("Keys", "CycleCameraPositionKey", KeyCode.K,
                "While in a gunner seat, cycle between configured camera positions for the current aircraft.");

            ReplaceSharedTargets = cfg.Bind("Behaviour", "ReplaceSharedTargets", false,
                "Replace the other seat's targets when sharing (ShareTargetsKey). When false, shared targets are merged.");
            VerboseLogging = cfg.Bind("Behaviour", "VerboseLogging", false,
                "Extra debug logging for view state, fire diagnostics, and gunner input.");

            foreach (string aircraftKey in BaselineCameraOffsets.Keys)
            {
                GetCameraOffsetEntry(aircraftKey);
                EnsureDefaultExtraCameraPositionEntries(aircraftKey);
            }

            RegisterExistingCameraOffsetEntries();
            EnsureConfigManagerCameraSlotsForAllKnownAircraft();
        }

        /// <summary>
        /// Ensures position 1 and optional .Position2.. slots exist for Configuration Manager editing.
        /// </summary>
        public static void EnsureCameraOffsetsFor(Aircraft aircraft)
        {
            if (aircraft == null) return;
            string key = CameraOffsetKey(aircraft);
            GetCameraOffsetEntry(key);
            EnsureConfigManagerCameraSlots(key);
        }

        public static string CameraOffsetKey(Aircraft aircraft)
        {
            string raw = null;
            if (aircraft != null && aircraft.definition != null)
                raw = aircraft.definition.unitName;
            if (string.IsNullOrEmpty(raw) && aircraft != null)
                raw = aircraft.unitName;
            if (string.IsNullOrEmpty(raw) && aircraft != null)
                raw = aircraft.name;
            if (string.IsNullOrEmpty(raw))
                raw = "UnknownAircraft";

            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
                raw = raw.Replace(invalid, '_');
            return raw.Replace(' ', '_');
        }

        public static Vector3 GetCameraOffset(Aircraft aircraft)
            => GetCameraOffset(aircraft, 0);

        public static Vector3 GetCameraOffset(Aircraft aircraft, int positionIndex)
        {
            string key = CameraOffsetKey(aircraft);
            if (positionIndex <= 0)
                return GetPrimaryCameraOffset(key);

            int remaining = positionIndex;
            for (int positionNumber = 2; positionNumber <= MaxCameraPositions; positionNumber++)
            {
                if (!TryReadCameraPosition(key, positionNumber, out Vector3 offset))
                    continue;

                remaining--;
                if (remaining == 0)
                    return offset;
            }

            return GetPrimaryCameraOffset(key);
        }

        /// <summary>
        /// Position 1 plus every .PositionN entry with a valid x,y,z value (empty slots are skipped).
        /// </summary>
        public static int GetCameraPositionCount(Aircraft aircraft)
        {
            string key = CameraOffsetKey(aircraft);
            GetCameraOffsetEntry(key);

            int count = 1;
            for (int positionNumber = 2; positionNumber <= MaxCameraPositions; positionNumber++)
            {
                if (TryReadCameraPosition(key, positionNumber, out _))
                    count++;
            }

            return count;
        }

        private static Vector3 GetPrimaryCameraOffset(string key)
            => ParseVector(GetCameraOffsetEntry(key).Value);

        private static Vector3 GetBaselineCameraOffset(string key)
        {
            return BaselineCameraOffsets.TryGetValue(key, out var tuned)
                ? tuned
                : Vector3.zero;
        }

        private static ConfigEntry<string> GetCameraOffsetEntry(string key)
        {
            if (!CameraOffsetEntries.TryGetValue(key, out var entry))
            {
                string defaultValue = FormatVector(GetBaselineCameraOffset(key));
                entry = _configFile.Bind(
                    CameraOffsetsSection,
                    key,
                    defaultValue,
                    CreateCameraPositionDescription(key, 1));
                CameraOffsetEntries[key] = entry;
            }
            return entry;
        }

        private static void EnsureConfigManagerCameraSlotsForAllKnownAircraft()
        {
            if (_configFile == null) return;

            var aircraftKeys = new HashSet<string>(BaselineCameraOffsets.Keys);
            foreach (var def in _configFile.Keys.Where(d => d.Section == CameraOffsetsSection))
            {
                if (TryParsePositionEntryKey(def.Key, out string aircraftKey, out _))
                    aircraftKeys.Add(aircraftKey);
                else
                    aircraftKeys.Add(def.Key);
            }

            foreach (string aircraftKey in aircraftKeys)
                EnsureConfigManagerCameraSlots(aircraftKey);
        }

        /// <summary>
        /// Registers empty .Position2.. slots so Configuration Manager can edit them in-game.
        /// Empty values are ignored until the player fills them in.
        /// </summary>
        private static void EnsureConfigManagerCameraSlots(string aircraftKey)
        {
            for (int positionNumber = 2; positionNumber <= ConfigManagerCameraSlots; positionNumber++)
            {
                string entryKey = ExtraCameraEntryKey(aircraftKey, positionNumber);
                if (!HasConfigEntry(entryKey))
                    BindCameraOffsetEntry(entryKey, aircraftKey, positionNumber);
            }
        }

        private static void EnsureDefaultExtraCameraPositionEntries(string aircraftKey)
        {
            if (!ExtraCameraPositionDefaults.TryGetValue(aircraftKey, out var defaults))
                return;

            for (int i = 0; i < defaults.Length; i++)
                BindCameraOffsetEntry(ExtraCameraEntryKey(aircraftKey, i + 2), aircraftKey, i + 2);
        }

        private static void RegisterExistingCameraOffsetEntries()
        {
            if (_configFile == null) return;

            foreach (var def in _configFile.Keys.Where(d => d.Section == CameraOffsetsSection))
                BindCameraOffsetEntryFromConfig(def.Key);
        }

        private static void BindCameraOffsetEntryFromConfig(string entryKey)
        {
            if (CameraOffsetEntries.ContainsKey(entryKey))
                return;

            if (TryParsePositionEntryKey(entryKey, out string aircraftKey, out int positionNumber))
                BindCameraOffsetEntry(entryKey, aircraftKey, positionNumber);
            else
                GetCameraOffsetEntry(entryKey);
        }

        private static bool TryReadCameraPosition(string aircraftKey, int positionNumber, out Vector3 offset)
        {
            offset = default;
            if (positionNumber == 1)
            {
                offset = GetPrimaryCameraOffset(aircraftKey);
                return true;
            }

            string entryKey = ExtraCameraEntryKey(aircraftKey, positionNumber);
            if (!HasConfigEntry(entryKey))
                return false;

            return TryParseVector(BindCameraOffsetEntry(entryKey, aircraftKey, positionNumber).Value, out offset);
        }

        private static bool HasConfigEntry(string entryKey)
        {
            if (CameraOffsetEntries.ContainsKey(entryKey))
                return true;

            return _configFile.Keys.Any(d => d.Section == CameraOffsetsSection && d.Key == entryKey);
        }

        private static string ExtraCameraEntryKey(string aircraftKey, int positionNumber)
            => $"{aircraftKey}.Position{positionNumber}";

        private static bool TryParsePositionEntryKey(string entryKey, out string aircraftKey, out int positionNumber)
        {
            aircraftKey = null;
            positionNumber = 0;

            const string suffix = ".Position";
            int dot = entryKey.LastIndexOf(suffix, System.StringComparison.Ordinal);
            if (dot < 0) return false;

            if (!int.TryParse(entryKey.Substring(dot + suffix.Length), out positionNumber) || positionNumber < 2)
                return false;

            aircraftKey = entryKey.Substring(0, dot);
            return !string.IsNullOrEmpty(aircraftKey);
        }

        private static ConfigEntry<string> BindCameraOffsetEntry(string entryKey, string aircraftKey, int positionNumber)
        {
            if (CameraOffsetEntries.TryGetValue(entryKey, out var entry))
                return entry;

            string defaultValue = GetExtraCameraPositionDefault(aircraftKey, positionNumber);
            entry = _configFile.Bind(
                CameraOffsetsSection,
                entryKey,
                defaultValue,
                CreateCameraPositionDescription(aircraftKey, positionNumber));
            CameraOffsetEntries[entryKey] = entry;
            return entry;
        }

        private static ConfigDescription CreateCameraPositionDescription(string aircraftKey, int positionNumber)
        {
            string description = positionNumber == 1
                ? "Aircraft-local meters from origin: X right, Y up, Z forward."
                : "Aircraft-local meters from origin. Leave empty to disable this view.";

            return new ConfigDescription(
                description,
                null,
                new ConfigurationManagerAttributes
                {
                    Category = FormatAircraftDisplayName(aircraftKey),
                    DispName = $"Position {positionNumber}",
                    Order = positionNumber,
                });
        }

        private static string FormatAircraftDisplayName(string aircraftKey)
            => aircraftKey.Replace('_', ' ');

        private static string GetExtraCameraPositionDefault(string key, int positionNumber)
        {
            int index = positionNumber - 2;
            return ExtraCameraPositionDefaults.TryGetValue(key, out var defaults) &&
                   index >= 0 &&
                   index < defaults.Length
                ? defaults[index]
                : "";
        }

        private static string FormatVector(Vector3 vector)
            => string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", vector.x, vector.y, vector.z);

        private static Vector3 ParseVector(string value)
        {
            return TryParseVector(value, out Vector3 parsed) ? parsed : Vector3.zero;
        }

        private static bool TryParseVector(string value, out Vector3 vector)
        {
            vector = Vector3.zero;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string[] parts = value.Split(',');
            if (parts.Length != 3) return false;

            vector = new Vector3(
                ParseFloat(parts[0]),
                ParseFloat(parts[1]),
                ParseFloat(parts[2]));
            return true;
        }

        private static float ParseFloat(string value)
        {
            return float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0f;
        }
    }
}
