using System;
using System.Linq;
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
        private const ushort UpdateFriendPacket = 225;
        private const ushort FriendConnectNotifyPacket = 275;

        [Packet(Packets.CmdFriendList)]
        public static void Handle(Packet packet)
        {
            if (packet.Sender?.User == null) return;

            var friends = FriendModel.Retrieve(
                GameServer.Instance.Database.Connection,
                packet.Sender.User.ActiveCharacterId);

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

        /// <summary>
        /// Packet 225 Cmd_UpdateFriend is exactly one 112-byte friend unit in the
        /// retail client. Push it whenever a live friend's AreaId changes so the UI
        /// updates without requiring a manual Refresh/open cycle.
        /// </summary>
        public static void PushLiveUpdate(string characterName)
        {
            if (string.IsNullOrWhiteSpace(characterName)) return;

            foreach (var viewer in GameServer.Instance.Server.GetClients())
            {
                if (viewer?.User?.ActiveCharacter == null) continue;
                if (string.Equals(viewer.User.ActiveCharacter.Name, characterName,
                    StringComparison.OrdinalIgnoreCase)) continue;

                var friends = FriendModel.Retrieve(
                    GameServer.Instance.Database.Connection,
                    viewer.User.ActiveCharacterId);
                var friend = friends.FirstOrDefault(x => string.Equals(
                    x.CharacterName, characterName, StringComparison.OrdinalIgnoreCase));
                if (friend == null) continue;

                ApplyLivePresence(friend);
                var update = new Packet(UpdateFriendPacket);
                WriteFriend(update, friend);
                viewer.Send(update);

                Log.Debug("Friend update: viewer={0} friend={1} online={2} area={3}",
                    viewer.User.ActiveCharacter.Name,
                    friend.CharacterName,
                    friend.Serial != 0,
                    friend.LocationId);
            }
        }

        /// <summary>
        /// Drift City retail packet 275 Cmd_FriendConnectNotify is 45 bytes including
        /// packet id: wchar Name[21] + bool connected. This is the original friend
        /// online/offline popup path in the client.
        /// </summary>
        public static void NotifyConnection(string characterName, bool connected)
        {
            if (string.IsNullOrWhiteSpace(characterName)) return;

            foreach (var viewer in GameServer.Instance.Server.GetClients())
            {
                if (viewer?.User?.ActiveCharacter == null) continue;
                if (string.Equals(viewer.User.ActiveCharacter.Name, characterName,
                    StringComparison.OrdinalIgnoreCase)) continue;

                var friends = FriendModel.Retrieve(
                    GameServer.Instance.Database.Connection,
                    viewer.User.ActiveCharacterId);
                if (!friends.Any(x => string.Equals(x.CharacterName, characterName,
                    StringComparison.OrdinalIgnoreCase)))
                    continue;

                var notify = new Packet(FriendConnectNotifyPacket);
                notify.Writer.WriteUnicodeStatic(characterName, 21, true);
                notify.Writer.Write(connected);
                viewer.Send(notify);

                Log.Debug("FriendConnectNotify: viewer={0} friend={1} connected={2}",
                    viewer.User.ActiveCharacter.Name, characterName, connected);
            }
        }

        private static void ApplyLivePresence(Friend friend)
        {
            if (friend == null || string.IsNullOrWhiteSpace(friend.CharacterName)) return;

            var liveClient = GameServer.Instance.Server.GetClient(friend.CharacterName);
            var character = liveClient?.User?.ActiveCharacter;

            if (character == null || liveClient.User.VehicleSerial == 0)
            {
                friend.Serial = 0;
                friend.LocationType = (char)0;
                friend.ChannelId = (char)0;
                friend.LocationId = 0;
                friend.CurCarGrade = 0;
                return;
            }

            friend.Serial = liveClient.User.VehicleSerial;
            friend.Level = character.Level;
            friend.CurCarGrade = character.ActiveCar == null ? 0u : character.ActiveCar.Grade;

            // The location displayed by Friend List is the LIVE AreaId from packet 300,
            // not Character.City persisted in the DB. If JoinArea has not arrived yet,
            // area 0 is the safe connected/default Driver Dome location.
            int liveArea;
            if (!global::GameServer.Network.Handlers.Join.JoinArea.TryGetLiveArea(
                    friend.CharacterName, out liveArea))
                liveArea = 0;

            friend.LocationType = (char)1;
            friend.ChannelId = (char)Math.Max(0, character.LastChannel);
            friend.LocationId = (ushort)Math.Max(0, liveArea);

            if (character.Crew != null)
            {
                friend.CrewId = character.Crew.Id;
                friend.CrewMarkId = character.Crew.MarkId;
                friend.CrewName = character.Crew.Name;
            }

            Log.Debug(
                "FriendList live: Name={0} Serial={1} LocType={2} Channel={3} AreaId={4} Level={5} Grade={6}",
                friend.CharacterName,
                friend.Serial,
                (int)friend.LocationType,
                (int)friend.ChannelId,
                friend.LocationId,
                friend.Level,
                friend.CurCarGrade);
        }

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
