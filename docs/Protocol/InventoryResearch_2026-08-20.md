# Inventory protocol research — 2026-08-20 captures

Source: structured packet capture archive supplied from live Drift City client sessions.

## Confirmed login inventory sequence

Across four complete GameServer login sessions the client consistently performs:

1. `CmdCheckInGame (120)`
2. `CmdLoadCharThread (123)`
3. `MyQuestList (272)`
4. `CmdItemList (400)`
5. server sends `ItemListAck (401)`
6. `CmdVisualItemList (1200)`
7. later the client sends `CmdInventoryRequest (1156)`

`CmdItemList (400)` is a header-only request (4 wire bytes).

Observed empty `ItemListAck (401)`:

```
0C 00 91 01 00 00 04 00 00 00 00 00
```

Wire size: 12 bytes.

This strongly supports using packet 400/401 as the authoritative initial inventory load flow.

## CmdInventoryRequest (1156 / 0x484)

Observed eight times in the supplied capture archive.

Every observed request is header-only:

```
04 00 84 04
```

No immediately corresponding outgoing packet with an obvious `1157` identity exists in these captures. Do **not** invent an ACK yet. Controlled client actions are needed to establish whether:

- it expects a differently-numbered response;
- it is only a client refresh notification;
- the current emulator is missing a response entirely;
- another later packet represents the requested state.

## CmdUpdateQuickSlot (2000 / 0x7D0)

Observed packet size: 28 bytes.

The existing handler reads only two UInt32 values, but captured payloads include data that can decode as UTF-16 fragments (`ding`, `rtuga`) in two sessions. That makes the current two-UInt32 interpretation unproven and likely incomplete.

Do not persist quickslots until we run controlled tests changing exactly one quickslot at a time and compare packet deltas.

## Server-side inventory model findings

The current database `items` table should remain the per-character inventory-instance table. `item_catalog` is the definition table.

Important fields present in `InventoryItem.Serialize`:

- CarId
- State
- Slot
- StackNum
- LastCarId
- AssistA..AssistJ
- Box
- Belonging
- Upgrade
- UpgradePoint
- ExpireTick
- Durability
- TableIndex
- InventoryIndex
- Random

The runtime item definition is resolved through `TableIndex` against Items.xml + UseItems.xml / ItemCatalog.json.

## Bugs corrected while reviewing captures

- repeated ItemList loads no longer append duplicate objects to `Character.InventoryItems`;
- ItemList now performs one database query rather than two identical queries;
- `ItemModel.Create` now allows `CarId = 0` because an unequipped item is valid;
- ItemList logging resolves TableIndex to ItemId, name and category for protocol debugging;
- inventory queries are ordered by InventoryIndex for deterministic packet output.

## Next controlled captures required

Run each test separately and keep its packet folder/session:

1. empty inventory login;
2. buy one non-stackable part;
3. relog with that part;
4. buy one stackable use item;
5. buy the same stackable item a second time;
6. equip a part to the active car;
7. unequip that part;
8. move an inventory item to a different slot;
9. sell one item;
10. change exactly one quickslot.

For each test record the visible action and approximate timestamp. Comparing these sessions byte-for-byte will let us identify state, slot, stack, CarId and quickslot semantics without client source code.
