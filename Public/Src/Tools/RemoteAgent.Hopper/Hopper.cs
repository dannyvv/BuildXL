// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RemoteAgent.Hopper
{
    public class Hopper : IDisposable
    {
        private TextWriter m_log;

        public Hopper()
        {
            m_log = new StreamWriter(@"C:\windows\temp\Agent.Hopper.log");
        }

        public int Hop()
        {
            try
            {
                System.Threading.Thread.Sleep(30_000);
                Log("Starting hopper");

                var workingDir = Environment.GetEnvironmentVariable(Constants.HopperWorkingDirectory);
                var executable = Environment.GetEnvironmentVariable(Constants.HopperCommand);
                var arguments = Environment.GetEnvironmentVariable(Constants.HopperArguments);

                if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
                {
                    Log("No executable specified in environment variables");
                    return 252;
                }

                if (!Directory.Exists(workingDir))
                {
                    Log("No working directory specified in environment variables");
                    return 252;
                }

                var process = new Process()
                {
                    StartInfo =
                    {
                        FileName = executable,
                        Arguments = arguments,
                        WorkingDirectory = workingDir,
                        RedirectStandardOutput = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        RedirectStandardError = true,
                        StandardErrorEncoding = Encoding.UTF8,
                        RedirectStandardInput = true,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    },
                };

                process.OutputDataReceived += ((sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Log("|.|" + e.Data);
                    }
                });

                process.ErrorDataReceived += ((sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Log("|^|" + e.Data);
                    }
                });

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                // TODO: Handle SdtIn

                var exitCode = process.ExitCode;

                Log($"Process finished with exit code {exitCode}");
                return exitCode;
            }
            catch (Exception e)
            {
                Log(e.ToString());
                return 252;
            }
        }


        private void Log(string message)
        {
            var detail = "[RemoteAgent.Hopper] " + message;
            Console.WriteLine(detail);
            m_log.WriteLine(detail);
            Console.WriteLine("STUFFFFFF");
        }

        public void Dispose()
        {
            m_log?.Dispose();
        }
    }
}
