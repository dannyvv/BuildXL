// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BuildXL;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cloud.Proto;
using BuildXL.Native.IO;
using BuildXL.Processes;
using BuildXL.Storage;
using BuildXL.Utilities;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using Grpc.Core;
using CasAbsolutePath = BuildXL.Cache.ContentStore.Interfaces.FileSystem.AbsolutePath;
using CasContext = BuildXL.Cache.ContentStore.Interfaces.Tracing.Context;

namespace RemoteAgent
{
    public class RemoteExecImpl : RemoteExec.RemoteExecBase
    {
        private string m_sandboxRoot;
        private IContentSession m_casSession;
        private ILogger m_casLogger;

        private ISandboxConfiguration m_configuration;

        public RemoteExecImpl(string sandboxRoot, ISandboxConfiguration configuration, IContentSession casSession, ILogger casLogger)
        {
            m_sandboxRoot = sandboxRoot;
            m_casSession = casSession;
            m_casLogger = casLogger;
            m_configuration = configuration;
        }

        public override async Task<ExecProcessResponse> ExecProcess(ExecProcessRequest request, ServerCallContext callContext)
        {
            try
            {
                Console.WriteLine("Executing process");

                var localId = Guid.NewGuid();
                var localSandBox = Path.Combine(m_sandboxRoot, localId.ToString("D"));
                if (FileUtilities.DirectoryExistsNoFollow(localSandBox))
                {
                    throw new InvalidOperationException("Guid collision");
                }

                FileUtilities.CreateDirectoryWithRetry(localSandBox);

                var loggingContext = new LoggingContext(
                    localId,
                    "Cloud",
                    new LoggingContext.SessionInfo(Guid.NewGuid().ToString(), "c3", Guid.NewGuid()));
                var casContext = new CasContext(localId, m_casLogger);

                var context = BuildXLContext.CreateInstanceForTesting();
                var protoContext = new BuildXL.Pips.ProtobufSerializationContext(context.PathTable);
                protoContext.ReceivePathTable(request.PathTable); // First reception of state
                var process = BuildXL.Pips.Operations.Process.FromProto(protoContext, request.Process);


                Console.WriteLine("Materializing files");
                var hashesWithPaths = new List<ContentHashWithPath>(request.InputFiles.Count);
                foreach (var inputFile in request.InputFiles)
                {
                    var clientTargetPath = protoContext.PathFromProto(inputFile.Key).ToString(context.PathTable);
                    // TODO: Mac/Unix file paths support, this code assumes aboslute path for now
                    var sandBoxedPath = Path.Combine(
                        m_sandboxRoot,
                        clientTargetPath[0].ToUpperInvariantFast().ToString(),
                        clientTargetPath.Substring(3));

                    hashesWithPaths.Add(
                        new ContentHashWithPath(
                            inputFile.Value.ToContentHash(),
                            new CasAbsolutePath(sandBoxedPath)
                        )
                    );
                }

                var placeResults = await m_casSession.PlaceFileAsync(
                    casContext,
                    hashesWithPaths,
                    FileAccessMode.ReadOnly,
                    FileReplacementMode.ReplaceExisting,
                    FileRealizationMode.Any,
                    callContext.CancellationToken);
                foreach (var placeResultAsync in placeResults)
                {
                    var placeResult = await placeResultAsync;
                    if (!placeResult.Item.Succeeded)
                    {
                        throw new InvalidOperationException("Failed to place file: " + placeResult.Item.ErrorMessage);
                    }
                }

                Console.WriteLine("Invoking");

                var executor = new SimplePipExecutor(m_configuration, loggingContext, context);
                var result = await executor.ExecuteProcessAsync(process, AbsolutePath.Create(context.PathTable, localSandBox));

                var response = new ExecProcessResponse() {ExitCode = result.ExitCode,};

                // TODO: validate file access against policy
                protoContext.PreparePathTableForWrite();

                // TODO: Consider parallel loop.
                foreach (var access in result.FileAccesses)
                {
                    switch (access.RequestedAccess)
                    {
                        case RequestedAccess.All:
                        case RequestedAccess.Write:
                        case RequestedAccess.ReadWrite:
                            var path = access.ManifestPath;
                            if (!path.IsValid)
                            {
                                path = AbsolutePath.Create(context.PathTable, access.Path);
                            }

                            // TODO: Consider shortcut for expandedpath not going through pathtable.
                            var expandedPath = path.Expand(context.PathTable);

                            var putFileResult = await m_casSession.PutFileAsync(
                                casContext,
                                ContentHashingUtilities.HashInfo.HashType,
                                new CasAbsolutePath(expandedPath.ExpandedPath),
                                FileRealizationMode.Any,
                                callContext.CancellationToken);
                            if (!putFileResult.Succeeded)
                            {
                                throw new InvalidOperationException("Failed to put file:" + putFileResult.ErrorMessage);
                            }

                            if (!result.TryGetRewrite(path, out var file))
                            {
                                file = FileArtifact.CreateOutputFile(path);
                            }

                            response.OutputFile.Add(
                                new OutputFile
                                {
                                    // TODO: Handle rewrites
                                    File = protoContext.ToProto(file),
                                    ContentHash = putFileResult.ContentHash.ToByteString(),
                                    Length = putFileResult.ContentSize,
                                });
                            break;
                        // Consider handling other cases
                    }
                }

                response.PathTable = protoContext.SendPathTable();

                return response;
            }
            catch (Exception e)
            {
                Console.WriteLine("EXCEPTION!: " + e);
                    throw e;
            }
        }
    }
}
