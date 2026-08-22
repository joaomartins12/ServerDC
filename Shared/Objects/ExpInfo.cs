using System.Collections.Generic;
using Shared.Util;

namespace Shared.Objects
{
    public struct ExpInfo : BinaryWriterExt.ISerializable
    {
        public long CurExp;
        public long NextExp;
        public long BaseExp;

        public void Serialize(BinaryWriterExt writer)
        {
            writer.Write(CurExp);
            writer.Write(NextExp);
            writer.Write(BaseExp);
        }
    }

    /// <summary>
    /// Retail Drift City keeps a small generation counter for each live character.
    /// JoinChannel starts a fresh generation and appearance changes advance it. The
    /// client uses the same generation to reject stale world objects, so keeping it
    /// permanently at zero can make otherwise valid player objects disappear.
    /// Kept in this already-compiled Shared source file because the solution uses
    /// classic explicit Compile entries rather than SDK-style wildcard inclusion.
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

        public static ushort Get(ulong characterId, ushort fallback = 1)
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
                {
                    current = 1;
                }
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
