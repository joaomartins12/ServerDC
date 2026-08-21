using System.IO;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.LobbyServer
{
    /// <summary>
    /// sub_53E260
    /// </summary>
    public class UserInfoAnswerPacket : OutPacket
    {
        public Character[] Characters;
        public int Permissions;
        public string Username;

        public UserInfoAnswerPacket()
        {
            Permissions = 0;
            Username = "";
            Characters = new Character[0];
        }

        public override Packet CreatePacket()
        {
            return base.CreatePacket(Packets.UserInfoAck);
        }

        public override int ExpectedSize() => (120 * (Characters.Length - 1)) + 194;

        private static uint GetClientColor(Vehicle vehicle)
        {
            if (vehicle == null) return 0;
            return vehicle.Color != 0 ? vehicle.Color : vehicle.BaseColor;
        }

        private static void WriteCharacterShort(BinaryWriterExt bs, Character character)
        {
            var car = character.ActiveCar ?? new Vehicle();

            bs.WriteUnicodeStatic(character.Name, 21);
            bs.Write(character.Id);
            bs.Write((int)character.Avatar);
            bs.Write((int)character.Level);
            bs.Write(character.ActiveVehicleId);
            bs.Write(car.CarType);

            // Character Select is one of the client-side roots for the current car.
            // Retail uses this colour while constructing the preview/world car cache.
            // Sending the immutable catalogue BaseColor here made a visually-painted car
            // fall back to the model default after returning to Server/Character Select.
            bs.Write(GetClientColor(car));

            bs.Write(character.CreationDate);
            bs.Write(character.CrewId);
            if (character.Crew != null)
            {
                bs.Write(character.Crew.MarkId);
                bs.WriteUnicodeStatic(character.Crew.Name, 13);
                bs.Write((short)character.CrewRank);
            }
            else
            {
                bs.Write(0L);
                bs.WriteUnicodeStatic("", 13);
                bs.Write((short)0);
            }
            bs.Write(character.Guild);
        }

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            using (var bs = new BinaryWriterExt(ms))
            {
                bs.Write(Permissions);
                bs.Write(Characters.Length);
                bs.WriteUnicodeStatic(Username, 18);
                bs.Write((long)0);
                bs.Write((long)0);
                bs.Write((long)0);
                bs.Write(0);

                foreach (var character in Characters)
                    WriteCharacterShort(bs, character ?? new Character());

                return ms.ToArray();
            }
        }
    }
}
