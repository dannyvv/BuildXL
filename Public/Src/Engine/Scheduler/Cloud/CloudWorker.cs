// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Cloud.Proto;
using BuildXL.Native.IO;
using BuildXL.Pips;
using BuildXL.Pips.Operations;
using BuildXL.Processes;
using BuildXL.Scheduler.Artifacts;
using BuildXL.Storage;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Tasks;
using Google.Protobuf;
using Grpc.Core;

namespace BuildXL.Scheduler.Distribution
{
    /// <summary>
    /// Worker that schedules the work to a cloud based endpoint
    /// </summary>
    public class CloudWorker : Worker
    {
        private RemoteCas.RemoteCasClient m_casClient;
        private RemoteExec.RemoteExecClient m_execClient;
        private Pool<byte[]> m_bufferPool = new Pool<byte[]>(() => new byte[1024 * 1024]);

        /// <nodoc />
        public CloudWorker(
            uint workerId,
            string name,
            IReadOnlyList<string> endPoints,
            FileContentManager fileContentManager,
            ITempDirectoryCleaner tempCleaner)
            : base(workerId, name)
        {

            // TODO: Round-robin and secure
            var casChannel = new Channel("dannyvv-a1903", 2233, ChannelCredentials.Insecure);
            m_casClient = new RemoteCas.RemoteCasClient(casChannel);
            var remoteChannel = new Channel("dannyvv-a1903", 2233, ChannelCredentials.Insecure);
            m_execClient = new RemoteExec.RemoteExecClient(remoteChannel);

            // m_fileContentManager = fileContentManager;
            // m_tempDirectoryCleaner = tempCleaner;
            // m_endPoints = endPoints;
            // m_client = AnyBuildClientFactory.CreateClient(
            //     Guid.NewGuid(),
            //     m_fileContentManager.LocalDiskContentStore.FileContentTable,
            //     endPoints.Select(e => new AgentInfo(e, 11337))
            // );
        }

        /// <inheritdoc />
        public override async Task<PipResultStatus> MaterializeInputsAsync(RunnablePip runnablePip)
        {
            Contract.Assert(runnablePip.PipType == PipType.Process, "We should only remote process pips");

            using (OnPipExecutionStarted(runnablePip))
            {
                var process = (Process)runnablePip.Pip;
                var fileContentManager = runnablePip.Environment.State.FileContentManager;
                var ct = runnablePip.Environment.Context.CancellationToken;
                // Ensure all hashes are registered.
                fileContentManager.RegisterDirectoryDependencies(process);

                var files = new List<(AbsolutePath, FileContentInfo)>();
                var pinRequest = new PinBulkRequest()
                {
                    Header = new RequestHeader() { TraceId = runnablePip.LoggingContext.Session.Id, SessionId = 1, }
                };

                foreach (var file in process.Dependencies)
                {
                    addFile(file);
                }

                foreach (var directory in process.DirectoryDependencies)
                {
                    // Sealed directories have multiple files in them, that the task may use. Preemptively send the files over
                    foreach (var file in fileContentManager.ListSealedDirectoryContents(directory))
                    {
                        addFile(file);
                    }
                }

                var pinResult = await m_casClient.PinBulkAsync(pinRequest);

                foreach (var header in pinResult.Header)
                {
                    switch ((PinResult.ResultCode)header.Value.Result)
                    {
                        case PinResult.ResultCode.Error:
                            throw new InvalidOperationException("Error pinning: " + header.Value.ErrorMessage);
                        case PinResult.ResultCode.Success:
                            // Cool pinned
                            break;
                        case PinResult.ResultCode.ContentNotFound:
                            // have to upload
                            var (filePath, fileContentInfo) = files[header.Key];
                            var path = filePath.ToString(runnablePip.Environment.Context.PathTable);
                            await StoreFileToAgent(
                                runnablePip.LoggingContext,
                                path,
                                fileContentInfo,
                                ct);
                            break;
                    }
                }

                return PipResultStatus.Succeeded;

                void addFile(FileArtifact file)
                {
                    if (!fileContentManager.TryGetInputContent(file, out var fileMaterializationInfo))
                    {
                        throw new InvalidOperationException("TODO: Handle no input content");
                    }

                    var fileContentInfo = fileMaterializationInfo.FileContentInfo;
                    if (fileContentInfo.Hash == WellKnownContentHashes.UntrackedFile)
                    {
                        throw new InvalidOperationException("TODO: Handle untracked file");
                    }

                    files.Add((file.Path, fileContentInfo));
                    pinRequest.ContentHashes.Add(fileContentInfo.Hash.ToByteString());
                }
            }
        }

        public override async Task<ExecutionResult> ExecuteProcessAsync(ProcessRunnablePip processRunnable)
        {
            using (var scope = OnPipExecutionStarted(processRunnable))
            {
                var fileContentManager = processRunnable.Environment.State.FileContentManager;

                //CurrentlyExecutingPips.TryAdd(processRunnable.PipId, Unit.Void);

                var context = new ProtobufSerializationContext(processRunnable.Environment.Context.PathTable);
                var process = processRunnable.Process;
                var processProto = Process.ToProto(context, processRunnable.Process);
                var request = new ExecProcessRequest()
                {
                    Process = processProto,
                };

                foreach (var file in process.Dependencies)
                {
                    var hash = fileContentManager.GetInputContent(file).FileContentInfo.Hash;
                    request.InputFiles.Add(context.AddPath(file.Path), hash.ToByteString());
                }

                foreach (var inputDirectory in process.DirectoryDependencies)
                {
                    // Sealed directories have multiple files in them, that the task may use. Preemptively send the files over
                    foreach (var file in fileContentManager.ListSealedDirectoryContents(inputDirectory))
                    {
                        // The sealed directories have dynamic files
                        context.AddPath(file.Path);

                        var hash = fileContentManager.GetInputContent(file).FileContentInfo.Hash;
                        request.InputFiles.Add(context.AddPath(file.Path), hash.ToByteString());
                    }
                }

                request.PathTable = context.SendPathTable();


                var reply = await m_execClient.ExecProcessAsync(request);

                context.ReceivePathTable(reply.PathTable);

                //// TODO: Handle failure
                //// TODO: Pass perf stats

                ToOutputs(context, processRunnable, reply, out var outputContent, out var directoryOutputs);

                var executionResult = ExecutionResult.CreateSealed(
                    result: PipResultStatus.NotMaterialized, // TODO: Handle failure, but on success we don't want to pull down the files when not needed
                    numberOfWarnings: 0,

                    outputContent: outputContent,
                    directoryOutputs: directoryOutputs,

                    performanceInformation: new ProcessPipExecutionPerformance(
                        level: PipExecutionLevel.Executed,
                        executionStart: DateTime.UtcNow,
                        executionStop: DateTime.UtcNow,
                        fingerprint: Fingerprint.Random(),
                        processExecutionTime: TimeSpan.Zero,
                        fileMonitoringViolations: new FileMonitoringViolationCounters(0, 0, 0),
                        ioCounters: new IOCounters(),
                        userTime: TimeSpan.Zero,
                        kernelTime: TimeSpan.Zero,
                        peakMemoryUsage: 0,
                        numberOfProcesses: 0,
                        workerId: 0),
                    fingerprint: null,
                    fileAccessViolationsNotWhitelisted: new ReportedFileAccess[0],
                    whitelistedFileAccessViolations: new ReportedFileAccess[0],
                    mustBeConsideredPerpetuallyDirty: true,
                    dynamicallyObservedFiles: ReadOnlyArray<AbsolutePath>.Empty,
                    dynamicallyObservedEnumerations: ReadOnlyArray<AbsolutePath>.Empty,
                    allowedUndeclaredSourceReads: CollectionUtilities.EmptySet<AbsolutePath>(),
                    absentPathProbesUnderOutputDirectories: CollectionUtilities.EmptySet<AbsolutePath>(),
                    twoPhaseCachingInfo: null,
                    pipCacheDescriptorV2Metadata: null,
                    converged: false,
                    pathSet: null,
                    cacheLookupStepDurations: null);
                processRunnable.SetExecutionResult(executionResult);

                return executionResult;
            }
        }
        
        private void ToOutputs(
            ProtobufSerializationContext context,
            ProcessRunnablePip processRunnable,
            ExecProcessResponse reply,
            out ReadOnlyArray<(FileArtifact, FileMaterializationInfo, PipOutputOrigin)> fileOutputs,
            out ReadOnlyArray<(DirectoryArtifact, ReadOnlyArray<FileArtifact>)> directoryOutputs)
        {
            var fileContentManager = processRunnable.Environment.State.FileContentManager;
            var outFiles = reply.OutputFile;

            var result = new (FileArtifact, FileMaterializationInfo, PipOutputOrigin)[outFiles.Count];

            for (int i = 0; i < outFiles.Count; i++)
            {
                var outFile = outFiles[i];

                var file = context.FromProto(outFile.File);
                // $TODO: DO proper effecient contenthash
                var contentHash = outFile.ContentHash.ToContentHash();
                var fileMaterializationInfo = new FileMaterializationInfo(
                    new FileContentInfo(contentHash, outFile.Length),
                    file.Path.GetName(context.PathTable)
                );
                result[i] = (
                    file,
                    fileMaterializationInfo,
                    PipOutputOrigin.NotMaterialized
                );

                // Just pass through the hash
                fileContentManager.ReportOutputContent(
                    processRunnable.OperationContext,
                    "TODO: PipDescription",
                    file,
                    fileMaterializationInfo,
                    PipOutputOrigin.NotMaterialized);
            }

            fileOutputs = ReadOnlyArray<(FileArtifact, FileMaterializationInfo, PipOutputOrigin)>.FromWithoutCopy(result);
        }

        private async Task StoreFileToAgent(LoggingContext loggingContext, string path, FileContentInfo fileContentInfo, CancellationToken ct)
        {
            var storeFileRequest = new StoreFileRequest
            {
                Header = loggingContext.ToHeader(),
                ContentHash = fileContentInfo.Hash.ToByteString(),
                Path = path,
            };
            var streamingCall = m_casClient.StoreFile();

            if (!fileContentInfo.HasKnownLength)
            {
                throw new InvalidOperationException("Don't know how to handle files without size yet");
            }

            using (var fs = FileUtilities.CreateAsyncFileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var bufferWrapper = m_bufferPool.Get())
            {
                var buffer = bufferWrapper.Value;

                int chunks = 0;
                long bytes = 0L;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    int chunkSize = await fs.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (chunkSize == 0) { break; }

                    storeFileRequest.FileContent = new Chunk
                    {
                        ChunkId = chunks,
                        Content = ByteString.CopyFrom(buffer, 0, chunkSize)
                    };

                    await streamingCall.RequestStream.WriteAsync(storeFileRequest);

                    bytes += chunkSize;
                    chunks++;
                }

                await streamingCall.RequestStream.CompleteAsync();

                var response = await streamingCall.ResponseAsync;
                if (!response.Header.Succeeded)
                {
                    throw new InvalidOperationException("Failed to store file: " + response.Header.ErrorMessage);
                }

            }
        }
    }


    internal static class ProtoExtension
    {
        public static RequestHeader ToHeader(this LoggingContext loggingContext)
        {
            return new RequestHeader()
            {
                TraceId = loggingContext.Session.Id,
                SessionId = 1,
            };
        }
    }
}
