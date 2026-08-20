using System;
using System.Collections.Generic;
using System.Data.Common;
using MySql.Data.MySqlClient;
using Shared.Database;
using Shared.Objects;

namespace Shared.Models
{
    public class ItemModel
    {
        public static List<InventoryItem> RetrieveAll(MySqlConnection dbconn, ulong characterId)
        {
            using (var command = new MySqlCommand("SELECT * FROM items WHERE CharacterId = @cid ORDER BY InventoryIndex ASC", dbconn))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                var items = new List<InventoryItem>();
                using (DbDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read()) items.Add(InventoryItem.ReadFromDb(reader));
                }
                return items;
            }
        }

        public static void RetrieveAll(MySqlConnection dbconn, ref Character character)
        {
            if (character == null) return;
            var items = RetrieveAll(dbconn, character.Id);
            character.InventoryItems.Clear();
            character.InventoryItems.AddRange(items);
        }

        public static void Update(MySqlConnection dbconn, InventoryItem inventoryItem)
        {
            using (var cmd = new UpdateCommand("UPDATE items SET {0} WHERE Id=@id", dbconn))
            {
                cmd.AddParameter("@id", inventoryItem.DbId);
                var updateCommand = cmd;
                inventoryItem.WriteToDb(ref updateCommand);
                cmd.Execute();
            }
        }

        public static InventoryItem RetrieveOne(MySqlConnection dbconn, long id)
        {
            using (var command = new MySqlCommand("SELECT * FROM items WHERE Id=@id", dbconn))
            {
                command.Parameters.AddWithValue("@id", id);
                using (DbDataReader reader = command.ExecuteReader())
                    return reader.Read() ? InventoryItem.ReadFromDb(reader) : null;
            }
        }

        public static bool Create(MySqlConnection dbconn, InventoryItem item)
        {
            // CarId=0 is valid for an unequipped inventory item.
            if (item == null || item.CharacterId == 0 || item.StackNum == 0 || item.TableIndex < 0)
                return false;

            // InventoryItems is a compact List<InventoryItem>, but InventoryIndex is a persistent
            // slot number. After an item/key is removed, List.Count is no longer a safe slot id:
            // another existing row can already own that number. Always allocate the first slot
            // that is actually free in the database before inserting a new inventory item.
            item.InventoryIndex = FindFirstFreeInventoryIndex(dbconn, item.CharacterId);

            using (var cmd = new InsertCommand("INSERT INTO `items` {0}", dbconn))
            {
                var insertCommand = cmd;
                item.WriteToDb(ref insertCommand);
                var result = cmd.Execute();
                if (result == 1 && cmd.LastId > 0)
                    item.DbId = checked((int)cmd.LastId);
                return result == 1;
            }
        }

        private static uint FindFirstFreeInventoryIndex(MySqlConnection dbconn, ulong characterId)
        {
            var expected = 0u;
            using (var command = new MySqlCommand(
                "SELECT InventoryIndex FROM items WHERE CharacterId=@cid ORDER BY InventoryIndex ASC", dbconn))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (DbDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var current = Convert.ToUInt32(reader[0]);
                        if (current < expected)
                            continue;
                        if (current > expected)
                            break;
                        expected++;
                    }
                }
            }
            return expected;
        }

        public static bool Remove(MySqlConnection dbconn, ulong charId, int slot)
        {
            using (var command = new MySqlCommand("DELETE FROM `items` WHERE CharacterId = @cid AND InventoryIndex = @slot", dbconn))
            {
                command.Parameters.AddWithValue("@slot", slot);
                command.Parameters.AddWithValue("@cid", charId);
                return command.ExecuteNonQuery() == 1;
            }
        }
    }
}
