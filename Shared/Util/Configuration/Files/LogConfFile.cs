using Shared.Network;

namespace Shared.Util.Configuration.Files
{
    /// <summary>
    /// Represents log.conf. Runtime persistence is handled by the unified Logs/ system;
    /// this configuration now controls only console visibility and keeps legacy dump flags readable.
    /// </summary>
    public class LogConfFile : ConfFile
    {
        public bool Archive { get; protected set; }
        public LogLevel Hide { get; protected set; }
        public bool DumpIncomingPackets { get; protected set; }
        public bool DumpOutgoingPackets { get; protected set; }

        public void Load()
        {
            Require("system/conf/log.conf");

            Archive = GetBool("archive", true);
            Hide = (LogLevel)GetInt("cmd_hide", (int)LogLevel.Debug);
            Log.Hide |= Hide;

            // Retain the settings for compatibility with existing configuration files,
            // but the old packetcaptures/ and log/ writers are intentionally disabled.
            // All IN/OUT packets are now captured unconditionally by Log.PacketTrace under Logs/.
            DumpIncomingPackets = GetBool("dump_incoming", true);
            DumpOutgoingPackets = GetBool("dump_outgoing", true);
            DefaultServer.DumpIncoming = false;
            DefaultServer.DumpOutgoing = false;

            Log.InitializeStructuredLogging();
        }
    }
}
