﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;

namespace Naive.HttpSvr
{
    public interface IMsgStream
    {
        MsgStreamStatus State { get; }
        Task SendMsg(Msg msg);
        Task<Msg> RecvMsg(BytesView buf);
        Task Close(CloseOpt closeOpt);
    }

    public interface IMsgStreamStringSupport
    {
        Task SendString(string str);
        Task<string> RecvString();
    }

    public struct Msg
    {
        public bool IsEOF => Data == null;
        public BytesView Data { get; set; }

        public Msg(BytesView data)
        {
            this.Data = data;
        }

        public static Msg EOF => new Msg() { Data = null };

        public static implicit operator Msg(BytesView bv) => new Msg(bv);

        public static implicit operator Msg(byte[] bytes) => new Msg(bytes);

        public string GetString()
        {
            var msg = this;
            if (msg.Data.nextNode == null) {
                return NaiveUtils.UTF8Encoding.GetString(msg.Data.bytes, msg.Data.offset, msg.Data.len);
            } else {
                return NaiveUtils.UTF8Encoding.GetString(msg.Data.GetBytes());
            }
        }
    }

    public struct CloseOpt
    {
        public CloseType CloseType;
        public SocketShutdown ShutdownType;

        public CloseOpt(CloseType closeType, SocketShutdown shutdownType = SocketShutdown.Send)
        {
            CloseType = closeType;
            ShutdownType = shutdownType;
        }

        public static CloseOpt Close => new CloseOpt(CloseType.Close);
    }

    public enum CloseType
    {
        Close = 0,
        Shutdown = 1
    }

    public static class MsgStreamExt
    {
        public static Task SendString(this IMsgStream ms, string str)
        {
            if (ms is IMsgStreamStringSupport isss)
                return isss.SendString(str);
            return ms.SendMsg(NaiveUtils.UTF8Encoding.GetBytes(str));
        }

        public static async Task<string> RecvString(this IMsgStream ms)
        {
            if (ms is IMsgStreamStringSupport isss)
                return await isss.RecvString().CAF();
            var msg = await ms.RecvMsg(null).CAF();
            if (msg.IsEOF)
                return null;
            return msg.GetString();
        }

        public static async Task StartReadLoop(this IMsgStream msgStream, Action<Msg> onRecv, Action onClose = null)
        {
            while (true) {
                var msg = await msgStream.RecvMsg(null).CAF();
                if (msg.IsEOF && onClose != null) {
                    onClose();
                    break;
                }
                onRecv(msg);
                if (msg.IsEOF)
                    break;
            }
        }

        public static async Task StartReadLoop(this IMsgStream msgStream, Func<Msg, Task> onRecv, Action onClose = null)
        {
            while (true) {
                var msg = await msgStream.RecvMsg(null).CAF();
                if (msg.IsEOF && onClose != null) {
                    onClose();
                    break;
                }
                await onRecv(msg);
                if (msg.IsEOF)
                    break;
            }
        }

        public static async Task<Msg> ThrowIfEOF(this Task<Msg> task)
        {
            var msg = await task.CAF();
            if (msg.IsEOF)
                throw new DisconnectedException("EOF Msg");
            return msg;
        }
    }

    public enum MsgStreamStatus
    {
        Open = 0,
        Shutdown = 1 << 0,
        RemoteShutdown = 1 << 1,
        Close = Shutdown | RemoteShutdown
    }
}
