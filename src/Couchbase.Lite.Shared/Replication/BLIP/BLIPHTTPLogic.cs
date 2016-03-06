//
// BLIPHTTPLogic.cs
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

namespace Couchbase.Protocol.Blip
{
    internal sealed class BLIPHTTPLogic
    {
        private static readonly string Tag = typeof(BLIPHTTPLogic).Name;

        public bool HandleRedirects { get; set; }

        public bool ShouldContinue { get; private set; }

        public bool ShouldRetry { get; private set; }

        public Uri Url 
        {
            get { return RequestMessage.RequestUri; }
        }

        public ushort Port
        {
            get {
                return (ushort)RequestMessage.RequestUri.Port;
            }
        }

        public bool UseTLS
        {
            get { 
                var scheme = RequestMessage.RequestUri.Scheme;
                return scheme == "https" || scheme == "wss";
            }
        }

        public NetworkCredential Credential { get; set; }

        public int HttpStatus { get; private set; }

        public Exception Error { get; private set; }

        public static string UserAgent
        {
            get { return String.Format("CouchbaseLite/{0} ({1})", Replication.SyncProtocolVersion, Manager.VersionString); }
        }

        public HttpRequestMessage RequestMessage { get; private set; }

        public BLIPHTTPLogic(HttpRequestMessage message)
        {
            if (message == null) {
                Log.To.Blip.E(Tag, "message cannot be null in ctor, throwing...");
                throw new ArgumentNullException("message");
            }

            RequestMessage = message;
            HandleRedirects = true;
        }

        public HttpRequestMessage CreateRequestMessage()
        {

        }

        public void ReceivedResponse(HttpRequestMessage message)
        {

        }

        public void SetHeaderValue(string key, string value)
        {
            RequestMessage.Headers.TryAddWithoutValidation(key, value);
        }
    }
}

