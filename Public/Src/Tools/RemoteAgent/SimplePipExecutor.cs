using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Pips.Operations;
using BuildXL.Processes;
using BuildXL.Processes.Containers;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;

namespace RemoteAgent
{
    public class SimplePipExecutor
    {
        private ISandboxConfiguration m_sandBoxConfig;
        private PipExecutionContext m_context;
        private LoggingContext m_loggingContext;

        public SimplePipExecutor(ISandboxConfiguration sandBoxConfig, LoggingContext loggingContext, PipExecutionContext context)
        {
            m_sandBoxConfig = sandBoxConfig;
            m_context = context;
            m_loggingContext = loggingContext;
        }

        public async Task<SimplePipExecutorResult> ExecuteProcessAsync(Process process, AbsolutePath sandBoxRoot)
        {
            var fileAccessManifest =
                new FileAccessManifest(m_context.PathTable, translateDirectories: null) // TODO
                {
                    MonitorNtCreateFile = m_sandBoxConfig.UnsafeSandboxConfiguration.MonitorNtCreateFile,
                    MonitorZwCreateOpenQueryFile = m_sandBoxConfig.UnsafeSandboxConfiguration.MonitorZwCreateOpenQueryFile,
                    ForceReadOnlyForRequestedReadWrite = m_sandBoxConfig.ForceReadOnlyForRequestedReadWrite,
                    IgnoreReparsePoints = m_sandBoxConfig.UnsafeSandboxConfiguration.IgnoreReparsePoints,
                    IgnorePreloadedDlls = m_sandBoxConfig.UnsafeSandboxConfiguration.IgnorePreloadedDlls,
                    IgnoreZwRenameFileInformation = m_sandBoxConfig.UnsafeSandboxConfiguration.IgnoreZwRenameFileInformation,
                    IgnoreZwOtherFileInformation = m_sandBoxConfig.UnsafeSandboxConfiguration.IgnoreZwOtherFileInformation,
                    IgnoreNonCreateFileReparsePoints = m_sandBoxConfig.UnsafeSandboxConfiguration.IgnoreNonCreateFileReparsePoints,
                    IgnoreSetFileInformationByHandle = m_sandBoxConfig.UnsafeSandboxConfiguration.IgnoreSetFileInformationByHandle,
                    NormalizeReadTimestamps =
                        m_sandBoxConfig.NormalizeReadTimestamps &&
                        // Do not normalize read timestamps if preserved-output mode is enabled and pip wants its outputs to be preserved.
                        (m_sandBoxConfig.UnsafeSandboxConfiguration.PreserveOutputs == PreserveOutputsMode.Disabled || !process.AllowPreserveOutputs),
                    UseLargeNtClosePreallocatedList = m_sandBoxConfig.UseLargeNtClosePreallocatedList,
                    UseExtraThreadToDrainNtClose = m_sandBoxConfig.UseExtraThreadToDrainNtClose,
                    DisableDetours = m_sandBoxConfig.UnsafeSandboxConfiguration.DisableDetours(),
                    LogProcessData = m_sandBoxConfig.LogProcesses && m_sandBoxConfig.LogProcessData,
                    IgnoreGetFinalPathNameByHandle = m_sandBoxConfig.UnsafeSandboxConfiguration.IgnoreGetFinalPathNameByHandle,
                    // SemiStableHash is 0 for pips with no provenance;
                    // since multiple pips can have no provenance, SemiStableHash is not always unique across all pips
                    PipId = process.SemiStableHash != 0 ? process.SemiStableHash : process.PipId.Value,
                    QBuildIntegrated = false,

                    ReportFileAccesses = true,
                    ReportUnexpectedFileAccesses = true,
                    // TODO: To keep the agent safe
                    //FailUnexpectedFileAccesses = true;
                };

            var outputIds = new HashSet<AbsolutePath>();
            var rewrites = new Dictionary<AbsolutePath, FileArtifact>();
            var excludeReportAccessMask = ~FileAccessPolicy.ReportAccess;

            // Record policies:
            // TODO: Share logic with SandboxedProcessPipExecutor
            if (process.AllowUndeclaredSourceReads)
            {
                throw new InvalidOperationException("Not supported for remoting");
            }
            // The default policy is to only allow absent file probes. But if undeclared source reads are enabled, we allow any kind of read
            var rootFileAccessPolicy =
                // All directory enumerations are reported unless explicitly excluded
                (FileAccessPolicy.AllowReadIfNonexistent | FileAccessPolicy.ReportDirectoryEnumerationAccess | FileAccessPolicy.ReportAccessIfNonexistent);

            fileAccessManifest.AddScope(
                AbsolutePath.Invalid,
                FileAccessPolicy.MaskNothing,
                rootFileAccessPolicy);

            // Engine
            var executingAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var executingAssemblyDirectory = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(executingAssembly));
            fileAccessManifest.AddScope(
                AbsolutePath.Create(m_context.PathTable, executingAssemblyDirectory),
                FileAccessPolicy.MaskNothing,
                FileAccessPolicy.AllowReadAlways);

            foreach (var fileOutput in process.FileOutputs)
            {
                var output = fileOutput.ToFileArtifact();

                {
                    // We mask 'report' here, since outputs are expected written and should never fail observed-access validation (directory dependency, etc.)
                    // Note that they would perhaps fail otherwise (simplifies the observed access checks in the first place).
                    // We allow the real input timestamps to be seen since if any of these outputs are rewritten, we should block input timestamp faking in favor of output timestamp faking
                    fileAccessManifest.AddPath(
                        output.Path,
                        values: FileAccessPolicy.AllowAll | FileAccessPolicy.ReportAccess | FileAccessPolicy.AllowRealInputTimestamps, // Always report output file accesses, so we can validate that output was produced.
                        mask: excludeReportAccessMask);
                    outputIds.Add(output.Path);

                    // TODO: Decide on create directory policy
                }
            }
            foreach (FileArtifact dependency in process.Dependencies)
            {
                // Outputs have already been added with read-write access. We should not attempt to add a less permissive entry.
                if (!outputIds.Contains(dependency.Path))
                {
                    var path = dependency.Path;
                    fileAccessManifest.AddPath(
                        path,
                        values: FileAccessPolicy.AllowRead | FileAccessPolicy.AllowReadIfNonexistent,
                        mask: excludeReportAccessMask & ~FileAccessPolicy.AllowRealInputTimestamps); // Make sure we fake the input timestamp

                    // TODO: Handle shared opaques
                }
                else
                {
                    // this is a rewrite:
                    rewrites.Add(dependency.Path, dependency.CreateNextWrittenVersion());
                }
            }

            CreateFolders(m_context.PathTable, process);

            // TODO: Handle response file
            // TODO: Handle temp directories

            var pipDataRenderer = new PipFragmentRenderer(m_context.PathTable);

            var executable = process.Executable.Path.ToString(m_context.PathTable);
            var arguments = process.Arguments.ToString(pipDataRenderer);
            var workingDirectory = process.WorkingDirectory.ToString(m_context.PathTable);
            var rootMappings = new Dictionary<string, string>();

            var environmentVariables = BuildParameters.GetFactory().PopulateFromDictionary(
                process.EnvironmentVariables.Select(
                    envVar =>
                    {
                        var key = envVar.Name.ToString(m_context.StringTable);

                        if (envVar.IsPassThrough)
                        {
                            return new KeyValuePair<string,string>(key, Environment.GetEnvironmentVariable(key));
                        }

                        var value = envVar.Value.ToString(pipDataRenderer);
                        return new KeyValuePair<string, string>(key, value);
                    })
            );

            var containerConfiguration = ContainerConfiguration.DisabledIsolation;
            if (sandBoxRoot.IsValid)
            {
                var redirectedDirectories = new MultiValueDictionary<ExpandedAbsolutePath, ExpandedAbsolutePath>();
                var originalDirectories = new MultiValueDictionary<AbsolutePath, ExpandedAbsolutePath>();
                for (var drive = 'B'; drive <= 'F'; drive++)
                {
                    var originalPathExpanded = new ExpandedAbsolutePath(
                        AbsolutePath.Create(m_context.PathTable, drive + @":\"),
                        m_context.PathTable);
                    var targetPath = sandBoxRoot.Combine(m_context.PathTable, drive.ToString());
                    var targetPathExpanded = new ExpandedAbsolutePath(
                        targetPath,
                        m_context.PathTable);

                    redirectedDirectories.Add(originalPathExpanded, targetPathExpanded);
                    originalDirectories.Add(targetPath, originalPathExpanded);
                }

                containerConfiguration = new ContainerConfiguration(
                    m_context.PathTable,
                    redirectedDirectories,
                    originalDirectories);
            }

            var info =
              new SandboxedProcessInfo(
                  m_context.PathTable,
                  new SimpleSandboxProcessFileStorage(workingDirectory),
                  process.Executable.Path.ToString(m_context.PathTable),
                  fileAccessManifest,
                  true, //m_disableConHostSharing,
                  containerConfiguration,
                  process.TestRetries,
                  m_loggingContext,
                  sandboxedKextConnection: null)
              {
                  Arguments = arguments,
                  WorkingDirectory = workingDirectory,
                  StandardInputReader = null, // TODO
                  StandardInputEncoding = CharUtilities.Utf8NoBomNoThrow,

                  // MaxLengthInMemory, TODO: We could let the Process Pip configure this
                  // BufferSize, TODO: We could let the Process Pip configure this
                  StandardErrorObserver = null, // TODO:
                  StandardOutputObserver = null, // TODO:
                  RootMappings = rootMappings,
                  EnvironmentVariables = environmentVariables,
                  Timeout = process.Timeout ?? TimeSpan.FromMinutes(10), // TOOD
                  PipSemiStableHash = process.SemiStableHash,
                  PipDescription = process.GetDescription(m_context),
                  ProcessIdListener = null,
                  TimeoutDumpDirectory = process.UniqueOutputDirectory.ToString(m_context.PathTable),
                  SandboxKind = SandboxKind.WinDetours,
                  AllowedSurvivingChildProcessNames = new string[0], // TODO: Block this: process.AllowedSurvivingChildProcessNames.Select(n => n.ToString(m_pathTable.StringTable)).ToArray(),
                  NestedProcessTerminationTimeout = process.NestedProcessTerminationTimeout ?? SandboxedProcessInfo.DefaultNestedProcessTerminationTimeout,
              };

            var sandboxexProcess = await SandboxedProcess.StartAsync(info);
            var result = await sandboxexProcess.GetResultAsync();
            var lastMessageCount = sandboxexProcess.GetLastMessageCount();

            return new SimplePipExecutorResult(result, rewrites);
        }

        private static void CreateFolders(PathTable pathTable, Process process)
        {
            Directory.CreateDirectory(process.WorkingDirectory.ToString(pathTable));

            var foldersToCreate = Enumerable.Union(
                    process.FileOutputs.Select(f => f.Path.GetParent(pathTable)),
                    process.DirectoryOutputs.Select(d => d.Path)
                )
                .Distinct()
                .Select(d => d.ToString(pathTable));

            foreach (var folderToCreate in foldersToCreate)
            {
                Directory.CreateDirectory(folderToCreate);
            }
        }
    }

}
