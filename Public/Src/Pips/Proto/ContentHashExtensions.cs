// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using Google.Protobuf;
using Google.Protobuf.Collections;
using AbsolutePathProto = BuildXL.Proto.AbsolutePath;
using DirectoryArtifactProto = BuildXL.Proto.DirectoryArtifact;
using FileArtifactProto = BuildXL.Proto.FileArtifact;

namespace BuildXL
{
    /// <summary>
    /// Extension methods to bridge ContentHash over Protobuf byte strings
    /// </summary>
    public static class ContentHashExtensions
    {
        public static ContentHash ToContentHash(this ByteString hashBytes)
        {
            // TODO: Optimize to avoid copy fo bytearray
            // Or consider contributing support for Span<byte> and ReadOnlySpan<byte> support to grpc
            // or add overload to contenthash that takes IEnumerable<byte>
            return new ContentHash(hashBytes.ToByteArray());

        }

        public static ByteString ToByteString(this ContentHash contentHash)
        {
            // TODO Figure out how to use Unsafe.FromBytes to avoid an extra copy
            // Or consider contributing support for Span<byte> and ReadOnlySpan<byte> support to grpc
            return ByteString.CopyFrom(contentHash.ToByteArray());
        }
    }
}
