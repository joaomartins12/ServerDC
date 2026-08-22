using System.Collections.Generic;

namespace Shared.Objects
{
    /// <summary>
    /// Retail Drift City keeps a small generation counter for each live character.
    /// JoinChannel starts a fresh generation and appearance changes advance it. The
    /// client includes the same generation in movement packets and uses it to reject
    /// stale world objects, so leaving it at zero can eventually cull valid players.
    /// </summary>
    public static class WorldSessionAge
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<ulong, ushort> Ages = new Dictionary<ulong, ushort>();

        public static ushort Begin(ulong characterId)
        {
            if (characterId == 0) return 1;
            lock (Sync)
            {
                Ages[characterId] = 1;
                return 1;
            }
        }

        public static ushort Get(ulong characterId, ushort fallback = 0)
        {
            if (characterId == 0) return fallback;
            lock (Sync)
            {
                ushort age;
                return Ages.TryGetValue(characterId, out age) && age != 0 ? age : fallback;
            }
        }

        public static ushort Advance(ulong characterId)
        {
            if (characterId == 0) return 1;
            lock (Sync)
            {
                ushort current;
                if (!Ages.TryGetValue(characterId, out current) || current == 0)
                    current = 1;
                else
                {
                    unchecked { current++; }
                    if (current == 0) current = 1;
                }
                Ages[characterId] = current;
                return current;
            }
        }

        public static void Remove(ulong characterId)
        {
            if (characterId == 0) return;
            lock (Sync) Ages.Remove(characterId);
        }
    }
}
