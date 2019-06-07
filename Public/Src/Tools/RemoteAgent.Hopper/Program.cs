// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace RemoteAgent.Hopper
{
    public class Program
    {
        /// <nodoc />
        public static int Main(string[] args)
        {
            try
            {
                using (var hopper = new Hopper())
                {
                    var result = hopper.Hop();
                    return result;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return 151;
            }
        }
    }
}
