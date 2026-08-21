using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network
{
    public class Client
    {
        private readonly NetworkStream _ns;
        private readonly DefaultServer _parent;
        private readonly TcpClient _tcp;
        private readonly object _sendSync = new object();

        private byte[] _buffer;
        private int _bytesToRead;

        private bool _connected;
        private ushort _packetLength, _packetId;

        public User User;

        public Client(TcpClient tcp, DefaultServer parent, bool exchangeRequired)
        {
            _tcp = tcp;
            _parent = parent;

            _ns = tcp.GetStream();
            _connected = true;

            try
            {
                if (exchangeRequired)
                {
                    _buffer = new byte[56];
                    _bytesToRead = _buffer.Length;
                    _ns.BeginRead(_buffer, 0, 56, OnExchange, null);
                }
                else
                {
                    _buffer = new byte[4];
                    _bytesToRead = _buffer.Length;
                    _ns.BeginRead(_buffer, 0, 4, OnHeader, null);
                }
            }
            catch (Exception ex)
            {
                KillConnection(ex);
            }
        }

        ~Client()
        {
            KillConnection();
        }

        public IPEndPoint EndPoint => _tcp.Client.RemoteEndPoint as IPEndPoint;

        private int LocalPort
        {
            get
            {
                try
                {
                    var endpoint = _tcp.Client.LocalEndPoint as IPEndPoint;
                    return endpoint == null ? 0 : endpoint.Port;
                }
                catch
                {
                    return 0;
                }
            }
        }

        private string RemoteEndpointText
        {
            get
            {
                try
                {
                    return _tcp.Client.RemoteEndPoint == null ? string.Empty : _tcp.Client.RemoteEndPoint.ToString();
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        private string Username => User == null ? string.Empty : User.Username;
        private string CharacterName => User == null || User.ActiveCharacter == null ? string.Empty : User.ActiveCharacter.Name;

        private void OnExchange(IAsyncResult result)
        {
            try
            {
                _bytesToRead -= _ns.EndRead(result);
                if (_bytesToRead > 0)
                {
                    _ns.BeginRead(_buffer, _buffer.Length - _bytesToRead, _bytesToRead, OnExchange, null);
                    return;
                }

                lock (_sendSync)
                    _ns.Write(new byte[56], 0, 56);

                _buffer = new byte[4];
                _bytesToRead = _buffer.Length;
                _ns.BeginRead(_buffer, 0, 4, OnHeader, null);
            }
            catch (Exception ex)
            {
                KillConnection(ex);
            }
        }

        private void OnHeader(IAsyncResult result)
        {
            try
            {
                _bytesToRead -= _ns.EndRead(result);
                if (_bytesToRead > 0)
                {
                    _ns.BeginRead(_buffer, _buffer.Length - _bytesToRead, _bytesToRead, OnHeader, null);
                    return;
                }

                _packetLength = BitConverter.ToUInt16(_buffer, 0);
                _packetId = BitConverter.ToUInt16(_buffer, 2);

                _bytesToRead = _packetLength - 4;
                _buffer = new byte[_bytesToRead];
                _ns.BeginRead(_buffer, 0, _bytesToRead, OnData, null);
            }
            catch (Exception ex)
            {
                KillConnection(ex);
            }
        }

        private void OnData(IAsyncResult result)
        {
            try
            {
                _bytesToRead -= _ns.EndRead(result);
                if (_bytesToRead > 0)
                {
                    _ns.BeginRead(_buffer, _buffer.Length - _bytesToRead, _bytesToRead, OnData, null);
                    return;
                }

                var wirePacket = new byte[_packetLength];
                Buffer.BlockCopy(BitConverter.GetBytes(_packetLength), 0, wirePacket, 0, 2);
                Buffer.BlockCopy(BitConverter.GetBytes(_packetId), 0, wirePacket, 2, 2);
                if (_buffer.Length > 0)
                    Buffer.BlockCopy(_buffer, 0, wirePacket, 4, _buffer.Length);

                Log.PacketTrace("IN", LocalPort, _packetId, wirePacket,
                    RemoteEndpointText, Username, CharacterName);

                var packet = new Packet(this, _packetId, _buffer);
                _parent.Parse(packet);

                _buffer = new byte[4];
                _bytesToRead = _buffer.Length;
                _ns.BeginRead(_buffer, 0, 4, OnHeader, null);
            }
            catch (Exception ex)
            {
                KillConnection(ex);
            }
        }

        public void Send(Packet packet)
        {
            var buffer = packet.Writer.GetBuffer();
            var bufferLength = buffer.Length;
            var length = (ushort)(bufferLength + 2);

            var wirePacket = new byte[length];
            Buffer.BlockCopy(BitConverter.GetBytes(length), 0, wirePacket, 0, 2);
            if (bufferLength > 0)
                Buffer.BlockCopy(buffer, 0, wirePacket, 2, bufferLength);

            Log.PacketTrace("OUT", LocalPort, packet.Id, wirePacket,
                RemoteEndpointText, Username, CharacterName);

#if DEBUG
            var hexDump = BinaryWriterExt.HexDump(buffer);
            if (!DefaultServer.PacketDumpBlacklist.Contains(packet.Id))
            {
                if (DefaultServer.PacketNameDatabase.ContainsKey(packet.Id))
                    Log.Info("Sending packet {0} ({1} id {2}, 0x{2:X}).", DefaultServer.PacketNameDatabase[packet.Id],
                        Packets.GetName(packet.Id), packet.Id);
                else
                    Log.Info("Sending unnamed packet ({0} id {1}, 0x{1:X}).",
                        Packets.GetName(packet.Id), packet.Id);

                if (bufferLength != 0)
                    Log.Debug("HexDump {0} (Size: {1}):{2}{3}", packet.Id, bufferLength, Environment.NewLine, hexDump);
                else
                    Log.Debug("HexDump {0}:{1}{2}", packet.Id, Environment.NewLine, hexDump);
            }
#endif

            var sent = false;
            try
            {
                lock (_sendSync)
                {
                    if (!_connected) return;
                    _ns.Write(BitConverter.GetBytes(length), 0, 2);
                    _ns.Write(buffer, 0, bufferLength);
                    sent = true;
                }
            }
            catch (Exception ex)
            {
                KillConnection(ex);
            }

            // DriftCity v0.77a keeps the logical PlayerInfo snapshot and the spawned
            // world vehicle render as separate states. Cmd_PlayerInfoRes (809) updates
            // XiPlayerInfo, but the client handler at the SetVisualItem path (805) is
            // what marks an already-created remote vehicle dirty. Mode 4 is a 16-byte
            // command whose +0x04 WORD is the target serial; it sets renderDirty=1 and
            // resets the pending rebuild state. Follow every successful 809 with that
            // invalidation so initial discovery and live VShop changes behave identically.
            if (sent && packet.Id == PlayerInfoOldAnswer.PlayerInfoLivePacketId)
                SendVisualInvalidateForPlayerInfo(buffer);
        }

        private void SendVisualInvalidateForPlayerInfo(byte[] playerInfoPacketBuffer)
        {
            try
            {
                // Packet.Writer buffer layout for 809:
                // +0x00 packet id (WORD)
                // +0x02 count (DWORD)
                // +0x06 XiPlayerInfo[0]
                // XiPlayerInfo.Serial is after wchar Cname[13] => +26 bytes.
                const int serialOffset = 2 + 4 + 26;
                if (playerInfoPacketBuffer == null || playerInfoPacketBuffer.Length < serialOffset + 2)
                    return;

                var serial = BitConverter.ToUInt16(playerInfoPacketBuffer, serialOffset);
                if (serial == 0) return;

                var invalidate = new Packet((ushort)805);
                invalidate.Writer.Write((ushort)0); // packet +0x02
                invalidate.Writer.Write(serial);    // packet +0x04 target serial
                invalidate.Writer.Write((ushort)0); // packet +0x06
                invalidate.Writer.Write(0u);        // packet +0x08
                invalidate.Writer.Write(4u);        // packet +0x0C mode 4: one vehicle
                Send(invalidate);

                Log.Debug("PlayerInfo live render invalidate: Serial={0} -> 809+805 mode=4", serial);
            }
            catch (Exception ex)
            {
                Log.Warning("PlayerInfo live render invalidate failed: {0}", ex.Message);
            }
        }

        public void SendError(string format, params object[] args)
        {
            var err = new Packet(Packets.ErrorAck);
            err.Writer.WriteUnicode(string.Format(format, args));
            Send(err);
        }

        public void SendDebugError(string format, params object[] args)
        {
#if DEBUG
            SendError(format, args);
#endif
        }

        private void KillConnection(Exception ex)
        {
            if (ex is SocketException || ex is IOException)
            {
                KillConnection("Socket or IO Exception");
                return;
            }

            KillConnection(ex.Message + ": " + ex.StackTrace);
        }

        public void KillConnection(string reason = "")
        {
            if (!_connected) return;

            var departingUser = User;
            var departingSerial = departingUser == null ? (ushort)0 : departingUser.VehicleSerial;
            var departingName = departingUser?.ActiveCharacter?.Name ?? string.Empty;
            var localPort = LocalPort;

            _connected = false;
#if !DEBUG
            if (reason != "Socket or IO Exception")
#endif
            {
                Log.Info("Killing off client. {0}", reason);
            }

            if (departingSerial != 0 && (localPort == 11031 || localPort == 11041))
            {
                try
                {
                    var remove = new Packet(Packets.CmdRemoveVehicle);
                    remove.Writer.Write(departingSerial);
                    _parent.Broadcast(remove, this);
                    Log.Debug("Area disconnect remove: Name={0} Serial={1} Port={2} -> packet 550 broadcast",
                        departingName, departingSerial, localPort);
                }
                catch (Exception ex)
                {
                    Log.Warning("Area disconnect remove failed for Serial={0}: {1}", departingSerial, ex.Message);
                }
            }

            if (departingUser != null && departingSerial != 0)
            {
                try
                {
                    User active;
                    if (DefaultServer.ActiveSerials.TryGetValue(departingSerial, out active) &&
                        ReferenceEquals(active, departingUser))
                    {
                        DefaultServer.ActiveSerials.Remove(departingSerial);
                    }
                }
                catch
                {
                }
            }

            User = null;

            try { _tcp.Close(); }
            catch { }
        }

        public void SendChatMessage(string message)
        {
            var ack = new ChatMessageAnswer
            {
                MessageType = "channel",
                SenderCharacterName = "SERVER",
                Message = message
            };
            Send(ack.CreatePacket());
        }
    }
}
