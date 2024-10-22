// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Reflection;

namespace BuildXL.Utilities
{
    /// <nodoc/>
    public static class AssemblyLoaderHelper
    {
        /// <summary>
        /// Force Newtonsoft.Json to version 11
        /// </summary>
        /// <remarks>
        /// Mostly used by unit tests to match BuildXL appconfig
        /// </remarks>
        public static Assembly Newtonsoft11DomainAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name);
            if (name.Name.Equals("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase))
            {
                name.Version = new Version(11, 0);
                return Assembly.Load(name);
            }

            return null;
        }
    }
}
