//
// BLIPResponse.cs
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
using Couchbase.Lite.Util;
using System.Collections.Generic;
using Couchbase.Lite;
using System.Text;

namespace Couchbase.Protocol.Blip
{
    internal sealed class BLIPResponse : BLIPMessage
    {
        private static readonly string Tag = typeof(BLIPResponse).Name;

        public Action<BLIPResponse> OnComplete { get; set; }

        public BLIPException Error 
        {
            get {
                if (!Flags.HasFlag(BLIPMessageFlags.Err)) {
                    return null;
                }

                var userInfo = new Dictionary<string, string>(Properties);
                var code = userInfo.Get("Error-Code");
                int parsedCode;
                if (code == "0" || !Int32.TryParse(code, out parsedCode)) {
                    parsedCode = (int)BLIPError.Unspecified;
                }

                userInfo.Remove("Error-Domain");
                userInfo.Remove("Error-Code");
                var message = BodyString;

                return new BLIPException((BLIPError)parsedCode, message);
            }
            set { 
                if (!IsMine) {
                    Log.To.Sync.E(Tag, "Attempting to set an error on a BLIPResponse where IsMine == false, throwing...");
                    throw new InvalidOperationException("Cannot set an error on a BLIPResponse where IsMine == false");
                }

                if (!CanWrite) {
                    Log.To.Sync.E(Tag, "Attempting to set an error on an immutable object, throwing...");
                    throw new InvalidOperationException("Cannot set an error on an immutable object");
                }

                SetError(value);
            }
        }

        public override bool Complete
        {
            get {
                return base.Complete;
            }
            internal set {
                base.Complete = value;
                if (value && OnComplete != null) {
                    try {
                        OnComplete(this);
                    } catch(Exception e) {
                        Log.To.Blip.E(Tag, "Exception in BLIPResponse OnComplete block, continuing...", e);
                    }

                    OnComplete = null;
                }
            }
        }

        internal BLIPResponse(BLIPRequest request)
            : base(request.Connection, !request.IsMine, BLIPMessageFlags.Rpy | BLIPMessageFlags.MoreComing, 
                request.Number, null)
        {
            if (IsMine && request.Urgent) {
                ToggleFlags(BLIPMessageFlags.Urgent, true);
            }
        }

        internal void SetError(BLIPException e)
        {
            ToggleFlags(BLIPMessageFlags.TypeMask, false);
            if (e != null) {
                // Setting this stuff is a PITA because this object might be technically immutable,
                // in which case the standard setters would barf if I called them.
                ToggleFlags(BLIPMessageFlags.Err, true);
                var errorProps = Properties != null ? new Dictionary<string, string>(Properties) :
                new Dictionary<string, string>();
                errorProps["Error-Code"] = e.ErrorCode.ToString();
                Properties = errorProps;

                var message = e.Message;
                Body = Encoding.UTF8.GetBytes(message);
            } else {
                ToggleFlags(BLIPMessageFlags.Rpy, true);
                Properties.Clear();
            }
        }

        internal bool Send()
        {
            if (_connection == null) {
                Log.To.Blip.E(Tag, "{0} has no connection to send over, throwing...", this);
                throw new InvalidOperationException("BLIPResponse has no connection to send over");
            }

            if (Sent) {
                Log.To.Blip.E(Tag, "{0} was already sent, throwing...", this);
                throw new InvalidOperationException("Cannot send same BLIPResponse twice");
            }

            Encode();
            var sent = Sent = _connection.SendResponse(this);
            System.Diagnostics.Debug.Assert(sent, "Failed to send BLIPResponse");
            return sent;
        }

        internal override void ConnectionClosed()
        {
            base.ConnectionClosed();
            if (!IsMine && !Complete) {
                var error = _connection.Error;
                if (error == null) {
                    error = BLIPUtility.MakeException(BLIPError.Disconnected, "Connection closed before response was received");
                }

                // Change incoming response to an error:
                CanWrite = true;
                SetError(error);
                CanWrite = false;

                Complete = true;
            }
        }
    }
}

