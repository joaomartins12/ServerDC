using Shared.Network.GameServer;
using Shared.Objects;

namespace GameServer.Util
{
    /// <summary>
    /// Builds the player/car visual context used by the client when it needs to
    /// render another character (world presence, room updates and User Information).
    /// Keeping this in one place prevents packet 802 and packet 467 from describing
    /// the same player differently.
    /// </summary>
    public static class PlayerVisualSnapshotBuilder
    {
        public static XiPlayerInfo BuildPlayerInfo(ushort serial, Character character)
        {
            return new XiPlayerInfo(serial, character)
            {
                Age = 0,
                VisualItem = BuildVisualItem(character),
                UseTime = 0.0f
            };
        }

        public static RoomNotifyChangeAnswer BuildRoomNotifyChange(ushort serial, Character character)
        {
            return new RoomNotifyChangeAnswer
            {
                Serial = serial,
                Age = 0,
                CarAttr = BuildCarAttr(character == null ? null : character.ActiveCar),
                PlayerInfo = BuildPlayerInfo(serial, character)
            };
        }

        /// <summary>
        /// XiCarAttr is an 8-byte union:
        ///   ushort Sort, ushort Body, byte Color[4]
        /// For a normal player vehicle Sort is 0 (player car), Body identifies the
        /// client vehicle model and Color is the vehicle color value.
        /// </summary>
        public static XiCarAttr BuildCarAttr(Vehicle vehicle)
        {
            var result = new XiCarAttr();
            if (vehicle == null)
                return result;

            const ushort playerCarSort = 0;
            var body = unchecked((ushort)vehicle.CarType);
            var color = vehicle.Color != 0 ? vehicle.Color : vehicle.BaseColor;

            var packed = (ulong)playerCarSort |
                         ((ulong)body << 16) |
                         ((ulong)color << 32);

            result.___u0.__s0.Sort = playerCarSort;
            result.___u0.__s0.Body = body;
            result.___u0.__s1.lvalSortBody = unchecked((int)(uint)packed);
            result.___u0.__s1.lvalColor = unchecked((int)color);
            result.___u0.llval = unchecked((long)packed);
            return result;
        }

        private static XiVisualItem BuildVisualItem(Character character)
        {
            // Persistent visual-shop equipment is not implemented yet. Returning the
            // native zero/default structure is intentional: it asks the client to render
            // the stock body for CarAttr.Body rather than inventing visual parts.
            // This method is centralized so real equipped visual items can be added here
            // later without changing every player-info packet.
            return new XiVisualItem
            {
                PlateString = string.Empty
            };
        }
    }
}
