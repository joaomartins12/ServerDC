using Shared.Util;

namespace Shared.Objects
{
    /// <summary>
    /// XiStrMyVSItem wire record used by VisualItemListAck. The v0.77 packet reserves
    /// exactly 120 bytes per inventory entry.
    /// </summary>
    public class InventoryVisualItem : BinaryWriterExt.ISerializable
    {
        public uint CarId;
        public int ItemState;
        public uint TableIdx;
        public uint InvenIdx;
        public string PlateName;
        public int Period;
        public int UpdateTime;
        public int CreateTime;

        public void Serialize(BinaryWriterExt writer)
        {
            var start = writer.BaseStream.Position;
            writer.Write(CarId);
            writer.Write(ItemState);
            writer.Write(TableIdx);
            writer.Write(InvenIdx);

            // CmdBuyVisualItemThread carries the same generic visual data field as
            // 20 UTF-16 characters. Keep that fixed width in the persisted-list record.
            writer.WriteUnicodeStatic(PlateName ?? string.Empty, 20);
            writer.Write(Period);
            writer.Write(UpdateTime);
            writer.Write(CreateTime);

            var used = writer.BaseStream.Position - start;
            const int recordSize = 120;
            if (used < recordSize)
                writer.Write(new byte[recordSize - used]);
        }
    }
}
