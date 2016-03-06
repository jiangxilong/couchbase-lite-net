//
// BLIPConnection.cs
//
// Author:
// 	Jim Borden  <jim.borden@couchbase.com>
//
// Copyright (c) 2016 Couchbase, Inc All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Couchbase.Lite;
using Couchbase.Lite.Util;
using System.Linq;
using System.IO;

namespace Couchbase.Protocol.Blip
{
    internal sealed class BLIPRequestWrapper
    {
        public readonly BLIPRequest Request;
        public bool Handled;

        internal BLIPRequestWrapper(BLIPRequest request)
        {
            Request = request;
        }
    }

    internal abstract class BLIPConnection
    {
        private static readonly string Tag = typeof(BLIPConnection).Name;
        private static readonly string[] TypeStrings = { "MSG","RPY","ERR","3??", "ACKMSG", "ACKRPY", "6??", "7??" };
        private const int DefaultFrameSize = 4096;

        private TaskFactory _transportScheduler = new TaskFactory(new SingleTaskThreadpoolScheduler());
        private bool _transportIsOpen;
        private TaskFactory _callbackScheduler = Manager.SharedInstance.CapturedContext;
        private Dictionary<ulong, BLIPRequest> _pendingRequests = new Dictionary<ulong, BLIPRequest>();
        private Dictionary<ulong, BLIPResponse> _pendingResponses = new Dictionary<ulong, BLIPResponse>();
        private Dictionary<string, Action<BLIPRequest>> _registeredActions = new Dictionary<string, Action<BLIPRequest>>();
        private List<BLIPMessage> _outBox;
        private List<BLIPMessage> _iceBox;
        private int _pendingDelegateCalls;
        private BLIPMessage _sendingMsg;
        private uint _numRequestsSent;
        private uint _numRequestsReceived;

        public event EventHandler OnConnect;

        public event TypedEventHandler<BLIPConnection, Exception> OnError;

        public event TypedEventHandler<BLIPConnection, Exception> OnClose;

        public event TypedEventHandler<BLIPConnection, BLIPRequestWrapper> OnRequest;

        public event TypedEventHandler<BLIPConnection, BLIPResponse> OnResponse;

        public abstract Uri Url { get; internal set; }

        public abstract bool TransportCanSend { get; }

        public BLIPException Error { get; private set; }

        public bool Active { get; private set; }

        public bool DispatchPartialMessages { get; set; }

        internal BLIPConnection(bool isOpen)
        {
            _transportIsOpen = isOpen;
        }

        public abstract void Connect();

        public abstract void Close();

        public abstract void SendFrame(IEnumerable<byte> frame);

        public BLIPRequest CreateRequest()
        {
            return new BLIPRequest(this, null, null);
        }

        public BLIPRequest CreateRequest(IEnumerable<byte> body, IDictionary<string, object> properties)
        {
            return new BLIPRequest(this, body, properties);
        }

        public void RegisterAction(string profile, Action<BLIPRequest> action)
        {
            _callbackScheduler.StartNew(() =>
            {
                if(action != null) {
                    _registeredActions[profile] = action;
                } else {
                    _registeredActions.Remove(profile);
                }
            });
        }

        public void Send(BLIPRequest request)
        {
            if (!request.IsMine || request.Sent) {
                // This was an incoming request that I'm being asked to forward or echo;
                // or it's an outgoing request being sent to multiple connections.
                // Since a particular BLIPRequest can only be sent once, make a copy of it to send:
                request = (BLIPRequest)request.Clone();
            }

            var itsConnection = request.Connection;
            if (itsConnection == null) {
                request.Connection = this;
            } else {
                Log.To.Blip.E(Tag, "{0} is already assigned to a different connection, throwing...", request);
                throw new InvalidOperationException("Attempt to send a BLIPRequest on a different connection " +
                "than the one it was assigned to");
            }

            request.Send();
        }

        protected void TransportOpened()
        {
            _transportIsOpen = true;
            if (_outBox != null && _outBox.Count > 0) {
                FeedTransport();
            }
        }

        protected void TransportClosed(BLIPException e)
        {
            Log.To.Blip.I(Tag, String.Format("{0} closed with error", this), e);
            if (_transportIsOpen) {
                _transportIsOpen = false;
                _callbackScheduler.StartNew(() =>
                {
                    if (OnClose != null) {
                        OnClose(this, e);
                    }
                });
            } else {
                if (e != null && Error == null) {
                    Error = e;
                }

                _callbackScheduler.StartNew(() =>
                {
                    if (OnError != null) {
                        OnError(this, e);
                    }
                });
            }
        }

        protected void FeedTransport()
        {
            if (_outBox != null && _outBox.Count > 0 && _sendingMsg == null) {
                // Pop first message in queue:
                var msg = _outBox.First();
                _outBox.RemoveAt(0);
                _sendingMsg = msg;

                // As an optimization, allow message to send a big frame unless there's a higher-priority
                // message right behind it:
                var frameSize = DefaultFrameSize;
                if (msg.Urgent || _outBox.Count == 0 || !_outBox[0].Urgent) {
                    frameSize *= 4;
                }

                // Ask the message to generate its next frame. Do this on the delegate queue:
                bool moreComing;
                IEnumerable<byte> frame;
                _callbackScheduler.StartNew(() =>
                {
                    frame = msg.NextFrame((ushort)frameSize, ref moreComing);
                    bool requeue = msg.NeedsAck;
                    Action<BLIPMessage> onSent = moreComing ? null : () => msg.OnSent;
                    _transportScheduler.StartNew(() => 
                    {
                        // SHAZAM! Send the frame to the transport:
                        if(frame != null) {
                            SendFrame(frame);
                        }

                        _sendingMsg = null;
                        if(moreComing) {
                            // add the message back so it can send its next frame later:
                            if(requeue) {
                                QueueMessage(msg, false, false);
                            } else {
                                PauseMessage(msg);
                            }
                        } else {
                            if(onSent != null) {
                                RunOnDelegateQueue(() => onSent(msg));
                            }
                        }

                        UpdateActive();
                    });
                });
            }
        }

        internal void CloseWithError(BLIPException e)
        {
            Error = e;
            Close();
        }

        internal void RunOnDelegateQueue(Action action)
        {
            _pendingDelegateCalls++;
            _callbackScheduler.StartNew(() =>
            {
                action();
                EndDelegateCall();
            });
        }

        internal void EndDelegateCall()
        {
            _transportScheduler.StartNew(() =>
            {
                if(--_pendingDelegateCalls == 0) {
                    UpdateActive();
                }
            });
        }

        internal void UpdateActive()
        {
            var active = _outBox.Count > 0 || _iceBox.Count > 0 || _pendingRequests.Count > 0 ||
                         _pendingResponses.Count > 0 || _sendingMsg != null || _pendingDelegateCalls > 0;
            if (active != Active) {
                Log.To.Blip.V(Tag, "{0} active = {1}", this, active);
                Active = active;
            }
        }

        internal void MessageReceivedPropertes(BLIPMessage message)
        {
            if (DispatchPartialMessages) {
                if (message.IsRequest) {
                    DispatchRequest((BLIPRequest)message);
                } else {
                    DispatchResponse((BLIPResponse)message);
                }
            }
        }

        internal void QueueMessage(BLIPMessage message, bool isNew, bool sendNow)
        {
            if (_outBox.Contains(message) || _iceBox.Contains(message)) {
                Log.To.Blip.E(Tag, "{0} has already queued {1}, throwing...", this, message);
                throw new InvalidOperationException("Attempting to queue an already queued message");
            }

            if (message == _sendingMsg) {
                Log.To.Blip.E(Tag, "{0} is current sending {1}, throwing...", this, message);
                throw new InvalidOperationException("Attempting to queue an in-flight message");
            }

            var n = _outBox.Count;
            var index = 0;
            if (message.Urgent && n > 1) {
                // High-priority gets queued after the last existing high-priority message,
                // leaving one regular-priority message in between if possible.
                for (index = n - 1; index > 0; index--) {
                    var otherMessage = _outBox[index];
                    if (otherMessage.Urgent) {
                        index = Math.Min(index + 2, n);
                        break;
                    } else if (isNew && otherMessage.BytesWritten == 0) {
                        // But have to keep message starts in order
                        index = index + 1;
                        break;
                    }
                }

                if (index == 0) {
                    index = 1;
                }
            } else {
                // Regular priority goes at the end of the queue:
                index = n;
            }

            if (_outBox == null) {
                _outBox = new List<BLIPMessage>();
            }

            _outBox.Insert(index, message);

            if (isNew) {
                Log.To.Blip.I(Tag, "{0} queuing outgoing {1} at index {2}", this, message, index);
            }

            if (sendNow) {
                if (n == 0 && _transportIsOpen) {
                    _transportScheduler.StartNew(() =>
                    {
                        FeedTransport();
                    });
                }
            }

            UpdateActive();
        }

        internal bool SendRequest(BLIPRequest request, BLIPResponse response)
        {
            if (request.Sent) {
                Log.To.Blip.E(Tag, "Message has already been sent, throwing...");
                throw new InvalidOperationException("Cannot send BLIPRequest twice");
            }

            bool result;
            _transportScheduler.StartNew(() =>
            {
                if(_transportIsOpen && !TransportCanSend) {
                    Log.To.Blip.W(Tag, "{0}: Attempt to send a request after the connected has started closing: {1}",
                        this, request);
                    result = false;
                    return;
                }

                request.AssignedNumber(++_numRequestsSent);
                if(response != null) {
                    response.AssignedNumber(_numRequestsSent);
                    _pendingResponses[response.Number] = response;
                    UpdateActive();
                }

                QueueMessage(request, true, true);
                result = true;
            }).Wait();

            return result;
        }

        internal bool SendResponse(BLIPResponse response)
        {
            if (response.Sent) {
                Log.To.Blip.E(Tag, "Message has already been sent, throwing...");
                throw new InvalidOperationException("Cannot send BLIPResponse twice");
            }

            _transportScheduler.StartNew(() =>
            {
                QueueMessage(response, true, true);
            });
        }

        internal void PauseMessage(BLIPMessage message)
        {
            if (_outBox.Contains(message) || _iceBox.Contains(message)) {
                Log.To.Blip.E(Tag, "Attempting to pause an already queued message, throwing...");
                throw new InvalidOperationException("Cannot pause an already queued message");
            }

            Log.To.Blip.V(Tag, "{0}: Pausing {1}", this, message);
            if (_iceBox == null) {
                _iceBox = new List<BLIPMessage>();
            }

            _iceBox.Add(message);
        }

        internal void UnpauseMessage(BLIPMessage message)
        {
            if (_iceBox == null) {
                return;
            }

            var index = _iceBox.IndexOf(message);
            if (index != -1) {
                if (_outBox.Contains(message)) {
                    Log.To.Blip.E(Tag, "Attempting to unpause an already queued message, throwing...");
                    throw new InvalidOperationException("Cannot unpause an already queued message");
                }

                Log.To.Blip.V(Tag, "{0}: Resuming {1}", this, message);
                _iceBox.RemoveAt(index);
                if (message != _sendingMsg) {
                    QueueMessage(message, false, true);
                }
            }
        }

        internal BLIPMessage GetOutgoingMessage(ulong number, bool isRequest)
        {
            foreach(var msg in _outBox) {
                if (msg.Number == number && msg.IsRequest == isRequest) {
                    return msg;
                }
            }

            foreach(var msg in _iceBox) {
                if (msg.Number == number && msg.IsRequest == isRequest) {
                    return msg;
                }
            }

            if (_sendingMsg.Number == number && _sendingMsg.IsRequest == isRequest) {
                return _sendingMsg;
            }

            return null;
        }

        internal void SendAck(uint number, bool isRequest, ulong bytesReceived)
        {
            Log.To.Blip.V(Tag, "{0}: Sending {1} of {2} ({3} bytes)", this, isRequest ? "ACKMSG" : "ACKRPY",
                number, bytesReceived);
            var flags = (isRequest ? BLIPMessageFlags.AckMsg : BLIPMessageFlags.AckRpy) | BLIPMessageFlags.Urgent |
                        BLIPMessageFlags.NoReply;

            var ackFrame = default(byte[]);
            using (var ms = new MemoryStream()) {
                var nextBytes = VarintBitConverter.GetVarintBytes(number);
                ms.Write(nextBytes, 0, nextBytes.Length);
                nextBytes = VarintBitConverter.GetVarintBytes((int)flags);
                ms.Write(nextBytes, 0, nextBytes.Length);
                nextBytes = VarintBitConverter.GetVarintBytes(bytesReceived);
                ms.Write(nextBytes, 0, nextBytes.Length);

                ackFrame = ms.ToArray();
            }

            SendFrame(ackFrame);
        }

        internal void ReceivedFrame(Stream frame)
        {
            ulong messageNum;
            try {
                messageNum = VarintBitConverter.ToUInt64(frame);
                ulong flags = VarintBitConverter.ToUInt64(frame);
                if(flags <= BLIPMessageFlags.MaxFlag) {
                    var body = new byte[frame.Length - frame.Position];
                    frame.Read(body, 0, body.Length);
                    ReceivedFrame(messageNum, (BLIPMessageFlags)flags, body);
                }
            } catch(ArgumentException) {
                
            }

            CloseWithError(BLIPUtility.MakeException(BLIPError.BadFrame, "Bad varint encoding in frame flags"));
        }

        private void ReceivedFrame(ulong requestNum, BLIPMessageFlags flags, byte[] body)
        {
            var type = (BLIPMessageFlags)(flags & BLIPMessageFlags.TypeMask);
            Log.To.Blip.V(Tag, "{0} rcvd frame of {1} #{2}, length {3}", this, TypeStrings[type], requestNum,
                body.Length);

            var key = requestNum;
            var complete = !flags.HasFlag(BLIPMessageFlags.MoreComing);
            switch (type) {
                case BLIPMessageFlags.Msg:
                    // Incoming request:
                    var request = _pendingRequests.Get(key);
                    if (request != null) {
                        // Continuation frame of a request:
                        if (complete) {
                            _pendingRequests.Remove(key);
                        }
                    } else if (requestNum == _numRequestsReceived + 1) {
                        // Next new request:
                        request = new BLIPRequest(this, false, flags | BLIPMessageFlags.MoreComing, requestNum, null);
                        if (!complete) {
                            _pendingRequests[key] = request;
                        }

                        ++_numRequestsReceived;
                    } else {
                        CloseWithError(BLIPUtility.MakeException(BLIPError.BadFrame, "Received bad request frame #{0}, " +
                        "(next is #{1})", requestNum, _numRequestsReceived + 1));
                        return;
                    }

                    ReceivedFrame(flags, body, complete, request);
                    break;
                case BLIPMessageFlags.Rpy:
                case BLIPMessageFlags.Err:
                    var response = _pendingResponses.Get(key);
                    if (response != null) {
                        if (complete) {
                            _pendingResponses.Remove(key);
                        }

                        ReceivedFrame(flags, body, complete, response);
                    } else {
                        if (requestNum <= _numRequestsSent) {
                            Log.To.Blip.I(Tag, "??? {0} got unexpected response frame to my msg #{0}", requestNum); // benign
                        } else {
                            CloseWithError(BLIPUtility.MakeException(BLIPError.BadFrame, "Bogus message number {0} in response",
                                requestNum));
                            return;
                        }
                    }
                    break;
                case BLIPMessageFlags.AckMsg:
                case BLIPMessageFlags.AckRpy:
                    var msg = GetOutgoingMessage(requestNum, (type == BLIPMessageFlags.AckMsg));
                    if (msg == null) {
                        Log.To.Blip.I(Tag, "??? {0} received ACK for non-current message ({1} {2})",
                            this, TypeStrings[type], requestNum);
                        break;
                    }

                    ulong bytesReceived;
                    try {
                        bytesReceived = VarintBitConverter.ToUInt64(body);
                    } catch (ArgumentException) {
                        CloseWithError(BLIPUtility.MakeException(BLIPError.BadFrame, "Bad ACK body"));
                        return;
                    }

                    RunOnDelegateQueue(() =>
                    {
                        var ok = msg.ReceivedAck(bytesReceived);
                        if (ok) {
                            UnpauseMessage(msg);
                        } else {
                            CloseWithError(BLIPUtility.MakeException(BLIPError.BadFrame, "Bad ACK count"));
                        }
                    });
                    break;
                default:
                    // To leave room for future expansion, undefined message types are just ignored.
                    Log.To.Blip.I(Tag, "??? {0} received header with unknown message type {1}", this, type);
                    break;
            }

            UpdateActive();
        }

        private void ReceivedFrame(BLIPMessageFlags flags, byte[] body, bool complete, BLIPMessage message)
        {
            RunOnDelegateQueue(() =>
            {
                var ok = _sendingMsg.ReceivedFrame(flags, body);
                if(!ok) {
                    _transportScheduler.StartNew(() => {
                        CloseWithError(BLIPUtility.MakeException(BLIPError.BadFrame, "Couldn't parse message frame"));
                    });
                } else if(DispatchPartialMessages) {
                    if(message.IsRequest) {
                        DispatchRequest((BLIPRequest)message);
                    } else {
                        DispatchResponse((BLIPResponse)message);
                    }
                }
            });
        }

        private void DispatchRequest(BLIPRequest request)
        {
            Log.To.Blip.I(Tag, "Dispatching {0} ({1})", request,
                new SecureLogJsonString(request.Properties, LogMessageSensitivity.PotentiallyInsecure));
            try {
                bool handled;
                if(request.Flags.HasFlag(BLIPMessageFlags.Meta)) {
                    handled = DispatchMetaRequest(request);
                } else {
                    handled = SendRegisteredAction(request);
                    if(!handled && OnRequest != null) {
                        var wrapper = new BLIPRequestWrapper(request);
                        OnRequest(this, wrapper);
                        handled = wrapper.Handled;
                    }
                }

                if(request.Complete) {
                    if(!handled) {
                        Log.To.Sync.I(Tag, "No handler found for incoming {0}", request);
                        request.Respond(BLIPError.NotFound, "No handler was found");
                    } else {
                        Log.To.Sync.I(Tag, "Returning default empty response to {0}", request);
                        request.Respond(null, null);
                    }
                }
            } catch(Exception e) {
                Log.To.Sync.E(Tag, "Exception disaptching BLIP request", e);
                request.Respond(e);
            }
        }

        private void DispatchResponse(BLIPResponse response)
        {
            Log.To.Blip.I(Tag, "Dispatching {0}", response);
            if (OnResponse != null) {
                OnResponse(this, response);
            }
        }

        private bool DispatchMetaRequest(BLIPRequest request)
        {
            //TODO
            return false;
        }

        private bool SendRegisteredAction(BLIPRequest request)
        {
            var profile = request.Profile;
            if (profile != null) {
                var action = _registeredActions.Get(profile);
                if (action != null) {
                    action(request);
                    return true;
                }
            }

            return false;
        }
    }
}

