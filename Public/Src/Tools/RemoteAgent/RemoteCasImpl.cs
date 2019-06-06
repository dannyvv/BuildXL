// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BuildXL;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Service.Grpc;
using BuildXL.Cloud.Proto;
using BuildXL.Native.IO;
using Grpc.Core;

namespace RemoteAgent
{
    public class RemoteCasImpl : RemoteCas.RemoteCasBase
    {
        private string m_tempFileRoot;
        private IContentSession m_casSession;
        private ILogger m_logger;

        public RemoteCasImpl(string tempFileRoot, IContentSession casSession, ILogger logger)
        {
            m_tempFileRoot = tempFileRoot;
            m_casSession = casSession;
            m_logger = logger;

            FileUtilities.CreateDirectoryWithRetry(tempFileRoot);

        }

        public override async Task<PinBulkResponse> PinBulk(PinBulkRequest request, ServerCallContext context)
        {
            Console.WriteLine($"Bulk Pin Request: {request.ContentHashes.Count}");


            var startTime = DateTime.UtcNow;
            var cacheContext = new BuildXL.Cache.ContentStore.Interfaces.Tracing.Context(new Guid(request.Header.TraceId), m_logger);

            var pinList = new List<ContentHash>();
            foreach (var hash in request.ContentHashes)
            {
                pinList.Add(hash.ToContentHash());
            }

            List<Task<Indexed<PinResult>>> pinResults = (await m_casSession.PinAsync(
                cacheContext,
                pinList,
                context.CancellationToken)).ToList();

            var response = new PinBulkResponse();
            try
            {
                foreach (var pinResult in pinResults)
                {
                    var result = await pinResult;
                    var item = result.Item;
                    response.Header.Add(
                        result.Index,
                        item.ToHeader(startTime, (int)item.Code));
                }
            }
            catch (Exception)
            {
                pinResults.ForEach(task => task.FireAndForget(cacheContext));
                throw;
            }

            return response;
        }

        public override async Task<StoreFileResponse> StoreFile(IAsyncStreamReader<StoreFileRequest> requestStream, ServerCallContext context)
        {
            BuildXL.Cache.ContentStore.Interfaces.Tracing.Context cacheContext = null;
            Stream stream = null;
            string path = Path.Combine(m_tempFileRoot, Guid.NewGuid().ToString("D"));
            ContentHash contentHash = default(ContentHash);

            var startTime = DateTime.UtcNow;

            try
            {
                while (await requestStream.MoveNext().ConfigureAwait(false))
                {
                    var storeFileRequest = requestStream.Current;
                    if (stream == null)
                    {
                        cacheContext = new BuildXL.Cache.ContentStore.Interfaces.Tracing.Context(new Guid(storeFileRequest.Header.TraceId), m_logger);

                        contentHash = storeFileRequest.ContentHash.ToContentHash();

                        Console.WriteLine($"Storing file: {path} for {storeFileRequest.Path}");

                        stream = FileUtilities.CreateAsyncFileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, allowExcludeFileShareDelete: true);
                    }

                    storeFileRequest.FileContent.Content.WriteTo(stream);
                }

                await stream.FlushAsync();
                stream.Close();
                var putResult = await m_casSession.PutFileAsync(cacheContext, contentHash, new AbsolutePath(path), FileRealizationMode.Any, context.CancellationToken);

                // For diagnostics not cleaning
                //FileUtilities.DeleteFile(path);

                return new StoreFileResponse()
                {
                    Header = putResult.ToHeader(startTime)
                };
            }
            finally
            {
                stream?.Dispose();
            }
        }
    }
    internal static class ProtoExtension
    {
        public static ResponseHeader ToHeader(this ResultBase result, DateTime receiptTime, int? resultCode = null)
        {
            var header = new ResponseHeader()
            {
                Succeeded = result.Succeeded,
                Result = resultCode ?? (result.Succeeded ? 0 : 1),
                ServerReceiptTimeUtcTicks = receiptTime.Ticks,
            };
            if (!string.IsNullOrEmpty(result.Diagnostics))
            {
                header.Diagnostics = result.Diagnostics;
            }
            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                header.ErrorMessage = result.ErrorMessage;
            }

            return header;
        }
    }
}
