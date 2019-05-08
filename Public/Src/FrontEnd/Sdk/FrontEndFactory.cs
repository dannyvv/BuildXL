// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace BuildXL.FrontEnd.Sdk
{
    /// <summary>
    /// Factory for the various FrontEnds.
    /// </summary>
    public class FrontEndFactory
    {
        private readonly Dictionary<string, IFrontEnd> m_frontEnds;

        /// <nodoc />
        public FrontEndFactory(string configurationFrontEndKind)
        {
            m_frontEnds = new Dictionary<string, IFrontEnd>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Registers a frontend by type
        /// </summary>
        public void RegisterFrontEnd<T>()
            where T : IFrontEnd, new()
        {
            var frontEnd = new T();

            foreach (var resolverKind in frontEnd.SupportedResolvers)
            {
                // Last registred resolverkind wins
                m_frontEnds[resolverKind] = frontEnd;
            }
        }

        /// <summary>
        /// Attempts to retrieve a frontend by the resolver kind
        /// </summary>
        public bool TryGetFrontEnd(string resolverKind, out IFrontEnd frontEnd)
        {
            return m_frontEnds.TryGetValue(resolverKind, out frontEnd);
        }

        /// <summary>
        /// Lists all supported resoverkinds by the frontends
        /// </summary>
        public IReadOnlyCollection<string> GetSupportedResolverKinds()
        {
            return m_frontEnds.Keys;
        }
    }
}
