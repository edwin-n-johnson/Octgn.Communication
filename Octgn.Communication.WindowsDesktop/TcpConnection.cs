﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Octgn.Communication
{
    public class TcpConnection : ConnectionBase
    {
#pragma warning disable IDE1006 // Naming Styles
        private static readonly ILogger Log = LoggerFactory.Create(nameof(TcpConnection));
#pragma warning restore IDE1006 // Naming Styles

        internal TcpClient TcpClient => _client;

        private readonly TcpClient _client;
        /// <summary>
        /// You must close the NetworkStream when you are through sending and receiving data. Closing TcpClient does not release the NetworkStream. -- https://docs.microsoft.com/en-us/dotnet/api/system.net.sockets.tcpclient.getstream?f1url=https%3A%2F%2Fmsdn.microsoft.com%2Fquery%2Fdev15.query%3FappId%3DDev15IDEF1%26l%3DEN-US%26k%3Dk(System.Net.Sockets.TcpClient.GetStream);k(TargetFrameworkMoniker-.NETFramework,Version%3Dv4.7);k(DevLang-csharp)%26rd%3Dtrue&view=netframework-4.7.1
        /// </summary>
        private NetworkStream _clientStream;

        internal bool IsListenerConnection { get; }

        /// <summary>
        /// Creates a <see cref="TcpConnection"/> from and already open <see cref="TcpClient"/>
        /// </summary>
        /// <param name="client"></param>
        /// <param name="serializer"></param>
        /// <param name="handshaker"></param>
        public TcpConnection(TcpClient client, ISerializer serializer, IHandshaker handshaker, Server server)
            : base(client.Client.RemoteEndPoint.ToString(), handshaker, serializer, server) {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _clientStream = _client.GetStream() ?? throw new ArgumentNullException(nameof(_client) + "." + nameof(_client.GetStream));

            IsListenerConnection = true;

            TransitionState(ConnectionState.Connecting);
        }

        public TcpConnection(string remoteHost, ISerializer serializer, IHandshaker handshaker, Client client)
            : base(remoteHost, handshaker, serializer, client) {
            _client = new TcpClient();
        }

        protected TcpConnection(TcpConnection connection, Server server)
            : base(connection.RemoteAddress, connection.Handshaker, connection.Serializer, server) {
            _client = new TcpClient();
        }

        protected TcpConnection(TcpConnection connection, Client client)
            : base(connection.RemoteAddress, connection.Handshaker, connection.Serializer, client) {
            _client = new TcpClient();
        }

        protected override void OnConnectionStateChanged(object sender, ConnectionStateChangedEventArgs args) {
            switch (args.NewState) {
                case ConnectionState.Handshaking:
                    ReadPackets().SignalOnException();
                    break;
                case ConnectionState.Closed:
                    try {
                        Log.Info($"{this}: Closing tcp client");
                        _clientStream?.Close();
                        _client.Close();
                    } catch (Exception ex) {
                        Log.Warn($"{this}: {nameof(OnConnectionStateChanged)}", ex);
                    }
                    break;
            }

            base.OnConnectionStateChanged(sender, args);
        }

        protected override async Task ConnectImpl(CancellationToken cancellationToken = default(CancellationToken)) {
            if (IsListenerConnection) throw new InvalidOperationException($"{this}: Cannot call {nameof(Connect)}");

            Log.Info($"{this}: Starting to {nameof(Connect)} to {RemoteAddress}...");

            var hostParts = RemoteAddress.Split(':');
            if (hostParts.Length != 2)
                throw new FormatException($"{this}: {nameof(RemoteAddress)} is in the wrong format '{RemoteAddress}.' Should be in the format 'hostname:port' for example 'localhost:4356' or 'jumbo.fried.jims.aquarium:4453'");
            var host = hostParts[0];
            var port = int.Parse(hostParts[1]);

            Log.Info($"{this}: Resolving IPAddresses for {host}...");
            IPAddress[] addresses = null;
            cancellationToken.ThrowIfCancellationRequested();
            await ResilliantTask.Run(async () => addresses = await Dns.GetHostAddressesAsync(host), cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            addresses = addresses ?? new IPAddress[0];

            Log.Info($"{this}: Found {addresses.Length.ToString()}: {string.Join(", ", (object[])addresses)}");

            if (addresses.Length == 0) throw new InvalidOperationException($"Unable to find any IP address for host {host} :(");

            foreach (var address in addresses) {
                try {
                    Log.Info($"{this}: Trying to connect to {address}:{port}...");

                    cancellationToken.ThrowIfCancellationRequested();

                    await _client.ConnectAsync(address, port);

                    _clientStream = _client.GetStream() ?? throw new InvalidOperationException("Null stream " + nameof(_client) + "." + nameof(_client.GetStream));

                    Log.Info($"{this}: Connected to {address}:{port}...");

                    return;
                } catch (Exception ex) {
                    Log.Warn($"{this}: Error connecting to {RemoteAddress} using the address {address}", ex);
                }
            }

            throw new CouldNotConnectException($"{this}: Unable to connect to host {host}. Check logs for previous errors.");
        }

        private readonly object _sendLock = new object();

        protected override Task SendImpl(ulong packetId, byte[] data, CancellationToken cancellationToken) {
            try {
                if (data == null) throw new ArgumentNullException(nameof(data));

                using (var combinedCancellations = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ClosedCancellationToken)) {
                    var packetBytes = BitConverter.GetBytes(packetId);

                    var size = BitConverter.GetBytes(data.Length);

                    // Don't let two sends happen at the same time.
                    lock (_sendLock) {
                        _client.Client.Send(packetBytes);
                        _client.Client.Send(size);
                        _client.Client.Send(data);
                    }
                }

                return Task.CompletedTask;
            } catch (DisconnectedException) {
                throw;
            } catch (ObjectDisposedException) {
                throw new DisconnectedException(this.ToString());
            } catch (Exception ex) {
                throw new DisconnectedException(this.ToString(), ex);
            }
        }

        private async Task ReadPackets() {
            async Task<byte[]> ReadChunk(int size) {
                var buffer = new byte[size];
                var bufferLength = 0;

                while (true) {
                    ClosedCancellationToken.ThrowIfCancellationRequested();

                    var count = await _clientStream.ReadAsync(buffer, bufferLength, size - bufferLength, ClosedCancellationToken);

                    if (count == 0) throw new DisconnectedException();

                    bufferLength += count;

                    if (bufferLength == size) {
                        return buffer;
                    }
                }
            }

            Log.Info($"{this}: {nameof(ReadPackets)}");

            try {
                while (!ClosedCancellationToken.IsCancellationRequested) {
                    var idChunk = await ReadChunk(8);

                    var packetId = BitConverter.ToUInt64(idChunk, 0);

                    var lengthChunk = await ReadChunk(4);

                    var dataLength = BitConverter.ToInt32(lengthChunk, 0);

                    if (dataLength <= 0 || dataLength > 5000000) throw new InvalidDataLengthException($"Invalid data length {dataLength}");

                    var packetBuffer = await ReadChunk(dataLength);

                    ProcessReceivedData(packetId, packetBuffer, Serializer).SignalOnException();
                }
            } catch (ObjectDisposedException) {
                Log.Warn($"{this}: Disconnected");
            } catch (InvalidDataLengthException ex) {
                Log.Warn($"{this}: Invalid Data Length: {ex.Message}");
            } catch (IOException ex) {
                Log.Warn($"{this}: Disconnected", ex);
            } catch (TaskCanceledException) {
                Log.Warn($"{this}: Disconnected. Task canceled.");
            } catch (DisconnectedException ex) when (ex.InnerException != null) {
                Log.Warn($"{this}: Disconnected", ex.InnerException);
            } catch (DisconnectedException) {
                Log.Warn($"{this}: Disconnected");
            } finally {
                Log.Info($"{this}: {nameof(ReadPackets)}: Done reading packets");
            }

            TransitionState(ConnectionState.Closed);
        }

        public override IConnection Clone() {
            if (Server != null)
                return new TcpConnection(this, Server);
            return new TcpConnection(this, Client);
        }

        protected override void Dispose(bool disposing) {
            if (!disposing) return;

            _clientStream?.Dispose();
            _client.Dispose();
            base.Dispose(disposing);
        }
    }
}
