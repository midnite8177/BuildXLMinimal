﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.ContractsLight;
using BuildXL.Utilities.Core;

namespace BuildXL.Processes
{
    /// <summary>
    /// Stdout and stderr stream redirection to files or trace file, potentially created by the sandboxed process.
    /// </summary>
    public class SandboxedProcessStandardFiles
    {
        /// <summary>
        /// Standard output redirected file path.
        /// </summary>
        public string StandardOutput { get; }

        /// <summary>
        /// Standard error redirected file path.
        /// </summary>
        public string StandardError { get; }

        /// <summary>
        /// Sandbox trace file.
        /// </summary>
        public string Trace { get; }

        /// <summary>
        /// Creates an instance of <see cref="SandboxedProcessFile"/>.
        /// </summary>
        public SandboxedProcessStandardFiles(string standardOutput, string standardError, string trace)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(standardOutput));
            Contract.Requires(!string.IsNullOrWhiteSpace(standardError));

            StandardOutput = standardOutput;
            StandardError = standardError;
            Trace = trace;
        }

        /// <summary>
        /// Serializes this instance into the given <paramref name="writer"/>.
        /// </summary>
        public void Serialize(BuildXLWriter writer)
        {
            Contract.Requires(writer != null);

            writer.Write(StandardOutput);
            writer.Write(StandardError);
            writer.WriteNullableString(Trace);
        }

        /// <summary>
        /// Serializes an empty instance into the given <paramref name="writer"/>.
        /// </summary>
        public static void SerializeEmpty(BuildXLWriter writer)
        {
            Contract.Requires(writer != null);

            writer.Write(string.Empty);  // StandardOutput
            writer.Write(string.Empty);  // StandardError
            writer.Write(string.Empty);  // SandboxTrace
        }

        /// <summary>
        /// Deserializes an instance of <see cref="SandboxedProcessStandardFiles"/>.
        /// </summary>
        public static SandboxedProcessStandardFiles Deserialize(BuildXLReader reader)
        {
            Contract.Requires(reader != null);

            string output = reader.ReadString();
            string error = reader.ReadString();
            string trace = reader.ReadNullableString();

            if (string.IsNullOrEmpty(output))
            {
                return null;
            }

            return new SandboxedProcessStandardFiles(output, error, trace);
        }

        /// <summary>
        /// Creates an instance of <see cref="SandboxedProcessStandardFiles"/> from <see cref="ISandboxedProcessFileStorage"/>.
        /// </summary>
        public static SandboxedProcessStandardFiles From(ISandboxedProcessFileStorage fileStorage) => 
            new SandboxedProcessStandardFiles(
                fileStorage.GetFileName(SandboxedProcessFile.StandardOutput), 
                fileStorage.GetFileName(SandboxedProcessFile.StandardError),
                fileStorage.GetFileName(SandboxedProcessFile.Trace));
    }
}
