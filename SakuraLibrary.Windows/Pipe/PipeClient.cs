﻿using System;
using System.IO.Pipes;

using Google.Protobuf;

using SakuraLibrary.Proto;

namespace SakuraLibrary.Pipe
{
    public class PipeClient : IDisposable
    {
        public Action<PipeConnection, PushMessageBase> ServerPush;

        public PipeConnection Pipe = null, PushPipe = null;

        public bool Connected
        {
            get
            {
                lock (this)
                {
                    return Pipe != null && PushPipe != null && Pipe.Pipe.IsConnected && PushPipe.Pipe.IsConnected;
                }
            }
        }

        public readonly int BufferSize;
        public readonly string Name, Host;

        public PipeClient(string name, string host = ".", int bufferSize = 1048576)
        {
            Name = name;
            Host = host;
            BufferSize = bufferSize;
        }

        public bool Connect()
        {
            try
            {
                lock (this)
                {
                    if (Connected)
                    {
                        return true;
                    }
                    Dispose();

                    var pipe = new NamedPipeClientStream(Host, Name, PipeDirection.InOut, PipeOptions.Asynchronous);
                    pipe.Connect(1000);
                    pipe.ReadMode = PipeTransmissionMode.Message;
                    Pipe = new PipeConnection(new byte[BufferSize], pipe);

                    pipe = new NamedPipeClientStream(Host, Name + PipeConnection.PUSH_SUFFIX, PipeDirection.InOut, PipeOptions.Asynchronous);
                    pipe.Connect(1000);
                    pipe.ReadMode = PipeTransmissionMode.Message;
                    PushPipe = new PipeConnection(new byte[BufferSize], pipe);
                }
                BeginPushPipeRead();
                return true;
            }
            catch
            {
                Dispose();
            }
            return false;
        }

        public void Dispose()
        {
            lock (this)
            {
                Pipe?.Dispose();
                Pipe = null;

                PushPipe?.Dispose();
                PushPipe = null;
            }
        }

        public ResponseBase Request(MessageID id) => Request(new RequestBase()
        {
            Type = id
        });

        public ResponseBase Request(RequestBase message)
        {
            lock (this)
            {
                try
                {
                    var data = message.ToByteArray();
                    Pipe.Pipe.Write(data, 0, data.Length);
                    return ResponseBase.Parser.ParseFrom(Pipe.Buffer, 0, Pipe.EnsureMessageComplete(Pipe.Pipe.Read(Pipe.Buffer, 0, Pipe.Buffer.Length)));
                }
                catch
                {
                    if (!Pipe.Pipe.IsConnected)
                    {
                        Dispose();
                    }
                }
                return new ResponseBase()
                {
                    Success = false,
                    Message = "未连接到守护进程"
                };
            }
        }

        protected void OnPushPipeData(IAsyncResult ar)
        {
            lock (this)
            {
                var pipe = ar.AsyncState as PipeStream;
                var count = pipe.EndRead(ar);

                if (!pipe.IsConnected || PushPipe == null)
                {
                    Dispose();
                    return;
                }
                try
                {
                    ServerPush?.Invoke(PushPipe, PushMessageBase.Parser.ParseFrom(PushPipe.Buffer, 0, PushPipe.EnsureMessageComplete(count)));
                }
                catch { }
            }
            BeginPushPipeRead();
        }

        protected void BeginPushPipeRead()
        {
            lock (this)
            {
                if (PushPipe == null)
                {
                    return;
                }
                PushPipe.Pipe.BeginRead(PushPipe.Buffer, 0, PushPipe.Buffer.Length, OnPushPipeData, PushPipe.Pipe);
            }
        }
    }
}
