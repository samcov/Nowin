using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace NowinWebServer
{
    public class SaeaLayerCallback : ITransportLayerCallback, IDisposable
    {
        [Flags]
        enum State
        {
            Receive = 1,
            Send = 2,
            Disconnect = 4,
            Aborting = 8
        }
        readonly ITransportLayerHandler _handler;
        readonly Socket _listenSocket;
        readonly Server _server;
        readonly SocketAsyncEventArgs _receiveEvent = new SocketAsyncEventArgs();
        readonly SocketAsyncEventArgs _sendEvent = new SocketAsyncEventArgs();
        readonly SocketAsyncEventArgs _disconnectEvent = new SocketAsyncEventArgs();
        Socket _socket;
#pragma warning disable 420
        volatile int _state;

        public SaeaLayerCallback(ITransportLayerHandler handler, byte[] buffer, Socket listenSocket, Server server)
        {
            _handler = handler;
            _listenSocket = listenSocket;
            _server = server;
            _receiveEvent.Completed += IoCompleted;
            _sendEvent.Completed += IoCompleted;
            _disconnectEvent.Completed += IoCompleted;
            _receiveEvent.SetBuffer(buffer, 0, 0);
            _sendEvent.SetBuffer(buffer, 0, 0);
            _receiveEvent.DisconnectReuseSocket = true;
            _sendEvent.DisconnectReuseSocket = true;
            _disconnectEvent.DisconnectReuseSocket = true;
            _receiveEvent.UserToken = this;
            _sendEvent.UserToken = this;
            _disconnectEvent.UserToken = this;
            handler.Callback = this;
        }

        static void IoCompleted(object sender, SocketAsyncEventArgs e)
        {
            var self = (SaeaLayerCallback)e.UserToken;
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Accept:
                    Debug.Assert(e == self._receiveEvent);
                    if (e.SocketError == SocketError.OperationAborted) return;
                    self.ProcessAccept();
                    break;
                case SocketAsyncOperation.Receive:
                    Debug.Assert(e == self._receiveEvent);
                    self.ProcessReceive();
                    break;
                case SocketAsyncOperation.Send:
                    Debug.Assert(e == self._sendEvent);
                    self.ProcessSend();
                    break;
                case SocketAsyncOperation.Disconnect:
                    Debug.Assert(e == self._disconnectEvent);
                    self.ProcessDisconnect();
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not expected");
            }
        }

        void ProcessAccept()
        {
            int oldState, newState;
            do
            {
                oldState = _state;
                newState = oldState & ~(int)State.Receive;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            _server.ReportNewConnectedClient();
            _socket = _receiveEvent.AcceptSocket;
            _receiveEvent.AcceptSocket = null;
            if (_receiveEvent.BytesTransferred > 0 && _receiveEvent.SocketError == SocketError.Success)
            {
                _handler.FinishAccept(_receiveEvent.Offset, _receiveEvent.BytesTransferred);
            }
        }

        void ProcessReceive()
        {
            if (_receiveEvent.BytesTransferred > 0 && _receiveEvent.SocketError == SocketError.Success)
            {
                int oldState, newState;
                do
                {
                    oldState = _state;
                    newState = oldState & ~(int)State.Receive;
                } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
                _handler.FinishReceive(_receiveEvent.Offset, _receiveEvent.BytesTransferred);
            }
            else
            {
                int oldState, newState;
                do
                {
                    oldState = _state;
                    newState = (oldState & ~(int)State.Receive) | (int)State.Aborting;
                } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
                _handler.FinishReceiveWithAbort();
            }
        }

        void ProcessSend()
        {
            int oldState, newState;
            do
            {
                oldState = _state;
                newState = oldState & ~(int)State.Send;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            Exception ex = null;
            if (_sendEvent.SocketError != SocketError.Success)
            {
                ex = new IOException();
            }
            _handler.FinishSend(ex);
        }

        void ProcessDisconnect()
        {
            int oldState, newState;
            do
            {
                oldState = _state;
                newState = (oldState & ~(int)(State.Disconnect|State.Aborting));
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            _socket = null;
            _server.ReportDisconnectedClient();
            _handler.PrepareAccept();
        }

        public void StartAccept(int offset, int length)
        {
            int oldState, newState;
            do
            {
                oldState = _state;
                if ((oldState & (int)State.Receive) != 0)
                    throw new InvalidOperationException("Already receiving or accepting");
                newState = oldState | (int)State.Receive;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            _receiveEvent.SetBuffer(offset, length);
            bool willRaiseEvent;
            try
            {
                willRaiseEvent = _listenSocket.AcceptAsync(_receiveEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                ProcessAccept();
            }
        }

        public void StartReceive(int offset, int length)
        {
            int oldState, newState;
            do
            {
                oldState = _state;
                if ((oldState & (int)State.Receive) != 0)
                    throw new InvalidOperationException("Already receiving or accepting");
                newState = oldState | (int)State.Receive;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            _receiveEvent.SetBuffer(offset, length);
            bool willRaiseEvent;
            try
            {
                willRaiseEvent = _socket.ReceiveAsync(_receiveEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                ProcessReceive();
            }
        }

        public void StartSend(int offset, int length)
        {
            int oldState, newState;
            do
            {
                oldState = _state;
                if ((oldState & (int)State.Send) != 0)
                    throw new InvalidOperationException("Already sending");
                newState = oldState | (int)State.Send;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            _sendEvent.SetBuffer(offset, length);
            bool willRaiseEvent;
            try
            {
                willRaiseEvent = _socket.SendAsync(_sendEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                ProcessSend();
            }
        }

        public void StartDisconnect()
        {
            int oldState, newState;
            do
            {
                oldState = _state;
                if ((oldState & (int)State.Disconnect) != 0)
                    throw new InvalidOperationException("Already disconnecting");
                newState = oldState | (int)State.Disconnect;
            } while (Interlocked.CompareExchange(ref _state, newState, oldState) != oldState);
            bool willRaiseEvent;
            try
            {
                willRaiseEvent = _socket.DisconnectAsync(_disconnectEvent);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (!willRaiseEvent)
            {
                ProcessDisconnect();
            }
        }

        public void Dispose()
        {
            var s = _socket;
            if (s != null)
            {
                s.Dispose();
            }
        }
    }
}