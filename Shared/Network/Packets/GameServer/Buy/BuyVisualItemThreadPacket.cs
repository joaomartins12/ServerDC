namespace Shared.Network.GameServer
{
    /// <summary>
    /// CmdBuyVisualItemThread (1203) request used by Drift City v0.77a.
    /// The client builder allocates 0x44 bytes including the packet id, so the
    /// request payload after the id is exactly 66 bytes.
    /// </summary>
    public class BuyVisualItemThreadPacket
    {
        public uint TableIndex;
        public uint CarId;
        public string PlateName;

        public uint PeriodIdx;

        // The retail builder writes these as two independent bytes at packet
        // offsets +0x36/+0x37. The original server names the first one UseMileage.
        public byte UseMileageRaw;
        public byte BuyFlagRaw;

        // The client writes three separate DWORDs at +0x38, +0x3C and +0x40.
        // CurCash is represented by the first two DWORDs in the original server
        // structure; keep the individual values as well so no wire data is lost.
        public uint CashLow;
        public uint CashHigh;
        public uint TailValue;

        public bool UseMileage => UseMileageRaw != 0;
        public long Cash => unchecked((long)(((ulong)CashHigh << 32) | CashLow));

        public BuyVisualItemThreadPacket(Packet packet)
        {
            TableIndex = packet.Reader.ReadUInt32();
            CarId = packet.Reader.ReadUInt32();
            PlateName = packet.Reader.ReadUnicodeStatic(20);
            PeriodIdx = packet.Reader.ReadUInt32();
            UseMileageRaw = packet.Reader.ReadByte();
            BuyFlagRaw = packet.Reader.ReadByte();
            CashLow = packet.Reader.ReadUInt32();
            CashHigh = packet.Reader.ReadUInt32();
            TailValue = packet.Reader.ReadUInt32();
        }
    }
}
