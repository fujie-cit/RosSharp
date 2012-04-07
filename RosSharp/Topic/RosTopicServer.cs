﻿#region License Terms

// ================================================================================
// RosSharp
// 
// Software License Agreement (BSD License)
// 
// Copyright (C) 2012 zoetrope
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
//     * Redistributions of source code must retain the above copyright
//       notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright
//       notice, this list of conditions and the following disclaimer in the
//       documentation and/or other materials provided with the distribution.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
// FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// ================================================================================

#endregion

using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Common.Logging;
using RosSharp.Message;
using RosSharp.Slave;
using RosSharp.Transport;

namespace RosSharp.Topic
{
    internal sealed class RosTopicServer<TMessage> : IDisposable
        where TMessage : IMessage, new()
    {
        private TcpRosClient _client;
        private ILog _logger = LogManager.GetCurrentClassLogger();

        public RosTopicServer(string nodeId, string topicName)
        {
            NodeId = nodeId;
            TopicName = topicName;
        }

        public string NodeId { get; private set; }
        public string TopicName { get; private set; }

        #region IDisposable Members

        public void Dispose()
        {
            _client.Dispose();
        }

        #endregion

        public Task<IObservable<TMessage>> StartAsync(TopicParam param)
        {
            _client = new TcpRosClient();

            var tcs = new TaskCompletionSource<IObservable<TMessage>>();
            _client.ConnectTaskAsync(param.HostName, param.PortNumber)
                .ContinueWith(t1 =>
                {
                    if (t1.IsFaulted) tcs.SetException(t1.Exception.InnerException);
                    else if (t1.IsCanceled) tcs.SetCanceled();
                    else
                    {
                        try
                        {
                            OnConnected().ContinueWith(t2 =>
                            {
                                if (t2.IsFaulted) tcs.SetException(t2.Exception.InnerException);
                                else if (t2.IsCanceled) tcs.SetCanceled();
                                else tcs.SetResult(t2.Result);
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.Error("Connect Error", ex);
                            tcs.SetException(ex);
                        }
                    }
                });

            return tcs.Task;
        }


        private Task<IObservable<TMessage>> OnConnected()
        {
            var last = _client.ReceiveAsync()
                .Take(1)
                .Select(x => TcpRosHeaderSerializer.Deserialize(new MemoryStream(x)))
                .PublishLast();

            last.Connect();

            var dummy = new TMessage();
            var sendHeader = new
            {
                callerid = NodeId,
                topic = TopicName,
                md5sum = dummy.Md5Sum,
                type = dummy.MessageType
            };

            var stream = new MemoryStream();
            TcpRosHeaderSerializer.Serialize(stream, sendHeader);

            var tcs = new TaskCompletionSource<IObservable<TMessage>>();
            _client.SendTaskAsync(stream.ToArray())
                .ContinueWith(task =>
                {
                    if (task.IsFaulted) tcs.SetException(task.Exception.InnerException);
                    else if (task.IsCanceled) tcs.SetCanceled();
                    else
                    {
                        try
                        {
                            var recvHeader = last.Timeout(TimeSpan.FromMilliseconds(ROS.TopicTimeout)).First();
                            tcs.SetResult(OnReceivedHeader(recvHeader));
                        }
                        catch (RosTopicException ex)
                        {
                            _logger.Error("Header Deserialize Error", ex);
                            tcs.SetException(ex);
                        }
                        catch (TimeoutException ex)
                        {
                            _logger.Error("Receive Header Timeout Error", ex);
                            tcs.SetException(ex);
                        }
                    }
                });

            return tcs.Task;
        }

        private IObservable<TMessage> OnReceivedHeader(dynamic header)
        {
            var dummy = new TMessage();

            if (header.topic != TopicName)
            {
                _logger.Error(m => m("TopicName mismatch error, expected={0} actual={1}", TopicName, header.topic));
                throw new RosTopicException("TopicName mismatch error");
            }
            if (header.type != dummy.MessageType)
            {
                _logger.Error(m => m("TopicType mismatch error, expected={0} actual={1}", dummy.MessageType, header.type));
                throw new RosTopicException("TopicType mismatch error");
            }
            if (header.md5sum != dummy.Md5Sum)
            {
                _logger.Error(m => m("MD5Sum mismatch error, expected={0} actual={1}", dummy.Md5Sum, header.md5sum));
                throw new RosTopicException("MD5Sum mismatch error");
            }

            return _client.ReceiveAsync().Select(Deserialize);
        }

        private TMessage Deserialize(byte[] x)
        {
            var data = new TMessage();
            var br = new BinaryReader(new MemoryStream(x));
            var len = br.ReadInt32();
            if (br.BaseStream.Length != len + 4)
            {
                _logger.Error("Received Invalid Message");
                throw new RosTopicException("Received Invalid Message");
            }
            data.Deserialize(br);
            return data;
        }
    }
}