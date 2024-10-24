﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using BuildXL.Utilities.Core;

namespace BuildXL.Processes
{
    /// <summary>
    /// Configuration for shim execution in place of child processes executing in a build sandbox.
    /// </summary>
    public sealed class SubstituteProcessExecutionInfo
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        public SubstituteProcessExecutionInfo(AbsolutePath substituteProcessExecutionShimPath, bool shimAllProcesses, IReadOnlyCollection<ShimProcessMatch> processMatches)
        {
            Contract.Requires(substituteProcessExecutionShimPath.IsValid);

            SubstituteProcessExecutionShimPath = substituteProcessExecutionShimPath;
            ShimAllProcesses = shimAllProcesses;
            ShimProcessMatches = processMatches ?? Array.Empty<ShimProcessMatch>();
        }


        /// <summary>
        /// Path to substitute shim process.
        /// </summary>
        /// <remarks>
        /// Children of the Detoured process are not directly executed, but instead this substitute shim process is injected, with
        /// the original process's command line appended, and with the original process's environment and working directory.
        /// </remarks>
        public AbsolutePath SubstituteProcessExecutionShimPath { get; set; }

        /// <summary>
        /// Path to an unmanaged 32-bit plugin DLL to load and call when determining whether to run child processes directly
        /// or to inject the substitute process specified in <see cref="SubstituteProcessExecutionShimPath"/>.
        /// </summary>
        /// <remarks>
        /// This DLL must implement a <code>CommandMatches</code> method; see <code>SubstituteProcessExecutionPluginFunc</code>
        /// in Public\Src\Sandbox\Windows\DetoursServices\globals.h.
        /// </remarks>
        public AbsolutePath SubstituteProcessExecutionPluginDll32Path { get; set; }

        /// <summary>
        /// Path to an unmanaged 64-bit plugin DLL to load and call when determining whether to run child processes directly
        /// or to inject the substitute process specified in <see cref="SubstituteProcessExecutionShimPath"/>.
        /// </summary>
        /// <remarks>
        /// This DLL must implement a <code>CommandMatches</code> method; see <code>SubstituteProcessExecutionPluginFunc</code>
        /// in Public\Src\Sandbox\Windows\DetoursServices\globals.h.
        /// </remarks>
        public AbsolutePath SubstituteProcessExecutionPluginDll64Path { get; set; }
        
        /// <summary>
        /// Specifies the shim injection mode. When true, <see cref="ShimProcessMatches"/>
        /// specifies the processes that should not be shimmed. When false, <see cref="ShimProcessMatches"/>
        /// specifies the processes that should be shimmed.
        /// </summary>
        public bool ShimAllProcesses { get; }

        /// <summary>
        /// Processes that should or should not be be shimmed, according to the shim injection mode specified
        /// by <see cref="ShimAllProcesses"/>.
        /// </summary>
        public IReadOnlyCollection<ShimProcessMatch> ShimProcessMatches { get; }
    }

    /// <summary>
    /// Process matching information used for including or excluding processes in <see cref="SubstituteProcessExecutionInfo"/>.
    /// </summary>
    /// <remarks>In unmanaged code this is decoded into class ShimProcessMatch in DetoursHelpers.cpp.</remarks>
    public sealed class ShimProcessMatch
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        public ShimProcessMatch(PathAtom processName, PathAtom argumentMatch)
        {
            Contract.Requires(processName.IsValid);

            ProcessName = processName;
            ArgumentMatch = argumentMatch;
        }

        /// <summary>
        /// A process name to match, including extension. E.g. "MSBuild.exe".
        /// </summary>
        public PathAtom ProcessName { get; }

        /// <summary>
        /// An optional string to match in the arguments of the process to further refine the match.
        /// Can be used for example with ProcessName="node.exe" ArgumentMatch="gulp.js" to match the
        /// Gulp build engine process.
        /// </summary>
        public PathAtom ArgumentMatch { get; }
    }
}
