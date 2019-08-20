// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using BuildXL.FrontEnd.Script;
using BuildXL.FrontEnd.Script.Evaluator;
using BuildXL.FrontEnd.Script.Tracing;
using BuildXL.FrontEnd.Script.Values;
using BuildXL.FrontEnd.Sdk;
using BuildXL.FrontEnd.Workspaces;
using BuildXL.Utilities.Configuration;

namespace BuildXL.FrontEnd.Nuget
{
    /// <summary>
    /// NuGet resolver frontend
    /// </summary>
    public sealed class NugetFrontEnd : IFrontEnd
    {
        /// <nodoc />
        public const string Name = nameof(NugetFrontEnd);

        private FrontEndContext m_context;
        private FrontEndHost m_host;

        private readonly IDecorator<EvaluationResult> m_evaluationDecorator;
        private SourceFileProcessingQueue<bool> m_sourceFileProcessingQueue;

        /// <nodoc/>
        public NugetFrontEnd(
            IFrontEndStatistics statistics,
            Logger logger = null,
            IDecorator<EvaluationResult> evaluationDecorator = null)
        {
            m_evaluationDecorator = evaluationDecorator;
        }

        /// <inheritdoc />
        public IReadOnlyCollection<string> SupportedResolvers { get; } = new[] { WorkspaceNugetModuleResolver.NugetResolverName };

        /// <inheritdoc />
        public void InitializeFrontEnd(FrontEndHost host, FrontEndContext context, IConfiguration configuration)
        {
            Contract.Requires(host != null);
            Contract.Requires(context != null);
            Contract.Requires(configuration != null);

        }


        public IWorkspaceModuleResolver CreateWorkspaceResolver(string kind)
        {
            return new WorkspaceNugetModuleResolver(m_context.StringTable, null);
        }

        /// <nodoc/>
        public IResolver CreateResolver(string kind)
        {
            Contract.Requires(SupportedResolvers.Contains(kind));
            Contract.Assert(m_sourceFileProcessingQueue != null, "Initialize method should be called to initialize m_sourceFileProcessingQueue.");

            return new NugetResolver(
                m_host,
                m_context,
                m_configuration,
                FrontEndStatistics,
                m_sourceFileProcessingQueue,
                Logger,
                m_evaluationDecorator);
        }

        /// <inheritdoc />
        public void LogStatistics(Dictionary<string, long> statistics)
        {
            // Nuget statistics still go through central system rather than pluggable system.
        }
    }
}
