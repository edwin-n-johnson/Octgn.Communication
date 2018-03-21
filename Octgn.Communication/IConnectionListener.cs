﻿using System;

namespace Octgn.Communication
{
    public interface IConnectionListener
    {
        ISerializer Serializer { get; }
        bool IsEnabled { get; set; }
        event ConnectionCreated ConnectionCreated;
    }

    public delegate void ConnectionCreated(object sender, ConnectionCreatedEventArgs args);

    public class ConnectionCreatedEventArgs : EventArgs
    {
        public IConnection Connection { get; set; }
    }
}
