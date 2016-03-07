//
// BLIPRequest.cs
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
using System.Collections.Generic;
using System.Text;
using Couchbase.Lite.Util;

namespace Couchbase.Protocol.Blip
{
    internal sealed class BLIPRequest : BLIPMessage, ICloneable
    {
        private static readonly string Tag = typeof(BLIPRequest).Name;
        private BLIPResponse _response;

        private BLIPResponse Response
        {
            get {
                if (_response == null && !NoReply) {
                    _response = new BLIPResponse(this);
                }

                return _response;
            }
        }

        internal bool NoReply
        {
            get {
                return Flags.HasFlag(BLIPMessageFlags.NoReply);
            }
            set { 
                ToggleFlags(BLIPMessageFlags.NoReply, value);
            }
        }

        internal bool RepliedTo
        {
            get {
                return _response != null;
            }
        }

        internal BLIPConnection Connection
        {
            get { return _connection; }
            set { 
                System.Diagnostics.Debug.Assert(IsMine && !Sent, "Connection can only be set before sending");
                _connection = value;
            }
        }

        public BLIPRequest(IEnumerable<byte> body)
            : this(null, body, null)
        {

        }

        public BLIPRequest(string bodyString)
            : this(Encoding.UTF8.GetBytes(bodyString))
        {

        }

        public BLIPRequest(IEnumerable<byte> body, IDictionary<string, string> properties)
            : this(null, body, properties)
        {

        }

        internal BLIPRequest(BLIPConnection connection, IEnumerable<byte> body, IDictionary<string, string> properties)
            : base(connection, true, BLIPMessageFlags.Msg, 0, body)
        {
            if (body != null) {
                Body = body;
            }

            if (properties != null) {
                Properties = properties;
            }
        }

        internal BLIPRequest(BLIPConnection connection, bool isMine, BLIPMessageFlags flags, ulong msgNo, IEnumerable<byte> body)
            : base(connection, isMine, flags, msgNo, body)
        {

        }

        public BLIPResponse Send()
        {
            System.Diagnostics.Debug.Assert(_connection != null, "No connection to send over");
            System.Diagnostics.Debug.Assert(!Sent, "Message was already sent");
            Encode();
            var response = Response;
            if (_connection.SendRequest(this, response)) {
                Sent = true;
            } else {
                response = null;
            }

            return response;
        }

        internal void DeferResponse()
        {
            // This will allocate _response, causing -repliedTo to become YES, so BLIPConnection won't
            // send an automatic empty response after the current request handler returns.
            Log.To.Sync.I(Tag, "Deferring response to {0}", this);
            if (_response == null && !NoReply) {
                _response = new BLIPResponse(this);
            }
        }

        internal void Respond(IEnumerable<byte> data, string contentType)
        {
            var response = Response;
            response.Body = data;
            response.ContentType = contentType;
            response.Send();
        }

        internal void Respond(string str)
        {
            Respond(Encoding.UTF8.GetBytes(str), "text/plain; charset=UTF-8");
        }

        internal void Respond(object jsonObject)
        {
            var response = Response;
            response.BodyJSON = jsonObject;
            response.Send();
        }

        internal void Respond(BLIPException e)
        {
            Response.Error = e;
            Response.Send();
        }

        internal void Respond(BLIPError errorCode, string errorMessage)
        {
            Respond(BLIPUtility.MakeException(errorCode, errorMessage));
        }

        internal void Respond(Exception e)
        {
            Respond(BLIPUtility.MakeException(e));
        }

        #region ICloneable

        public object Clone()
        {
            System.Diagnostics.Debug.Assert(Complete);
            var copy = new BLIPRequest(Body, Properties);
            copy.Compressed = Compressed;
            copy.Urgent = Urgent;
            copy.NoReply = NoReply;
            return copy;
        }

        #endregion
    }
}

