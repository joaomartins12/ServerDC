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

            try
            {
                lock (_sendSync)
                {
                    if (!_connected) return;
                    _ns.Write(BitConverter.GetBytes(length), 0, 2);
                    _ns.Write(buffer, 0, bufferLength);
                }
            }
            catch (Exception ex)
            {
                KillConnection(ex);
                return;
            }

            // Retail free-roam keeps XiPlayerInfo (802/809) and the instantiated world
            // vehicle as separate pieces of state. Packet 809 refreshes the logical
            // XiPlayerInfo/XiVisualItem cache, but mesh-based cosmetics on an already
            // spawned remote car are not rebuilt merely because that cache changed.
            //
            // Cmd_RemoveVehicle (550) is the retail invalidation path for a remote world
            // vehicle. Its handler consumes exactly 60 bytes and, for a non-local serial,
            // only needs Serial(+0x02) and Age(+0x06) to invalidate the existing object.
            // Sending the correctly-sized 550 immediately after an 809 lets the next 541
            // materialize the remote car again from the newly installed player-info cache.
            TryInvalidateRemoteVehiclesAfterPlayerInfo(packet.Id, buffer);
        }

        private void TryInvalidateRemoteVehiclesAfterPlayerInfo(ushort packetId, byte[] buffer)
        {
            const ushort PlayerInfoLivePacketId = 809;
            const int PlayerInfoStride = 216;
            const int HeaderSize = 6; // id(u16) + count(u32)
            const int NameSize = 26;  // wchar Name[13]

            if (packetId != PlayerInfoLivePacketId || buffer == null || buffer.Length < HeaderSize)
                return;

            int count;
            try
            {
                count = BitConverter.ToInt32(buffer, 2);
            }
            catch
            {
                return;
            }

            if (count <= 0 || count > 64) return;

            for (var i = 0; i < count; i++)
            {
                var record = HeaderSize + (i * PlayerInfoStride);
                if (record < 0 || record + PlayerInfoStride > buffer.Length) break;

                var serial = BitConverter.ToUInt16(buffer, record + NameSize);
                var age = BitConverter.ToUInt16(buffer, record + NameSize + 2);
                if (serial == 0) continue;
                if (User != null && serial == User.VehicleSerial) continue;

                Send(BuildRetailRemoveVehicle(serial, age));
                Log.Debug(
                    "Remote visual rebuild invalidate: Viewer={0} TargetSerial={1} Age={2} -> 809+550; next 541 recreates world vehicle",
                    CharacterName, serial, age);
            }
        }

        /// <summary>
        /// Drift City v0.77a Cmd_RemoveVehicle (550) is exactly 60 bytes including the
        /// packet id. The remote handler reads Serial at +0x02 and Age at +0x06; the
        /// remaining retail structure is not consulted by the remote invalidation branch.
        /// Keep the complete wire size nevertheless, because the client handler returns
        /// 0x3C and short historical packets were malformed.
        /// </summary>
        public static Packet BuildRetailRemoveVehicle(ushort serial, ushort age = 0)
        {
            var remove = new Packet(Packets.CmdRemoveVehicle);
            remove.Writer.Write(serial);       // +0x02
            remove.Writer.Write((ushort)0);    // +0x04 opaque/reserved
            remove.Writer.Write(age);          // +0x06
            remove.Writer.Write(new byte[52]); // +0x08 .. +0x3B
            return remove;
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
                    var remove = BuildRetailRemoveVehicle(departingSerial);
                    _parent.Broadcast(remove, this);
                    Log.Debug("Area disconnect remove: Name={0} Serial={1} Port={2} -> retail packet 550/60-byte broadcast",
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