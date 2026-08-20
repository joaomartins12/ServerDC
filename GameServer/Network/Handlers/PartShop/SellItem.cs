using System;
using System.Linq;
using MySql.Data.MySqlClient;
using Shared;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public class SellItem
    {
        [Packet(Packets.CmdSellItem)]
        public static void Handle(Packet packet)
        {
            var sellItemPacket = new SellItemPacket(packet);
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null)
            {
                packet.Sender.SendDebugError("No active character");
                return;
            }

            // Trust the owned inventory slot as the authoritative item instance. The packet's
            // TableIndex is still logged/echoed, but price resolution follows the exact item the
            // character actually owns so a forged/mismatched packet cannot select another price.
            var inventoryItem = character.InventoryItems == null
                ? null
                : character.InventoryItems.FirstOrDefault(i => i != null && i.InventoryIndex == sellItemPacket.Slot);
            if (inventoryItem == null)
            {
                packet.Sender.SendDebugError("Inventory item not found");
                return;
            }

            var clientTableIndex = inventoryItem.TableIndex;
            BasicItem itemData;
            int catalogIndex;
            if (!TryResolveInventoryItem(clientTableIndex, out catalogIndex, out itemData))
            {
                packet.Sender.SendDebugError($"Item TableIndex {clientTableIndex} could not be resolved for selling!");
#if !DEBUG
                packet.Sender.KillConnection("Invalid shop item");
#endif
                return;
            }

            uint unitPrice;
            if (itemData.SellValue == "n/a" || !uint.TryParse(itemData.SellValue, out unitPrice))
            {
                packet.Sender.SendDebugError($"No sell price ({itemData.SellValue}) for item {itemData.Name}");
#if !DEBUG
                packet.Sender.KillConnection("Price missing!");
#endif
                return;
            }

            var price = checked(unitPrice * sellItemPacket.Quantity);

            Log.Info(
                "SellItem resolve: Slot={0} PacketTableIndex={1} InventoryTableIndex={2} CatalogIndex={3} ItemId={4} Name={5} UnitSellValue={6} Quantity={7} Total={8}",
                sellItemPacket.Slot,
                sellItemPacket.TableIndex,
                clientTableIndex,
                catalogIndex,
                itemData.Id,
                itemData.Name,
                unitPrice,
                sellItemPacket.Quantity,
                price);

            if (!character.RemoveItem(
                GameServer.Instance.Database.Connection,
                (int)sellItemPacket.Slot,
                sellItemPacket.Quantity))
            {
                packet.Sender.SendDebugError("Removing item failure");
                return;
            }

            character.MitoMoney += price;
            CharacterModel.Update(GameServer.Instance.Database.Connection, character);

            packet.Sender.Send(new SellItemAnswer()
            {
                TableIndex = sellItemPacket.TableIndex,
                Quantity = sellItemPacket.Quantity,
                Money = price,
                Slot = sellItemPacket.Slot
            }.CreatePacket());

            character.FlushItemModBuffer(packet.Sender);
        }

        private static bool TryResolveInventoryItem(int clientTableIndex, out int catalogIndex, out BasicItem itemData)
        {
            catalogIndex = -1;
            itemData = null;

            if (ServerMain.Items == null || ServerMain.Items.Count == 0 || clientTableIndex < 0)
                return false;

            string itemId;
            if (TryGetClientItemId(clientTableIndex, out itemId))
            {
                for (var i = 0; i < ServerMain.Items.Count; i++)
                {
                    var candidate = ServerMain.Items[i];
                    if (candidate == null || !string.Equals(candidate.Id, itemId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    catalogIndex = i;
                    itemData = candidate;
                    return true;
                }

                Log.Warning(
                    "SellItem client lookup resolved TableIndex={0} to ItemId={1}, but that ItemId was not found in server items.",
                    clientTableIndex,
                    itemId);
            }

            // Compatibility fallback for old inventories/imports where the runtime index still
            // happens to be identical to the client index.
            if (clientTableIndex < ServerMain.Items.Count && ServerMain.Items[clientTableIndex] != null)
            {
                catalogIndex = clientTableIndex;
                itemData = ServerMain.Items[catalogIndex];
                Log.Warning(
                    "SellItem using legacy direct index fallback for ClientTableIndex={0} ItemId={1}. Import client Item/UseItem data for authoritative mapping.",
                    clientTableIndex,
                    itemData.Id);
                return true;
            }

            return false;
        }

        private static bool TryGetClientItemId(int clientTableIndex, out string itemId)
        {
            itemId = null;

            try
            {
                using (var cmd = new MySqlCommand(@"
IF OBJECT_ID(N'dbo.client_item_lookup', N'U') IS NOT NULL
BEGIN
    SELECT TOP (1) ItemId
    FROM dbo.client_item_lookup
    WHERE ClientTableIndex=@tableIndex;
END", GameServer.Instance.Database.Connection))
                {
                    cmd.Parameters.AddWithValue("@tableIndex", clientTableIndex);
                    var value = cmd.ExecuteScalar();
                    if (value == null || value == DBNull.Value)
                        return false;

                    itemId = Convert.ToString(value).Trim();
                    return !string.IsNullOrWhiteSpace(itemId);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("SellItem client item lookup failed for TableIndex={0}: {1}", clientTableIndex, ex.Message);
                return false;
            }
        }
    }
}
