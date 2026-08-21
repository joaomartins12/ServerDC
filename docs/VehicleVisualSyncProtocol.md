# Drift City v0.77a — Vehicle / Visual Synchronization

This document records the packet layouts verified directly against `DriftCity.exe` and the 2026-08-21 packet captures.

## Important architectural split

The client keeps at least three separate states for a player vehicle:

1. **Garage/local car state** — complete `XiStrCarInfo`, including `Color` and `Color2`.
2. **Player identity / cosmetic state** — `XiPlayerInfo` + `XiVisualItem`.
3. **Spawned world vehicle state** — vehicle object created/updated by the AreaServer movement path.

Updating one state does not automatically rebuild the others. This is why the inventory preview can be correct while the world car remains default or uses an old colour.

## Packet map

| ID | Client handler | Retail packet size | Purpose | Server status |
|---:|---:|---:|---|---|
| 541 | `0x51D9E0` | core handler uses `0x6E`; observed client packet buffer is 116 bytes | Area/world movement and remote vehicle discovery | Implemented/relayed by AreaServer |
| 563 | `0x51CAA0` | fixed response | Enter-area acknowledgement | Implemented |
| 802 | `0x52DA40` | `6 + 216*N` | Player identity snapshot (`XiPlayerInfo`) | Implemented |
| 809 | `0x52D9D0` | `6 + 216*N` | Live player-info collection update | Implemented, but **not sufficient for car colour** |
| 1061 | `0x529FD0` | **61 bytes** | Local `XiStrCarInfo`; contains `Color` and `Color2` | Implemented |
| 1201 | `0x52EFB0` | `10 + 120*N` | Visual-item inventory list | Implemented |
| 1202 | `0x52F020` | `6 + 124*N` | Visual-item add/update/delete list | Implemented |
| 1204 | `0x52A2D0` | fixed retail buy response | Buy visual item | Implemented |
| 1206 | `0x52A6D0` | **14 bytes** | Equip visual item acknowledgement | Implemented |
| 1208 | `0x52A700` | **10 bytes** | Unequip visual item acknowledgement | Implemented |
| 467 | `0x5402E0` | **240 bytes** | Rebuild/update an already-spawned world vehicle by serial | **Corrected** |

## `XiPlayerInfo`

Both packet 802 and 809 iterate records at an exact **0xD8 / 216-byte stride**.

Verified packet layout:

```text
+0x00  PacketId      WORD
+0x02  Count         DWORD
+0x06  XiPlayerInfo[Count]
```

Therefore packet size is:

```text
6 + (216 * Count)
```

`XiPlayerInfo` carries the player name, serial, character/team data and `XiVisualItem`. It carries body kit, wheels, spoiler, neon, decal, plate, etc. It does **not** provide the complete car-colour state required to rebuild the world vehicle.

## Packet 1061 — `Cmd_VisualUpdate`

Handler: `0x529FD0`

Exact retail size: **61 bytes including packet id**.

```text
+0x00  PacketId       WORD
+0x02  Serial         WORD
+0x04  Age            WORD
+0x06  CarId          DWORD
+0x0A  VisualState    BYTE
+0x0B  XiStrCarInfo   50 bytes
```

Important verified reads:

- handler compares `WORD [packet+0x02]` with the **local** vehicle serial;
- `DWORD [packet+0x2B]` is written as car `Color`;
- `DWORD [packet+0x2F]` is written as car `Color2`.

Therefore packet 1061 is a **local-player car-state packet**. Sending it with another player's serial is rejected by the client.

This packet explains why the inventory/garage preview can show the correct paint and tint even while the spawned world object is still wrong.

## Packet 467 — `Cmd_RoomNotifyChange`

Handler: `0x5402E0`

This was the missing/malformed world-visual packet.

The handler returns `0xF0`, so the packet beginning at the 2-byte packet id is exactly **240 bytes**.

Disassembly proves these offsets:

```text
+0x00  PacketId        WORD
+0x02  Serial          DWORD
+0x06  Age             WORD
+0x08  XiCarAttr       16 bytes
+0x18  XiPlayerInfo   216 bytes
--------------------------------
Total                  240 bytes
```

Proof points from the executable:

- `MOV EAX,[ESI+0x02]` — target serial/context is a **DWORD**, not a WORD;
- `LEA ECX,[ESI+0x08]` — `XiCarAttr` starts at `+0x08`;
- `LEA ECX,[ESI+0x18]` — `XiPlayerInfo` starts at `+0x18`;
- the handler looks up the world vehicle before applying the snapshot; if the vehicle does not exist yet, the update is discarded.

The old emulator layout was incorrect:

```text
Serial WORD + XiCarAttr 8 + XiPlayerInfo + artificial tail
```

That kept the overall size near the expected value but shifted every field the client actually reads. In the 2026-08-21 capture, the client would interpret bytes belonging to the old 8-byte attribute / player name as the car body and attributes, explaining default cars and incorrect colours.

## Correct `XiCarAttr` — 16 bytes

The function at `0x4C8BB0` copies **four DWORDs** from the packet into the world vehicle's cached attribute block.

The client-side attribute producer at `0x4C8B00` generates:

```text
+0x00  Sort      WORD
+0x02  Body      WORD
+0x04  Color     DWORD
+0x08  Color2    DWORD
+0x0C  State     DWORD   (retail player-car path generates 1)
```

Total: **16 bytes**.

`Color2` is especially important for the current server because window tint is stored in the active vehicle as RGB565. The previous 8-byte emulator structure physically had nowhere to send it to a remote world vehicle.

## Timing / ordering

Packet 467 only updates an **existing** world vehicle.

The current flow is therefore intentionally split:

### Own vehicle

1. visual inventory / character load resolves paint and tint;
2. packet 1061 loads complete local `XiStrCarInfo`;
3. AreaServer creates the world vehicle through the 541 path;
4. delayed GameServer resync sends **1061 + 467** after world creation.

### Remote vehicle

1. GameServer publishes 802 identity state;
2. AreaServer 541 creates/discovers the remote world vehicle;
3. delayed GameServer pass sends **467** with corrected `XiCarAttr + XiPlayerInfo`;
4. 806 supplies license state.

Live VShop changes can send 467 immediately because the world vehicle is already present.

## Packet 541

Registered handler: `0x51D9E0`.

The packet is the AreaServer movement/discovery stream. The client handler passes a `0x6E`-byte core to the world movement manager. Captured packets from the retail client have a 116-byte packet buffer (118 bytes including the TCP length prefix), so six additional bytes exist outside the core consumed by that manager.

The current server correctly preserves and relays the client's movement body verbatim instead of reconstructing it. This packet is responsible for creating/updating the world object, but it does not replace packet 467's complete visual snapshot.

## Visual Shop packet sizes

### 1201 — VisualItemListAck

Handler `0x52EFB0` computes:

```text
size = 0x0A + 0x78 * Count
     = 10 + 120 * Count
```

Entries begin at packet `+0x0A`.

### 1202 — VSItemModList

Handler `0x52F020` computes:

```text
size = 0x06 + 0x7C * Count
     = 6 + 124 * Count
```

The extra 4 bytes versus the 120-byte visual item are the modification operation (`add/update/delete`).

### 1206 — EquipVisualItemAck

Handler `0x52A6D0` returns `0x0E`: **14 bytes total**, matching packet id + three DWORDs.

### 1208 — UnEquipVisualItemAck

Handler `0x52A700` returns `0x0A`: **10 bytes total**, matching packet id + two DWORDs.

## About packet 805

The previous emulator comments associated a render-invalidating path with packet 805. Reverse checking the executable shows the function previously cited for that behaviour (`0x52DB10`) is registered as **packet 185 / 0xB9**, not packet 805.

Therefore 805 must not be treated as a proven retail visual-sync packet. The corrected world-appearance implementation no longer relies on it.

## Current synchronization responsibility

- **AreaServer:** connection ownership, area membership, 541 movement/discovery/replay.
- **GameServer 802:** identity / `XiPlayerInfo` cache.
- **GameServer 1061:** local complete car state (`XiStrCarInfo`).
- **GameServer 1201/1202:** visual inventory and visual-item modifications.
- **GameServer 467:** spawned-world vehicle appearance (`XiCarAttr + XiPlayerInfo`).
- **GameServer 806:** license metadata.

This separation matches the observed client behaviour: inventory correctness does not imply world-render correctness, and remote movement visibility does not imply remote visual correctness.
