﻿using Sockety.Model;
using MessagePack;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sockety.Service;

namespace Sockety.Client
{
    internal class ClientReceiver<T>: IDisposable where T: IService
    {
        private Socket serverSocket = null;
        private Socket serverUdpSocket;
        private IPEndPoint serverUdpPort;

        /// <summary>
        /// TCPの受信用スレッド
        /// </summary>
        private Thread TcpReceiveThread;
        /// <summary>
        /// UDPの受信用スレッド
        /// </summary>
        private Thread UdpReceiveThread;

        private byte[] CommunicateButter = new byte[SocketySetting.MAX_BUFFER];
        /// <summary>
        /// 通信が切断時に発火
        /// </summary>
        internal Action ConnectionReset;

        /// <summary>
        /// クライアントが読んでいるサーバのメソッド名
        /// </summary>
        private string ServerCallMethodName = "";
        /// <summary>
        /// サーバからのレスポンスデータ
        /// </summary>
        private object ServerResponse;

        private ClientInfo ClientInfo;
        /// <summary>
        /// サーバメソッド呼び出しの待ちイベント
        /// </summary>
        private ManualResetEvent RecieveSyncEvent = new ManualResetEvent(false);

        private T Parent;
        PacketSerivce<T> PacketSerivce;

        #region IDisposable
        public void Dispose()
        {
            AbortReceiveProcess();
        }
        #endregion


        internal void AbortReceiveProcess()
        {
            if (TcpReceiveThread != null)
            {
                TcpReceiveThread.Abort();
                TcpReceiveThread = null;
            }

        }


        internal void Run(Socket handler,Socket UdpSocket,IPEndPoint UdpEndPort,ClientInfo clientInfo, T parent)
        {
            Parent = parent;
            serverSocket = handler;
            serverUdpSocket = UdpSocket;
            serverUdpPort = UdpEndPort;
            ClientInfo = clientInfo;

            PacketSerivce = new PacketSerivce<T>();
            PacketSerivce.SetUp(parent);

            TcpReceiveThread = new Thread(new ThreadStart(ReceiveProcess));
            TcpReceiveThread.Start();

            UdpReceiveThread = new Thread(new ThreadStart(UdpReceiveProcess));
            UdpReceiveThread.Start();
        }

        /// <summary>
        /// UDP送信
        /// </summary>
        /// <param name="data"></param>
        internal void UdpSend(SocketyPacketUDP packet)
        {
            if (serverUdpSocket == null)
            {
                return;
            }

            var bytes = MessagePackSerializer.Serialize(packet);

            serverUdpSocket.SendTo(bytes, SocketFlags.None, serverUdpPort);
        }

        /// <summary>
        /// サーバメソッド呼び出し（サーバのレスポンスを待つ）
        /// </summary>
        /// <param name="serverMethodName"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        internal object Send(string serverMethodName,object data)
        {
            if (serverSocket == null || serverSocket.Connected == false)
            {
                return null;
            }

            lock (ServerCallMethodName)
            {
                ServerCallMethodName = serverMethodName;
            }
            SocketyPacket packet = new SocketyPacket {MethodName = serverMethodName,clientInfo = ClientInfo, PackData = data };
            RecieveSyncEvent.Reset();
            serverSocket.Send(MessagePackSerializer.Serialize(packet));
            RecieveSyncEvent.WaitOne();

            return ServerResponse;
        }

        /// <summary>
        /// UDP受信用スレッド
        /// </summary>
        private void UdpReceiveProcess()
        {
            var CommunicateBuffer = new byte[SocketySetting.MAX_BUFFER];

            while(true)
            {
                try
                {
                    serverUdpSocket.Receive(CommunicateBuffer);
                    var packet = MessagePackSerializer.Deserialize<SocketyPacketUDP>(CommunicateBuffer);

                    Task.Run(() => PacketSerivce.ReceiverSocketyPacketUDP(packet));
                }
                catch(Exception ex)
                {
                    throw ex;
                }
            }
        }

        /// <summary>
        /// TCP受信用スレッド
        /// </summary>
        private void ReceiveProcess()
        {
            while (true)
            {
                try
                {
                    int bytesRec = serverSocket.Receive(CommunicateButter);
                    var packet = MessagePackSerializer.Deserialize<SocketyPacket>(CommunicateButter);
                    lock (RecieveSyncEvent)
                    {
                        if (string.IsNullOrEmpty(ServerCallMethodName) != true && ServerCallMethodName == packet.MethodName)
                        {
                            ///サーバのレスポンスを待つタイプの場合は待ちイベントをセットする
                            ServerResponse = packet.PackData;
                            RecieveSyncEvent.Set();
                        }
                        else
                        {
                            ///非同期なので、クライアントメソッドを呼ぶ
                            InvokeMethod(packet);
                        }
                    }
                }catch(SocketException ex)
                { 
                    //通信切断処理
                    if (ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        //通信切断
                        Task.Run(() => ConnectionReset?.Invoke());

                        //受信スレッド終了
                        return;
                    }
                }catch(Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// SocketyPacket.MethodNameのメソッド呼び出し
        /// </summary>
        /// <param name="packet"></param>
        private void InvokeMethod(SocketyPacket packet)
        {
            Type t = Parent.GetType();
            var method = t.GetMethod(packet.MethodName);

            if (method == null)
            {
                throw new Exception("not found Method");
            }
            if (packet.PackData != null)
            {
                Task.Run(() => method.Invoke(Parent, new object[] { packet.PackData }));
            }
            else
            {
                Task.Run(() => method.Invoke(Parent, null));
            }
        }
    }
}
