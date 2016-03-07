//
// BLIPWebSocketConnection.cs
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
using System.Net.Http;
using System.Net;

using Couchbase.Lite.Util;
using System.Linq;
using System.Collections.Generic;
using WebSocketSharp;
using System.IO;

namespace Couchbase.Protocol.Blip.WebSocket
{
    internal sealed class BLIPWebSocketConnection : BLIPConnection
    {
        private static readonly string Tag = typeof(BLIPWebSocketConnection).Name;
        private BLIPHTTPLogic _httpLogic;

        public WebSocketSharp.WebSocket Socket { get; private set; }

        public override bool TransportCanSend
        {
            get {
                return Socket.ReadyState == WebSocketState.Open;
            }
        }

        public override Uri Url { get; internal set; }

        public BLIPWebSocketConnection(HttpRequestMessage message)
            : this(null, message.RequestUri, false)
        {
            /*if (message == null) {
                Log.To.Blip.E(Tag, "message cannot be null in ctor, throwing...");
                throw new ArgumentNullException("message");
            }

            _httpLogic = new BLIPHTTPLogic(message);
            _httpLogic.SetHeaderValue("Sec-WebSocket-Protocol", "BLIP");*/
        }

        public BLIPWebSocketConnection(Uri url)
            : this(null, url, false)
        {

        }

        internal BLIPWebSocketConnection(WebSocketSharp.WebSocket webSocket, Uri url, bool incoming)
            : base(incoming)
        {
            Socket = webSocket;
            if (!incoming && webSocket != null) {
                SetupCallbacks();
            }

            Url = url;
        }

        public void SetCredential(NetworkCredential credential)
        {
            Socket.SetCredentials(credential.UserName, credential.Password, true);
        }

        public override void Connect()
        {
            Log.To.Blip.I(Tag, "{0} connecting to <{1}>...", this, new SecureLogUri(Url));
            Socket = new WebSocketSharp.WebSocket(Url.AbsoluteUri);
            SetupCallbacks();
            Socket.ConnectAsync();
        }

        public void Close(int code, string reason)
        {
            Socket.CloseAsync((CloseStatusCode)code, reason);
        }

        private void SetupCallbacks()
        {
            Socket.OnMessage += OnWebSocketMessage;
            Socket.OnOpen += OnWebSocketOpen;
            Socket.OnError += OnWebSocketError;
            Socket.OnClose += OnWebSocketClosed;
        }

        private void OnWebSocketClosed (object sender, CloseEventArgs e)
        {
            var error = default(BLIPException);
            if (e.Code != (ushort)CloseStatusCode.Normal || !e.WasClean) {
                error = BLIPUtility.MakeException(BLIPError.Disconnected, e.Reason);
            }

            TransportClosed(error);
        }

        private void OnWebSocketError (object sender, WebSocketSharp.ErrorEventArgs e)
        {
            TransportClosed(BLIPUtility.MakeException(e.Exception));
        }

        private void OnWebSocketOpen (object sender, EventArgs e)
        {
            TransportOpened();
        }

        private void OnWebSocketMessage(object sender, MessageEventArgs args)
        {
            if (args.IsBinary) {
                using (var ms = new MemoryStream(args.RawData)) {
                    base.ReceivedFrame(ms);
                }
            }
        }

        public override void Close()
        {
            //TODO: Error reporting
            Socket.CloseAsync();
        }

        public override void SendFrame(IEnumerable<byte> frame)
        {
            Socket.SendAsync(frame.ToArray(), null);
        }
    }
}

