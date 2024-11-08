// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Xml.Linq;

namespace BuildXL.Utilities.CodeGenerationHelper
{
    /// <summary>
    /// Utility to generate C# source code.
    /// </summary>
    /// <remarks>
    /// This was lifted and scaled down from the BuildXL codebase.
    /// </remarks>
    public sealed partial class CodeGenerator
    {
        private static readonly string s_applicationName = CollectApplicationName();
        private static readonly string s_applicationVersion = CollectionApplicationVersion();

        /// <summary>
        /// Gets the application name used by code generators
        /// </summary>
        public static string ApplicationName
        {
            get
            {
                return s_applicationName;
            }
        }

        /// <summary>
        /// Gets the application version used by code generators
        /// </summary>
        public static string ApplicationVersion
        {
            get
            {
                return s_applicationVersion;
            }
        }

        /// <summary>
        /// Creates a Generator object for emitting C# source code
        /// </summary>
        /// <param name="output">Where to send generated output.</param>
        public CodeGenerator(Action<char> output)
        {
            Contract.Requires(output != null);

            m_output = output;
            m_debracer = new Debracer(this);
            m_deindenter = new Deindenter(this);
        }

        /// <summary>
        /// Generates the header.
        /// </summary>
        /// <param name="signature">Signature in the header.</param>
        public void GenHeader(string signature)
        {
            Ln("// Copyright (c) Microsoft Corporation. All rights reserved.");
            Ln();
            Ln("// *****************************************************************************");
            Ln("// <auto-generated>");
            Ln("//     {0}", signature);
            Ln("//     This code was generated by {0} version {1}", s_applicationName, s_applicationVersion);
            Ln("//");
            Ln("//     Changes to this file may cause incorrect behavior and will be lost if");
            Ln("//     the code is regenerated");
            Ln("// </auto-generated>");
            Ln("// *****************************************************************************");
            Ln();
        }

        /// <summary>
        /// Generates the attributes for generated
        /// </summary>
        public void WriteGeneratedAttribute(bool includeCodeCoverageExclusion = true)
        {
            Ln(@"[System.CodeDom.Compiler.GeneratedCode(""{0}"", ""{1}"")]", s_applicationName, s_applicationVersion);
            if (includeCodeCoverageExclusion)
            {
                Ln(@"[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]");
            }
        }

        /// <summary>
        /// Generates the attributes for notBrowsable
        /// </summary>
        public void WriteNotBrowsableAttribute()
        {
            Ln("[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]");
        }

        /// <summary>
        /// Generates comments for methods.
        /// </summary>
        /// <param name="summary">The summary of method description.</param>
        /// <param name="parameters">Mapping from method parameter names to their descriptions.</param>
        /// <param name="returnComment">The comment on method return value.</param>
        public void GenerateMethodComment(string summary, Dictionary<string, string> parameters, string returnComment)
        {
            GenerateSummaryComment(summary);
            if (parameters != null)
            {
                foreach (var nameComment in parameters)
                {
                    Ln("/// <param name=\"{0}\">{1}.</param>", nameComment.Key, nameComment.Value);
                }
            }

            if (!string.IsNullOrEmpty(returnComment))
            {
                Ln("/// <returns>{0}.</returns>", returnComment);
            }
        }

        /// <summary>
        /// Wraps a summary in a comment summary tag.
        /// </summary>
        /// <param name="summary">The summary description.</param>
        /// <param name="args">Format arguments</param>
        public void GenerateSummaryComment(string summary, params object[] args)
        {
            GenerateSummaryComment(string.Format(CultureInfo.InvariantCulture, summary, args));
        }

        /// <summary>
        /// Wraps a summary in a comment summary tag.
        /// </summary>
        /// <param name="summary">The summary description.</param>
        public void GenerateSummaryComment(string summary)
        {
            if (string.IsNullOrEmpty(summary))
            {
                Ln("/// <summary/>");
            }
            else
            {
                Ln("/// <summary>");
                NormalizeAndWriteXmlComment(summary);
                Ln("/// </summary>");
            }
        }

        /// <summary>
        /// Generates a 'nodoc' comment
        /// </summary>
        public void GenerateNoDoc()
        {
            Ln("/// <nodoc />");
        }

        /// <summary>
        /// Generates an 'inheritdoc' comment
        /// </summary>
        public void GenerateInheritDoc()
        {
            Ln("/// <inheritdoc />");
        }

        /// <summary>
        /// Normalizes and writes the XML comments.
        /// </summary>
        /// <param name="xmlComment">The XML comment needs to be written.</param>
        public void NormalizeAndWriteXmlComment(string xmlComment)
        {
            NormalizeAndWriteComment(new XText(xmlComment).ToString(), "///");
        }

        /// <summary>
        /// Normalizes and writes multi line comments.
        /// </summary>
        /// <param name="comment">The comment needs to be written.</param>
        public void NormalizeAndWriteMultilineComment(string comment)
        {
            NormalizeAndWriteComment(comment, "//");
        }

        private void NormalizeAndWriteComment(string comment, string commentSymbol)
        {
            Contract.Assume(comment != null);

            foreach (string line in SplitIntoNormalizedLines(comment))
            {
                Ln("{0} {1}", commentSymbol, line);
            }
        }

        #region Helpers for cleaning XmlComments

        /// <summary>
        /// Splits a string into lines normalized by trimming off leading spaces up to the number of spaces in the last
        /// line of the string.
        /// </summary>
        /// <remarks>
        /// For example this:
        /// first line
        /// second line
        /// third line
        /// would yield this:
        /// first line
        /// second line
        /// third line
        /// </remarks>
        private static IEnumerable<string> SplitIntoNormalizedLines(string unnormalized)
        {
            string[] split = unnormalized.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            if (split.Length > 0)
            {
                int leadingSpaces = GetLeadingSpacesCount(split[split.Length - 1]);
                foreach (string line in split)
                {
                    yield return TrimLeadingSpaces(line, leadingSpaces);
                }
            }
        }

        private static int GetLeadingSpacesCount(string input)
        {
            int leadingSpaces = 0;
            foreach (char c in input)
            {
                if (c == ' ')
                {
                    leadingSpaces++;
                }
                else
                {
                    break;
                }
            }

            return leadingSpaces;
        }

        private static string TrimLeadingSpaces(string input, int upToCount)
        {
            Contract.Requires(input != null);
            Contract.Requires(upToCount >= 0);

            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == ' ' && i < upToCount)
                {
                    continue;
                }

                return input.Substring(i);
            }

            // In the event that a doc comment containing "///\n" is encountered,
            // upToCount will often be larger than input.Length (e.g. 1)
            // in which case input.Length may be 0
            // Constrain Substring so it cannot touch a negative index
            return input.Substring(Math.Min(upToCount, Math.Max(0, input.Length - 1)));
        }

        #endregion

        private static Assembly GetEntryAssembly()
        {
            return Assembly.GetEntryAssembly() ?? Assembly.GetCallingAssembly();
        }

        private static string CollectApplicationName()
        {
            return Path.GetFileName(AssemblyHelper.GetAssemblyLocation(GetEntryAssembly()));
        }

        private static string CollectionApplicationVersion()
        {
            return FileVersionInfo.GetVersionInfo(AssemblyHelper.GetAssemblyLocation(GetEntryAssembly())).FileVersion;
        }

        /// <summary>
        /// Allows the characters that would have been output by a <see cref="CodeGenerator"/> to be intercepted and
        /// sent somewhere else during the lifetime of the object
        /// </summary>
        private sealed class OutputIntercepter : IDisposable
        {
            private readonly CodeGenerator m_generator;
            private readonly Action<char> m_originalAction;

            public OutputIntercepter(CodeGenerator generator, Action<char> interceptionAction)
            {
                m_generator = generator;
                m_originalAction = generator.m_output;
                m_generator.m_output = interceptionAction;
            }

            public void Dispose()
            {
                m_generator.m_output = m_originalAction;
            }
        }

        /// <summary>
        /// Temporarily intercepts the output and sends it to the specified action during the lifetime of the return value.
        /// </summary>
        public IDisposable InterceptOutput(Action<char> interceptionAction)
        {
            return new OutputIntercepter(this, interceptionAction);
        }
    }
}
