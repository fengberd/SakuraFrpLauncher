﻿using System;
using System.IO.Pipes;
using System.Security.Principal;
using System.Collections.Generic;
using System.Security.AccessControl;

using Google.Protobuf;

using SakuraLibrary.Proto;

namespace SakuraLibrary.Pipe
{
    public class PipeServer : IDisposable
    {
        public event PipeConnection.PipeConnectionEventHandler Connected;
        public event PipeConnection.PipeConnectionEventHandler Disconnected;

        public event PipeConnection.PipeDataEventHandler DataReceived;

        public NamedPipeServerStream ListeningPipe = null, ListeningPushPipe = null;

        public List<PipeStream> PushPipes = new List<PipeStream>();
        public Dictionary<PipeStream, PipeConnection> Pipes = new Dictionary<PipeStream, PipeConnection>();

        public readonly string Name;
        public readonly int BufferSize;

        public bool Running { get; protected set; }

        public PipeServer(string name, int bufferSize = 1048576)
        {
            Name = name;
            BufferSize = bufferSize;
        }

        public void Start()
        {
            if (Running)
            {
                return;
            }
            Running = true;

            BeginPipeListen();
            BeginPushPipeListen();
        }

        public void Stop()
        {
            if (!Running)
            {
                return;
            }
            Running = false;

            lock (Pipes)
            {
                lock (PushPipes)
                {
                    Dispose();
                }
            }
        }

        public void Dispose()
        {
            ListeningPipe?.Dispose();
            ListeningPipe = null;
            ListeningPushPipe?.Dispose();
            ListeningPushPipe = null;

            foreach (var p in Pipes.Values)
            {
                p.Dispose();
            }
            Pipes.Clear();

            foreach (var p in PushPipes)
            {
                p.Dispose();
            }
            PushPipes.Clear();
        }

        public PipeSecurity CreateSecurity()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            return security;
        }

        #region RESTful API Pipe

        protected void OnPipeConnect(IAsyncResult ar)
        {
            try
            {
                PipeConnection conn;
                lock (Pipes)
                {
                    ListeningPipe.EndWaitForConnection(ar);
                    conn = new PipeConnection(new byte[BufferSize], ListeningPipe);
                    ListeningPipe = null;

                    Pipes.Add(conn.Pipe, conn);
                }

                BeginPipeRead(conn);
                Connected?.Invoke(conn);
            }
            catch { }

            BeginPipeListen();
        }

        protected void OnPipeData(IAsyncResult ar)
        {
            var conn = ar.AsyncState as PipeConnection;
            try
            {
                DataReceived?.Invoke(conn, conn.EnsureMessageComplete(conn.Pipe.EndRead(ar)));
            }
            catch
            {
                if (!conn.Pipe.IsConnected)
                {
                    lock (Pipes)
                    {
                        Pipes.Remove(conn.Pipe);
                    }
                    Disconnected?.Invoke(conn);
                    conn.Dispose();
                    return;
                }
            }
            BeginPipeRead(conn);
        }

        protected void BeginPipeListen()
        {
            if (!Running)
            {
                return;
            }
            lock (Pipes)
            {
                if (ListeningPipe != null)
                {
                    ListeningPipe.Dispose(); // Shouldn't happen
                }
                ListeningPipe = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous, BufferSize, BufferSize, CreateSecurity())
                {
                    ReadMode = PipeTransmissionMode.Message
                };
                ListeningPipe.BeginWaitForConnection(OnPipeConnect, null);
            }
        }

        protected void BeginPipeRead(PipeConnection conn) => conn.Pipe.BeginRead(conn.Buffer, 0, conn.Buffer.Length, OnPipeData, conn);

        #endregion

        #region Message Push Pipe

        public void PushMessage(PushMessageBase msg)
        {
            var buffer = msg.ToByteArray();
            var remove = new List<PipeStream>();
            lock (PushPipes)
            {
                foreach (var p in PushPipes)
                {
                    try
                    {
                        p.Write(buffer, 0, buffer.Length);
                    }
                    catch
                    {
                        if (!p.IsConnected)
                        {
                            remove.Add(p);
                        }
                    }
                }
                foreach (var r in remove)
                {
                    PushPipes.Remove(r);
                }
            }
        }

        protected void OnPushPipeConnect(IAsyncResult ar)
        {
            try
            {
                lock (PushPipes)
                {
                    ListeningPushPipe.EndWaitForConnection(ar);
                    PushPipes.Add(ListeningPushPipe);
                    ListeningPushPipe = null;
                }
            }
            catch { }

            BeginPushPipeListen();
        }

        protected void BeginPushPipeListen()
        {
            if (!Running)
            {
                return;
            }
            lock (PushPipes)
            {
                if (ListeningPushPipe != null)
                {
                    ListeningPushPipe.Dispose(); // Shouldn't happen too
                }
                // Note: PipeDirection.InOut ensures the client can set ReadMode, IDK why but it works this way
                ListeningPushPipe = new NamedPipeServerStream(Name + PipeConnection.PUSH_SUFFIX, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous, 0, BufferSize, CreateSecurity());
                ListeningPushPipe.BeginWaitForConnection(OnPushPipeConnect, null);
            }
        }

        #endregion
    }
}
