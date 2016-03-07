//
// BLIPProperties.cs
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
using System.IO;
using Couchbase.Lite;
using System.Linq;

namespace Couchbase.Protocol.Blip
{
    internal static class BLIPProperties
    {
        private static readonly string[] Abbreviations = new string[] {
            "Profile",
            "Error-Code",
            "Error-Domain",

            "Content-Type",
            "application/json",
            "application/octet-stream",
            "text/plain; charset=UTF-8",
            "text/xml",

            "Accept",
            "Cache-Control",
            "must-revalidate",
            "If-Match",
            "If-None-Match",
            "Location"
        };

        public static IEnumerable<byte> Encode(IDictionary<string, string> properties)
        {
            var data = new List<byte>(16 * properties.Count);
            foreach (var pair in properties) {
                AppendString(data, pair.Key);
                AppendString(data, pair.Value);
            }

            var length = data.Count;
            data.InsertRange(0, VarintBitConverter.GetVarintBytes(length));
            return data;
        }

        public static Dictionary<string, string> Read(Stream input, ref bool complete)
        {
            var pos = 0;
            var length = 0U;
            try {
                length = VarintBitConverter.ToUInt32(input);
            } catch(Exception) {
                complete = false;
                return null;
            }

            complete = true;
            var retVal = new Dictionary<string, string>();
            if (length == 0) {
                return retVal;
            }

            var buffer = new byte[length];
            input.Read(buffer, 0, (int)length);

            var offset = 0;
            while (offset < length) {
                var key = ReadString(input, ref offset);
                if (key == null) {
                    return null;
                }

                var value = ReadString(input, ref offset);
                if (value == null) {
                    return null;
                }

                retVal[key] = value;
            }

            return retVal;
        }

        private static void AppendString(List<byte> data, string str)
        {
            byte index = 0;
            foreach (var abbreviation in Abbreviations) {
                if (str == abbreviation) {
                    data.Add((byte)(index+1));
                    data.Add(0);
                    return;
                }
                index++;
            }

            data.AddRange(Encoding.UTF8.GetBytes(str));
            data.Add(0);
        }

        private static string ReadString(Stream stream, ref int bytesRead)
        {
            var nextBytes = ReadUntil(stream, 0).ToArray();
            if (nextBytes.Length == 0) {
                return String.Empty;
            }

            bytesRead += nextBytes.Length;
            var first = nextBytes[0];
            if (nextBytes.Length == 2 && first < ' ') {
                if (first > Abbreviations.Length) {
                    return null;
                }

                return Abbreviations[first - 1];
            }

            return Encoding.UTF8.GetString(nextBytes);
        }

        private static IEnumerable<byte> ReadUntil(Stream stream, byte end)
        {
            var next = stream.ReadByte();
            while (next != end && next != -1) {
                yield return (byte)next;
            }
        }
    }
}

