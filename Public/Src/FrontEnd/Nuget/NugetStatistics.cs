// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BuildXL.FrontEnd.Workspaces;

#pragma warning disable SA1649 // File name must match first type name

namespace BuildXL.FrontEnd.Nuget
{
    /// <summary>
    /// Captures the statistics for nuget.
    /// </summary>
    public sealed class NugetStatistics
    {
        /// <inheritdoc />
        public Counter EndToEnd { get; } = new Counter();

        /// <inheritdoc />
        public Counter PackagesFromDisk { get; } = new Counter();

        /// <inheritdoc />
        public Counter Failures { get; } = new Counter();

        /// <inheritdoc />
        public Counter PackagesFromCache { get; } = new Counter();

        /// <inheritdoc />
        public Counter PackagesFromNuget { get; } = new Counter();

        /// <inheritdoc />
        public Counter SpecGeneration { get; } = new Counter();
    }
}
