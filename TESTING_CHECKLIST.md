# SimpleWSO testing checklist

Use this before tagging a release or after meaningful changes. Mark each item **PASS** / **FAIL** / **SKIP** and note the build (`Plugin.Version`), mission, and airframe.

**Log file:** `BepInEx/LogOutput.log`  
**Config:** `BepInEx/config/com.bongus.simplewso.cfg`  
**Defaults:** `H` possess/leave · `U` share targets · vanilla **Next/Previous Weapon** · vanilla **Fire**

Enable `VerboseLogging = true` when diagnosing failures.

---

## Prerequisites

| # | Check | Result | Notes |
| --- | --- | --- | --- |
| P1 | Release build deployed to `BepInEx/plugins/SimpleWSO/` | | |
| P2 | BepInEx log shows `SimpleWSO 1.0.0 loaded.` on game start | | |
| P3 | Rewired input works (vanilla fire / weapon cycle in normal cockpit) | | |
| P4 | Two-client MP session available (for network section) | | |

---

## Solo smoke (quick gate)

Run alone in a mission with at least one friendly turret aircraft available.

| # | Step | Expected | Result | Notes |
| --- | --- | --- | --- | --- |
| S1 | Spectate a friendly aircraft (orbit camera) | Camera follows unit | | |
| S2 | Press **G** | Gunner cockpit view; log `[Gunner] Took …` | | |
| S3 | Move mouse / look around | Turret or aim vector follows look direction | | |
| S4 | Cycle **Next Weapon** / **Previous Weapon** | Station changes; HUD weapon readout updates | | |
| S5 | HUD targeting mode (A2A / A2G / LOG) | Mode matches selected weapon after cycle | | |
| S6 | Hold **Fire** | Weapon fires; no instant double-burst per pull | | |
| S7 | Press **G** again | Return to prior spectator view; log `[Gunner] Left station` | | |
| S8 | Repeat S2 → S7 once | Same behaviour second time | | |

---

## Solo functional (UI, alarms, edge cases)

| # | Step | Expected | Result | Notes |
| --- | --- | --- | --- | --- |
| F1 | Enter gunner on followed aircraft | Spectator intel overlay (g-force, missile count, coords) **hidden** | | `UnitDebug` panel |
| F2 | In gunner view | Minimap, damage/status UI visible where vanilla shows them | | |
| F3 | Press **H** (or leave cockpit via orbit) while gunning | Gunner session ends; no stuck cockpit HUD overlay | | |
| F4 | After leaving gunner to spectator | Orbit/spectator HUD normal; flight HUD not stuck on screen | | |
| F5 | Take incoming missile while **piloting** own aircraft | Incoming-missile alarm plays | | |
| F6 | Evade / defeat missile (flare, outrun, kill missile) | Alarm **stops**; threat UI clears | | |
| F7 | Enter gunner → take missile on **gunner** aircraft → defeat it | Alarm stops for gunner client | | |
| F8 | Leave gunner (**G**) after missile test | No looping alarm audio after leave | | |
| F9 | Press **U** with targets selected (solo / same client) | Targets merge into other seat list per config | | |
| F10 | `ReplaceSharedTargets = true` → share again | Receiver list replaced, not merged | | |
| F11 | End mission → start new mission → possess gunner | No double aim/fire; `[Net] Initialized` once per session | | |
| F12 | Gunner on own aircraft (not piloting, **G** on self) | Possess works; pilot can still fly if another seat active | | |

---

## Two-client network (v1.0 gate)

**Setup:** Client **A** hosts and pilots. Client **B** spectates **A**'s aircraft and possesses gunner.

| # | Step | Expected | Result | Notes |
| --- | --- | --- | --- | --- |
| N1 | Both join mission | Both logs: `[Net] Initialized` | | |
| N2 | **B** presses **G** on **A**'s aircraft | **B** in cockpit view of **A**'s plane; **A** still flies | | |
| N3 | **B** aims | Turret on **A**'s aircraft follows on **both** clients | | |
| N4 | **B** selects HUD target | Owner turret **tracks unit** (not free-aim override) on **both** clients | | |
| N5 | **B** fires | Shots visible on **both** clients | | |
| N6 | **B** holds fire | No obvious double-fire burst per trigger pull | | Low priority if intermittent |
| N7 | On a pilot-controlled turret airframe (e.g. SAH-46 Chicane), **A** cycles between the cannon and another station while **B** owns the cannon | **B** keeps cannon aim/fire authority; turret movement is visible to **A** whether or not cannon is selected by **A** | | |
| N8 | **A** presses **U** (pilot → gunner share) | **B**'s gunner target list updates; **no duplicate** entries | | |
| N9 | **B** presses **U** (gunner → pilot share) | **A**'s pilot list updates; **no duplicate** entries | | |
| N10 | **B** leaves gunner (**G**) | Clean return to spectator; **A** still flies and fires | | |
| N11 | **A** fires own station after **B** left | Pilot weapon works normally | | |
| N12 | **B** re-possesses gunner same mission | Aim/fire still work; no double relay | | |
| N13 | **B** leaves → both stay in mission → **B** re-possesses | No double aim steps or double fire | | Handler re-register regression |
| N14 | End mission → new mission → repeat N2–N5 | Networking still works | | Scene-reset regression |

---

## Multiplayer verify-if-fail

Only needed when a network item fails:

| # | Symptom | What to check |
| --- | --- | --- |
| V1 | Aim mirrored or offset on owner | World vs aircraft-local aim in `TurretController.Aim` |
| V2 | No `[Net] Initialized` | Mirage session not up; retry after fully in mission |
| V3 | Fire on gunner client, nothing on owner | Owner `LocalSim`; BepInEx log for `[Net]` errors |
| V4 | Target lock ignored on owner | Owner receives `TargetNetId` in aim messages |

---

## Airframe spot checks (optional)

Baseline camera offsets are tuned per type. Spot-check any airframe you care about.

| Airframe | Enter gunner | Camera reasonable | Weapon cycle | Fire | Result |
| --- | --- | --- | --- | --- | --- |
| CI-22 Cricket | | | | | |
| T/A-30 Compass | | | | | |
| UH-90 Ibis | | | | | |
| SAH-46 Chicane | | | | | |
| VL-49 Tarantula | | | | | |
| EW-25 Medusa | | | | | |
| SFB-81 Darkreach | | | | | |
| Alkyon AB-4 | | | | | |

Config `CameraOffsets` entries are full camera positions in aircraft-local meters from origin; `.Position2`/`.Position3` use the same coordinate system as position 1.

---

## Sign-off

| Field | Value |
| --- | --- |
| Tester | |
| Date | |
| Mod version | |
| Game build | |
| Solo smoke (S1–S8) | PASS / FAIL |
| Solo functional (F1–F12) | PASS / FAIL |
| Network gate (N1–N13) | PASS / FAIL |
| Blockers | |
| Follow-ups | |
