using System;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Util;

namespace GameServer.Network.Handlers.Social
{
    public class FriendList
    {
        private const int FriendsPerPacket = 12;
        private const uint FinalListFlag = 0x00040000;
        private const uint MoreListFlag = 0x00040001;

        [Packet(Packets.CmdFriendList)]
        public static void Handle(Packet packet)
        {
            if (packet.Sender?.User == null) return;

            var friends = FriendModel.Retrieve(
                GameServer.Instance.Database.Connection,
                packet.Sender.User.ActiveCharacterId);

            // The friendship table is persistent data only. Online state, serial and
            // location must come from the currently connected GameServer session.
            foreach (var friend in friends)
                ApplyLivePresence(friend);

            var packetCount = Math.Max(1, (friends.Count + FriendsPerPacket - 1) / FriendsPerPacket);
            for (var packetIndex = 0; packetIndex < packetCount; packetIndex++)
            {
                var start = packetIndex * FriendsPerPacket;
                var count = Math.Min(FriendsPerPacket, Math.Max(0, friends.Count - start));
                var ack = new Packet(Packets.FriendListAck);

                ack.Writer.Write(count);
                ack.Writer.Write(packetIndex + 1 < packetCount ? MoreListFlag : FinalListFlag);

                for (var i = 0; i < count; i++)
                    WriteFriend(ack, friends[start + i]);

                packet.Sender.Send(ack);
            }

            Log.Debug("FriendList: owner={0} friends={1} online={2}",
                packet.Sender.User.ActiveCharacter?.Name ?? "UNKNOWN",
                friends.Count,
                friends.FindAll(x => x.Serial != 0).Count);
        }

        private static void ApplyLivePresence(Friend friend)
        {
            if (friend == null || string.IsNullOrWhiteSpace(friend.CharacterName)) return;

            var liveClient = GameServer.Instance.Server.GetClient(friend.CharacterName);
            var character = liveClient?.User?.ActiveCharacter;

            if (character == null || liveClient.User.VehicleSerial == 0)
            {
                // v0.77a treats a zero session serial/location type as offline.
                friend.Serial = 0;
                friend.LocationType = (char)0;
                friend.CurCarGrade = 0;
                return;
            }

            friend.Serial = liveClient.User.VehicleSerial;
            friend.Level = character.Level;
            friend.CurCarGrade = character.ActiveCar == null ? 0u : character.ActiveCar.Grade;

            // Location type 2 is the normal live city/channel context used by the
            // same v0.77a profile flow. Type 0 is offline; type 1 is Area/PvP.
            friend.LocationType = (char)2;
            friend.ChannelId = (char)(character.LastChannel < 0 ? 0 : character.LastChannel);
            friend.LocationId = (ushort)Math.Max(0, character.City);

            if (character.Crew != null)
            {
                friend.CrewId = character.Crew.Id;
                friend.CrewMarkId = character.Crew.MarkId;
                friend.CrewName = character.Crew.Name;
            }

            Log.Debug(
                "FriendList live: Name={0} Serial={1} LocType={2} Channel={3} LocId={4} Level={5} Grade={6}",
                friend.CharacterName,
                friend.Serial,
                (int)friend.LocationType,
                (int)friend.ChannelId,
                friend.LocationId,
                friend.Level,
                friend.CurCarGrade);
        }

        /// <summary>
        /// Drift City v0.77a friend unit is exactly 112 bytes:
        /// Name[21], CrewName[13], CID, CrewId, CrewMarkId, State,
        /// LocationType(wchar), ChannelId(wchar), LocationId(u16),
        /// Level(u16), CurCarGrade(u32), Serial(u32).
        ///
        /// Do not use the old DCNC serializer here: that old research layout wrote
        /// an additional serial before LocationType and is four bytes too long for
        /// the packet consumed by this client build.
        /// </summary>
        private static void WriteFriend(Packet ack, Friend friend)
        {
            ack.Writer.WriteUnicodeStatic(friend.CharacterName, 21, true);
            ack.Writer.WriteUnicodeStatic(friend.CrewName, 13, true);
            ack.Writer.Write(friend.CharacterId);
            ack.Writer.Write(friend.CrewId);
            ack.Writer.Write(friend.CrewMarkId);
            ack.Writer.Write(friend.State);

            ack.Writer.Write(friend.LocationType);
            ack.Writer.Write(friend.ChannelId);
            ack.Writer.Write(friend.LocationId);
            ack.Writer.Write(friend.Level);
            ack.Writer.Write(friend.CurCarGrade);
            ack.Writer.Write(friend.Serial);
        }
    }
}
