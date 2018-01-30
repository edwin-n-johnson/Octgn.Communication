﻿using Octgn.Communication.Packets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Octgn.Communication
{
    public abstract class Client : IDisposable
    {
#pragma warning disable IDE1006 // Naming Styles
        private static readonly ILogger Log = LoggerFactory.Create(typeof(Client));
#pragma warning restore IDE1006 // Naming Styles

        public User User { get; set; }
        public IConnection Connection { get; private set; }

        public bool IsConnected { get; private set; }

        public event EventHandler<ConnectedEventArgs> Connected;
        protected void FireConnectedEvent()
        {
            try {
                Connected?.Invoke(this, new ConnectedEventArgs { Client = this });
            } catch (Exception ex) {
                Signal.Exception(ex, nameof(FireConnectedEvent));
            }
        }

        public event EventHandler<DisconnectedEventArgs> Disconnected;
        protected void FireDisconnectedEvent()
        {
            try {
                Disconnected?.Invoke(this, new DisconnectedEventArgs { Client = this });
            } catch (Exception ex) {
                Signal.Exception(ex, nameof(FireDisconnectedEvent));
            }
        }

        public ISerializer Serializer { get; }

        public IAuthenticator Authenticator { get; }

        protected abstract IConnection CreateConnection();

        public Client(ISerializer serializer, IAuthenticator authenticator) {
            Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            Authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        }

        public Task Connect(CancellationToken cancellationToken = default(CancellationToken)) {
            if (Connection != null) throw new InvalidOperationException($"{this}: Cannot call Connect more than once.");

            return ConnectInternal(cancellationToken);
        }

        private bool _authenticating = false;
        private async Task ConnectInternal(CancellationToken cancellationToken = default(CancellationToken)) {
            Log.Info($"{this}: Connecting...");

            try {
                Connection = CreateConnection();
                Connection.Serializer = Serializer;

                await Connection.Connect(cancellationToken);
                Connection.ConnectionClosed += Connection_ConnectionClosed;
                Connection.RequestReceived += Connection_RequestReceived;

                AuthenticationResult result = null;

                try {
                    _authenticating = true;
                    Log.Info($"{this}: Authenticating...");

                    result = await Authenticator.Authenticate(this, Connection, cancellationToken);

                    if (!result.Successful) {
                        var ex = new AuthenticationException(result.ErrorCode);
                        Log.Info($"{this}: Authentication Failed {result.ErrorCode}: {ex.Message}");
                        throw ex;
                    }
                } finally {
                    _authenticating = false;
                }

                // Should do this before an operation that might block
                cancellationToken.ThrowIfCancellationRequested();

                IsConnected = true;
                this.User = result.User;

                Log.Info($"{this}: Firing connected events...");
                FireConnectedEvent();
                Log.Info($"{this}: Connected");
            } catch {
                if (Connection != null) {
                    Connection.ConnectionClosed -= Connection_ConnectionClosed;
                    Connection.RequestReceived -= Connection_RequestReceived;
                    Connection = null;
                }
                throw;
            }
        }

        private async void Connection_ConnectionClosed(object sender, ConnectionClosedEventArgs args) {
            try
            {
                IsConnected = false;
                args.Connection.ConnectionClosed -= Connection_ConnectionClosed;
                args.Connection.RequestReceived -= Connection_RequestReceived;

                Log.Warn($"{this}: Disconnected", args.Exception);

                FireDisconnectedEvent();
                await ReconnectAsync();
            } catch (Exception ex) {
                Signal.Exception(ex, nameof(Connection_ConnectionClosed));
            }
        }

        public const int ReconnectRetryCount = 10;
        public static TimeSpan DefaultReconnectRetryDelay = TimeSpan.FromSeconds(5);
        public TimeSpan ReconnectRetryDelay { get; set; } = DefaultReconnectRetryDelay;

        private async Task ReconnectAsync() {
            var currentTry = 0;
            const int maxRetryCount = ReconnectRetryCount;
            try {
                Log.Info($"{this}: Reconnecting...");

                for(currentTry = 0; currentTry < maxRetryCount; currentTry++)
                {
                    if (_disposed) {
                        Log.Warn($"{this}: {nameof(ReconnectAsync)}: Disposed, stopping reconnect attempt.");
                        break;
                    }

                    Log.Info($"{this}: {nameof(ReconnectAsync)}: Reconnecting...{currentTry}/{maxRetryCount}");

                    try {
                        await Task.Delay(ReconnectRetryDelay);
                        await ConnectInternal();
                    } catch (Exception ex) {
                        Log.Warn($"{this}: {nameof(ReconnectAsync)}: Error When Reconnecting...Going to try again...", ex);
                        continue;
                    }

                    Log.Info($"{this}: {nameof(ReconnectAsync)}: Reconnected after {currentTry} out of {maxRetryCount} tries");
                    return;
                }
            } finally {
                if(!IsConnected)
                    Log.Warn($"{this}: {nameof(ReconnectAsync)}: Failed to reconnect after {currentTry} out of {maxRetryCount} tries");
            }
        }

        private readonly Dictionary<Type, IClientModule> _clientModules = new Dictionary<Type, IClientModule>();
        public void Attach(IClientModule module) {
            _clientModules.Add(module.GetType(), module);
        }

        public T GetModule<T>() where T : IClientModule{
            return (T)_clientModules[typeof(T)];
        }

        private bool _disposed;
        public void Dispose() {
            _disposed = true;
            Log.Info($"{this}: Disposed");
            foreach(var moduleKVP in _clientModules) {
                var module = moduleKVP.Value;

                (module as IDisposable)?.Dispose();
            }
            _clientModules.Clear();
            if (Connection != null) {
                Connection.IsClosed = true;
            }
        }

        public Task<ResponsePacket> Request(RequestPacket request, CancellationToken cancellationToken = default(CancellationToken)) {
            if (!IsConnected && !_authenticating) throw new NotConnectedException($"{this}: Could not send the request {request}, the client is not connected.");
            return Connection.Request(request, cancellationToken);
        }

#pragma warning disable RCS1159 // Use EventHandler<T>.
        // This delegate returns a Task, and we need that for our implementation
        public event RequestReceived RequestReceived;
#pragma warning restore RCS1159 // Use EventHandler<T>.

        private async Task Connection_RequestReceived(object sender, RequestReceivedEventArgs args) {
            if (sender == null) {
                throw new ArgumentNullException(nameof(sender));
            }

            try {
                args.Context.Client = this;
                args.Context.User = this.User;

                foreach (var handler in _clientModules.Values) {
                    await handler.HandleRequest(this, args);
                    if (args.IsHandled)
                        break;
                }

                // Copy locally in case it become null
                var eventHandler = RequestReceived;
                if(!args.IsHandled && eventHandler != null) {
                    await eventHandler.Invoke(this, args);
                }
            } catch (Exception ex) {
                Signal.Exception(ex);
            }
        }

        public override string ToString() {
            return $"{nameof(Client)} {this.User}: {Connection}";
        }
    }

    public class ConnectedEventArgs : EventArgs
    {
        public Client Client { get; set; }
    }

    public class DisconnectedEventArgs : EventArgs
    {
        public Client Client { get; set; }
    }
}
