using System;
using System.IO;
using Shared.Network.AreaServer;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// sub_529160
    /// PKTSIZE: 1177-byte body.
    ///
    /// The tail layout was reconstructed against Drift City v0.77a's real
    /// Cmd_GameCharInfoAck handler (0x529160). In particular:
    ///  - field_10 +0x47D is a 12-byte profile context. Its first DWORD is
    ///    tested by the client before it creates the 3D vehicle preview.
    ///  - field_11 +0x489 and field_12 +0x48F are both 6-byte XiLicense-like
    ///    records. The client resolves their first WORD through the same
    ///    License.xlt table used by Cmd_NewLicenseNoti (817).
    ///  - field_13 +0x495 remains LocType.
    /// </summary>
    public class GameCharInfoAnswer : OutPacket
    {
        public Character Character;
        public Vehicle Vehicle;
        public XiStrStatInfo StatisticInfo;
        public Crew Crew;

        /// <summary>
        /// Non-zero online/profile context. For an online player this is the
        /// Area/Game vehicle serial; offline callers can use a stable CID-derived value.
        /// A zero value makes v0.77a skip creation of the vehicle preview entirely.
        /// </summary>
        public uint ProfileContextId;

        /// <summary>
        /// Currently equipped license/title id (e.g. 7000 = Rookie).
        /// </summary>
        public int CurrentLicenseId;

        public int LocType = 2;

        public GameCharInfoAnswer()
        {
            Character = new Character();
            Vehicle = new Vehicle();
            StatisticInfo = new XiStrStatInfo();
            Crew = new Crew();
        }

        public override Packet CreatePacket()
        {
            return base.CreatePacket(Packets.GameCharInfoAck);
        }

        public override int ExpectedSize() => 1177;

        private static void WriteLicense(BinaryWriterExt writer, int licenseId, bool equipped)
        {
            var safeId = licenseId > 0 && licenseId <= ushort.MaxValue
                ? (ushort)licenseId
                : (ushort)0;

            writer.Write(safeId);
            writer.Write((ushort)0);
            writer.Write((ushort)(equipped && safeId != 0 ? 1 : 0));
        }

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            {
                using (var bs = new BinaryWriterExt(ms))
                {
                    Character.Serialize(bs);          // +0x002 .. +0x142 (321)
                    Vehicle.Serialize(bs);            // +0x143 .. +0x174 (50)
                    StatisticInfo.Serialize(bs);      // +0x175 .. +0x1E4 (112)

                    if (Crew == null)
                        bs.Write(new byte[664]);
                    else
                        Crew.Serialize(bs);            // ends at +0x47C

                    // field_10 (+0x47D, 12 bytes)
                    // The v0.77a client checks the first DWORD for zero before creating
                    // the profile's 3D vehicle. The remaining two DWORDs are currently
                    // not consumed by the User Information render path we traced.
                    bs.Write(ProfileContextId);
                    bs.Write(0u);
                    bs.Write(0u);

                    // field_11 (+0x489, 6 bytes) - license/title record.
                    WriteLicense(bs, CurrentLicenseId, true);

                    // field_12 (+0x48F, 6 bytes) - license/title record used directly by
                    // both the self and remote User Information render paths.
                    WriteLicense(bs, CurrentLicenseId, true);

                    // field_13 (+0x495)
                    bs.Write(LocType);
                }

                return ms.ToArray();
            }
        }
    }
}
