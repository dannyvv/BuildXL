using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Processes;

namespace RemoteAgent
{
    public class SimpleSandboxProcessFileStorage : ISandboxedProcessFileStorage
    {
        private string m_workingDirectory;

        public SimpleSandboxProcessFileStorage(string workingDirectory)
        {
            m_workingDirectory = workingDirectory;
        }

        /// <nodoc />
        public string GetFileName(SandboxedProcessFile file)
        {
            return Path.Combine(m_workingDirectory, file.DefaultFileName());
        }
    }
}
