// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using BuildXL.Utilities.CodeGenerationHelper;
using Microsoft.CodeAnalysis;

namespace BuildXL.LogGen.Core
{
    /// <summary>
    /// Base class for Logging Generators
    /// </summary>
    public abstract class GeneratorBase
    {
        /// <summary>
        /// Global namespace for the assembly
        /// </summary>
        protected string m_globalNamespace;

        /// <summary>
        /// The target framework the generator should create code for when running.
        /// </summary>
        protected string m_targetFramework;

        /// <summary>
        /// The target runtime the generator should create code for when running.
        /// </summary>
        protected string m_targetRuntime;

        /// <summary>
        /// Code generator
        /// </summary>
        protected CodeGenerator m_codeGenerator;

        /// <summary>
        /// All logging classes
        /// </summary>
        protected IReadOnlyList<LoggingClass> m_loggingClasses;

        /// <summary>
        /// The ErrorReport
        /// </summary>
        protected ErrorReport m_errorReport;

        /// <summary>
        /// Initializes the Generator
        /// </summary>
        public void Initialize(string globalNamespace, string targetFramework, string targetRuntime, CodeGenerator codeGenerator, IReadOnlyList<LoggingClass> loggingClasses, ErrorReport errorReport)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(globalNamespace));
            Contract.Requires(codeGenerator != null);
            Contract.Requires(loggingClasses != null);
            Contract.Requires(errorReport != null);

            m_globalNamespace = globalNamespace;
            m_targetFramework = targetFramework;
            m_targetRuntime = targetRuntime;
            m_codeGenerator = codeGenerator;
            m_loggingClasses = loggingClasses;
            m_errorReport = errorReport;
        }

        /// <summary>
        /// Writes to the body of the log method. This will be called for each <see cref="LoggingSite"/>
        /// </summary>
        public abstract void GenerateLogMethodBody(LoggingSite site, Func<string> getMessageExpression);

        /// <summary>
        /// Allows the generator to create a class to support its generated logging. This will be called once for the
        /// lifetime of the log generator
        /// </summary>
        public virtual void GenerateClass() { }

        /// <summary>
        /// Allows the generator to create a members inside the generated class.
        /// This will be called once for the lifetime of the log generator.
        /// </summary>
        public virtual void GenerateAdditionalLoggerMembers() { }

        /// <summary>
        /// Namespaces the generator consumed with optional condition
        /// </summary>
        public virtual IEnumerable<Tuple<string, string>> ConsumedNamespaces => Enumerable.Empty<Tuple<string, string>>();

        /// <summary>
        /// Returns true if the type is a numeric type that can be cast to a long
        /// </summary>
        public static bool IsCompatibleNumeric(ITypeSymbol item)
        {
            return
                    item.SpecialType == SpecialType.System_Byte ||
                    item.SpecialType == SpecialType.System_SByte ||
                    item.SpecialType == SpecialType.System_Int16 ||
                    item.SpecialType == SpecialType.System_Int32 ||
                    item.SpecialType == SpecialType.System_Int64 ||
                    item.SpecialType == SpecialType.System_UInt16 ||
                    item.SpecialType == SpecialType.System_UInt32;
        }
    }
}
