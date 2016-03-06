//
// BLIPMessage.cs
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
using System.IO;
using System.Collections.Generic;
using Couchbase.Lite.Util;
using System.Text;
using System.Linq;
using Couchbase.Lite;
using ICSharpCode.SharpZipLib.Zip;

namespace Couchbase.Protocol.Blip
{
    internal enum BLIPError
    {
        BadData = 1,
        BadFrame,
        Disconnected,
        PeerNotAllowed,

        Misc = 99,

        // errors returned in responses:
        BadRequest = 400,
        Forbidden = 403,
        NotFound = 404,
        BadRange = 416,

        HandlerFailed = 501,
        Unspecified = 599       // peer didn't send any detailed info
    }

    [Flags]
    internal enum BLIPMessageFlags
    {
        Msg = 0x00,
        Rpy = 0x01,
        Err = 0x02,
        AckMsg = 0x04,
        AckRpy = 0x05,

        TypeMask = 0x07,
        Compressed = 0x08,
        Urgent = 0x10,
        NoReply = 0x20,
        MoreComing = 0x40,
        Meta = 0x80,

        MaxFlag = 0xFF
    }

    internal abstract class BLIPMessage
    {
        private static readonly string Tag = typeof(BLIPMessage).Name;
        private const int MaxUnackedBytes = 128000;
        private const int AckByteInterval = 50000;

        private readonly bool _isMine;
        private readonly Dictionary<string, string> _properties;
        private List<byte> _body;
        private List<Stream> _bodyStreams = new List<Stream>();
        private ulong _bytesReceived;

        protected BLIPConnection _connection;
        protected MemoryStream _encodedBody;
        protected Stream _outgoing;
        protected Stream _incoming;

        public Action<BLIPMessage, Stream> OnDataReceived { get; set; }

        public Action<BLIPMessage, ulong> OnDataSent { get; set; }

        public Action<BLIPMessage> OnSent { get; set; }

        public uint Number { get; private set; }

        public bool IsMine
        {
            get {
                return _isMine;
            }
        }

        public bool IsRequest
        {
            get {
                return (Flags & BLIPMessageFlags.TypeMask) == BLIPMessageFlags.Msg;
            }
        } 

        public bool Sent { get; protected set; }

        public bool PropertiesAvailable { get; private set; }

        internal BLIPMessageFlags Flags { get; private set; }

        public virtual bool Complete { get; internal set; }

        public bool Compressed
        {
            get {
                return Flags.HasFlag(BLIPMessageFlags.Compressed);
            } 
            set { 
                ToggleFlags(BLIPMessageFlags.Compressed, value);
            }
        }

        public bool Urgent 
        {
            get {
                return Flags.HasFlag(BLIPMessageFlags.Urgent);
            }
            set { 
                ToggleFlags(BLIPMessageFlags.Urgent, value);
            }
        }

        public bool CanWrite { get; private set; }

        public IEnumerable<byte> Body 
        {
            get { 
                return _body.ToArray(); 
            } set { 
                if (!IsMine || !CanWrite) {
                    Log.To.Sync.E(Tag, "Attempt to write to a readonly BLIPMessage, throwing...");
                    throw new InvalidOperationException("Attempt to write to a readonly BLIPMessage");
                }

                _body = new List<byte>(value);
            }
        }

        public string BodyString 
        {
            get {
                var body = Body;
                if (body != null) {
                    return Encoding.UTF8.GetString(body.ToArray());
                }

                return null;
            }
            set { 
                Body = Encoding.UTF8.GetBytes(value).ToList();
                ContentType = "text/plain; charset=UTF-8";
            }
        }

        public object BodyJSON 
        {
            get {
                var retVal = default(object);
                try {
                    retVal = Manager.GetObjectMapper().ReadValue(Body);
                } catch(Exception e) {
                    Log.To.Sync.W(Tag, String.Format("Couldn't parse {0} as JSON, returning null...",
                        new SecureLogString(Body, LogMessageSensitivity.PotentiallyInsecure)), e);
                    return null;
                }

                return retVal;
            }
            set { 
                var bytes = default(IEnumerable<byte>);
                try {
                    bytes = Manager.GetObjectMapper().WriteValueAsBytes(value);
                } catch(Exception e) {
                    Log.To.Sync.E(Tag, String.Format("Couldn't serialize {0} to JSON, throwing...",
                        value.GetType().Name), e);
                    throw new ArgumentException("Invalid BodyJSON object", "value");
                }

                Body = bytes;
            }
        }

        public object Context { get; set; }

        public IDictionary<string, string> Properties { get; set; }

        public string ContentType 
        {
            get {
                return this["Content-Type"];
            } 
            set { 
                this["Content-Type"] = value;
            }
        }

        public string Profile 
        {
            get {
                return this["Profile"];
            } 
            set { 
                this["Profile"] = value;
            }
        }

        public string this[string key]
        {
            get {
                return Properties.Get(key);
            }
            set { 
                Properties[key] = value;
            }
        }

        internal bool NeedsAck
        {
            get {
                System.Diagnostics.Debug.Assert(IsMine);
                return BytesWritten - _bytesReceived >= MaxUnackedBytes;
            }
        }

        internal ulong BytesWritten { get; private set; }

        internal BLIPMessage(BLIPConnection connection, bool isMine, BLIPMessageFlags flags, uint msgNo,
            IEnumerable<byte> body)
        {
            _connection = connection;
            _isMine = isMine;
            CanWrite = isMine;
            Flags = flags;
            Number = msgNo;
            if (isMine) {
                Body = new List<byte>(body);
                _properties = new Dictionary<string, string>();
                PropertiesAvailable = true;
                Complete = true;
            } else if(body != null) {
                Log.To.Sync.E(Tag, "Received non-null body but isMine == false, throwing...");
                throw new InvalidOperationException("Cannot construct a BLIPMessage with a body that isn't mine");
            }
        }

        public override string ToString()
        {
            //TODO: Length / compression
            var sb = new StringBuilder();
            sb.AppendFormat("{0}[#{1}{2}", GetType().Name, Number, IsMine ? "->" : "<-");
            if (Flags.HasFlag(BLIPMessageFlags.Urgent)) {
                sb.Append(", urgent");
            }

            if (Flags.HasFlag(BLIPMessageFlags.NoReply)) {
                sb.Append(", noreply");
            }

            if (Flags.HasFlag(BLIPMessageFlags.Meta)) {
                sb.Append(", META");
            }

            if (Flags.HasFlag(BLIPMessageFlags.MoreComing)) {
                sb.Append(", incomplete");
            }

            sb.Append("]");
            return sb.ToString();
        }

        internal void ToggleFlags(BLIPMessageFlags flags, bool on)
        {
            if (!IsMine || !CanWrite) {
                Log.To.Sync.E(Tag, "Attempt to write to a readonly BLIPMessage, throwing...");
                throw new InvalidOperationException("Attempt to write to a readonly BLIPMessage");
            }

            if (on) {
                Flags != flags;
            } else {
                Flags &= ~flags;
            }
        }

        internal void Encode()
        {
            if (!IsMine || !CanWrite) {
                Log.To.Sync.E(Tag, "Attempt to write to a readonly BLIPMessage, throwing...");
                throw new InvalidOperationException("Attempt to write to a readonly BLIPMessage");
            }

            CanWrite = false;
            _encodedBody = new MemoryStream();
            _encodedBody.Write(_body.ToArray(), 0, _body.Count);
            foreach (var stream in _bodyStreams) {
                stream.CopyTo(_encodedBody);
            }

            if (Compressed) {
                _outgoing = new ZipInputStream(_encodedBody) { IsStreamOwner = true };
            } else {
                _outgoing = _encodedBody;
            }
        }

        internal void AssignedNumber(uint number)
        {
            if (Number != 0) {
                Log.To.Sync.E(Tag, "Attempt to set number twice on a BLIPMessage object ({0})", Number);
                throw new InvalidOperationException("Attempt to set number twice on a BLIPMessage object");
            }

            Number = number;
            CanWrite = false;
        }

        internal IEnumerable<byte> NextFrame(ushort maxSize, ref bool moreComing)
        {
            if (Number == 0) {
                Log.To.Sync.E(Tag, "{0}: Cannot generate next frame because Number == 0", this);
                throw new InvalidOperationException("Invalid state for generating frames");
            }

            if (!IsMine) {
                Log.To.Sync.E(Tag, "{0}: Cannot generate next frame because IsMine == false", this);
                throw new InvalidOperationException("Invalid state for generating frames");
            }

            if (_outgoing == null) {
                Log.To.Sync.E(Tag, "{0}: Cannot generate next frame because stream is not generated", this);
                throw new InvalidOperationException("Invalid state for generating frames");
            }

            moreComing = false;
            if (BytesWritten == 0) {
                Log.To.Sync.I(Tag, "Now sending {0}", this);
            }

            var frame = new List<byte>(maxSize);
            var prevBytesWritten = BytesWritten;
            if (BytesWritten == 0) {
                // First frame: always write entire properties
                var propertyData = BLIPProperties.Encode(_properties);
                frame.AddRange(propertyData);
                BytesWritten += propertyData.Count();
            }

            // Now read from the payload:
            var frameLen = frame.Count;
            if (frameLen < maxSize) {
                var buffer = new byte[maxSize - frameLen];
                var bytesRead = 0;
                try {
                    bytesRead = _outgoing.Read(buffer, 0, buffer.Length);
                } catch(IOException e) {
                    Log.To.Sync.W(Tag, String.Format("Unable to send {0}: Couldn't read body from stream, returning null..."
                        , this), e);
                    if (OnDataSent != null) {
                        OnDataSent(this, 0);
                    }

                    Complete = true;
                    return null;
                }

                BytesWritten += bytesRead;
            }

            // Write the header at the start of the frame:
            if (_outgoing.Position == _outgoing.Length) {
                ToggleFlags(BLIPMessageFlags.MoreComing, false);
                _outgoing.Dispose();
                _outgoing = null;
            } else {
                ToggleFlags(BLIPMessageFlags.MoreComing, true);
                moreComing = true;
            }

            frame.AddRange(VarintBitConverter.GetVarintBytes(Number));
            frame.AddRange(VarintBitConverter.GetVarintBytes((int)Flags));
            Log.To.Sync.V(Tag, "{0} pushing frame, bytes {1}-{2}{3}", this, prevBytesWritten, BytesWritten, 
                moreComing ? "" : " (finished)");

            if (OnDataSent != null) {
                OnDataSent(this, BytesWritten);
            }

            if (!moreComing) {
                Complete = true;
            }

            return frame;
        }

        internal bool ReceivedAck(ulong bytesReceived)
        {
            System.Diagnostics.Debug.Assert(IsMine);
            if (bytesReceived <= _bytesReceived || bytesReceived > BytesWritten) {
                return false;
            }

            _bytesReceived = bytesReceived;
            return true;
        }

        // Parses the next incoming frame.
        internal bool ReceivedFrame(BLIPMessageFlags flags, IEnumerable<byte> body)
        {
            var realized = body.ToArray();
            Log.To.Sync.V(Tag, "{0} rcvd bytes {1}-{2}, flags={3}", this, _bytesReceived,
                _bytesReceived + realized.Length, flags);
            System.Diagnostics.Debug.Assert(!IsMine);
            System.Diagnostics.Debug.Assert(flags.HasFlag(BLIPMessageFlags.MoreComing));

            if (!IsRequest) {
                Flags = Flags | BLIPMessageFlags.MoreComing;
            }

            var oldBytesReceived = _bytesReceived;
            _bytesReceived += realized.Length;
            var shouldAck = flags.HasFlag(BLIPMessageFlags.MoreComing) && oldBytesReceived > 0 &&
                            (oldBytesReceived / AckByteInterval) < (_bytesReceived / AckByteInterval);

            if (!_incoming) {
                _incoming = _encodedBody = new MemoryStream();
            }

            try {
                _incoming.Write(realized, 0, realized.Length);
            } catch(IOException e) {
                Log.To.Sync.W(Tag, "Failed to write to incoming stream, returning false...");
                return false;
            }

            if (_properties == null) {
                // Try to extract the properties:
                bool complete;
                _properties = BLIPProperties.Read(_encodedBody, ref complete);
                if (_properties != null) {
                    if (flags.HasFlag(BLIPMessageFlags.Compressed)) {
                        // Now that properties are read, enable decompression for the rest of the stream:
                        ToggleFlags(BLIPMessageFlags.Compressed, true);
                        var restOfFrame = _encodedBody.ReadAllBytes();
                        _encodedBody = new MemoryStream();
                        _incoming = new ZipOutputStream(_encodedBody) { IsStreamOwner = true };
                        if (restOfFrame.Length > 0) {
                            try {
                                _incoming.Write(restOfFrame, 0, restOfFrame.Length);
                            } catch (IOException e) {
                                Log.To.Sync.W(Tag, "Exception unzipping stream, returning false...", e);
                                return false;
                            }
                        }

                        PropertiesAvailable = true;
                        _connection.MessageReceivedProperties(this);
                    }
                } else if (complete) {
                    return false;
                }
            }

            if (_properties != null && OnDataReceived != null) {
                Log.To.Sync.V(Tag, "{0} -> calling OnDataReceived({1} bytes)", this, _encodedBody.Length);
                _encodedBody.Seek(0, SeekOrigin.Begin);
                OnDataReceived(this, _encodedBody);
            }

            if (!flags.HasFlag(BLIPMessageFlags.MoreComing)) {
                ToggleFlags(BLIPMessageFlags.MoreComing, false);
                if (_properties == null) {
                    return false;
                }

                _body = _encodedBody.ReadAllBytes().ToList();
                _encodedBody.Dispose();
                _encodedBody = null;
                _incoming.Dispose();
                _incoming = null;
                OnDataReceived = null;
                Complete = true;
            }

            if (shouldAck) {
                _connection.SendAck(Number, IsRequest, _bytesReceived);
            }

            return true;
        }

        internal virtual void ConnectionClosed()
        {
            if (IsMine) {
                BytesWritten = 0;
                ToggleFlags(BLIPMessageFlags.MoreComing, true);
            }
        }
    }
}

