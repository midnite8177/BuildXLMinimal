// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using BuildXL.LogGen.Core;

namespace BuildXL.LogGen.Generators
{
    /// <summary>
    /// Aria V2 logger
    /// </summary>
    public sealed class AriaV2 : GeneratorBase
    {
        private const string GlobalInstrumentationNamespaceCommon = "global::BuildXL.Utilities.Instrumentation.Common";
        private const string GlobalTracing = "global::BuildXL.Tracing";

        /// <inheritdoc/>
        public override void GenerateLogMethodBody(LoggingSite site, Func<string> getMessageExpression)
        {
            m_codeGenerator.Ln("if ({0}.AriaV2StaticState.IsEnabled)", GlobalInstrumentationNamespaceCommon);
            using (m_codeGenerator.Br)
            {
                m_codeGenerator.Lns("var eventData = new {0}.AriaEvent(\"{1}\", \"{2}\", \"{3}\")", GlobalInstrumentationNamespaceCommon, site.Method.Name, m_targetFramework, m_targetRuntime);

                // Save context fields that all events save
                m_codeGenerator.Lns("eventData.SetProperty(\"Environment\", {0}.Session.Environment)", site.LoggingContextParameterName);
                m_codeGenerator.Lns("eventData.SetProperty(\"SessionId\", {0}.Session.Id)", site.LoggingContextParameterName);
                m_codeGenerator.Lns("eventData.SetProperty(\"RelatedSessionId\", {0}.Session.RelatedId)", site.LoggingContextParameterName);

                // We only capture the username for MS internal
                m_codeGenerator.Ln("if ({0}.UserName.IsInternalCollectionAllowed)", GlobalInstrumentationNamespaceCommon);
                using (m_codeGenerator.Br)
                {
                    m_codeGenerator.Lns("eventData.SetProperty(\"UserName\", EngineEnvironmentSettings.BuildXLUserName.Value ?? Environment.UserName)");
                    m_codeGenerator.Lns("eventData.SetProperty(\"MachineName\", Environment.MachineName)");
                }

                m_codeGenerator.Lns("eventData.SetProperty(\"User\", EngineEnvironmentSettings.BuildXLUserName.Value ?? Environment.UserName, {0}.PiiType.Identity)", GlobalInstrumentationNamespaceCommon);

                foreach (var item in site.FlattenedPayload)
                {
                    WritePayload(site, item);
                }

                m_codeGenerator.Ln("eventData.Log();");
            }
        }

        /// <inheritdoc/>
        public override IEnumerable<Tuple<string, string>> ConsumedNamespaces
        {
            get
            {
                yield return new Tuple<string, string>("System", string.Empty);
                yield return new Tuple<string, string>("BuildXL.Utilities.Configuration", string.Empty);
            }
        }

        /// <inheritdoc/>
        public override void GenerateClass()
        {
        }

        private void WritePayload(LoggingSite site, LoggingSite.AddressedType item)
        {
            if (item.Type.SpecialType == SpecialType.System_String)
            {
                m_codeGenerator.Ln("eventData.SetProperty({0}.AriaV2StaticState.ScrubEventProperty(\"{1}\"), {2});", GlobalInstrumentationNamespaceCommon, item.AddressForTelemetryString, item.Address);
                return;
            }
            else if (IsCompatibleNumeric(item.Type))
            {
                m_codeGenerator.Ln("eventData.SetProperty({0}.AriaV2StaticState.ScrubEventProperty(\"{1}\"), (long){2});", GlobalInstrumentationNamespaceCommon, item.AddressForTelemetryString, item.Address);
                return;
            }
            else if (item.Type.TypeKind == TypeKind.Enum)
            {
                m_codeGenerator.Ln("eventData.SetProperty({0}.AriaV2StaticState.ScrubEventProperty(\"{1}\"), (long){2});", GlobalInstrumentationNamespaceCommon, item.AddressForTelemetryString, item.Address);
                m_codeGenerator.Ln("eventData.SetProperty({0}.AriaV2StaticState.ScrubEventProperty(\"{1}\"), {2}.ToString());", GlobalInstrumentationNamespaceCommon, item.AddressForTelemetryString, item.Address);
                return;
            }
            else if (item.Type.SpecialType == SpecialType.System_Boolean)
            {
                m_codeGenerator.Ln("eventData.SetProperty({0}.AriaV2StaticState.ScrubEventProperty(\"{1}\"), {2}.ToString());", GlobalInstrumentationNamespaceCommon, item.AddressForTelemetryString, item.Address);
                return;
            }
            else
            {
                IPropertySymbol key;
                IPropertySymbol value;
                if (TryGetEnumerableKeyValuePair(item, out key, out value) &&
                    key.Type.SpecialType == SpecialType.System_String &&
                    (value.Type.SpecialType == SpecialType.System_String || IsCompatibleNumeric(value.Type)))
                {
                    m_codeGenerator.Ln("foreach (var item in {0})", item.Address);
                    using (m_codeGenerator.Br)
                    {
                        if (value.Type.SpecialType == SpecialType.System_String)
                        {
                            m_codeGenerator.Ln("eventData.SetProperty({0}.AriaV2StaticState.ScrubEventProperty(item.Key), item.Value);", GlobalInstrumentationNamespaceCommon);
                        }
                        else
                        {
                            m_codeGenerator.Ln("eventData.SetProperty({0}.AriaV2StaticState.ScrubEventProperty(item.Key), (long)item.Value);", GlobalInstrumentationNamespaceCommon);
                        }
                    }

                    return;
                }
            }

            m_errorReport.ReportError(site.Method, "{0}'s type is not supported by the AriaV2 generator", item.Address);
        }

        internal static bool TryGetEnumerableKeyValuePair(LoggingSite.AddressedType item, out IPropertySymbol key, out IPropertySymbol value)
        {
            // Look through the implemented interfaces to see if we can put it into a dictionary
            foreach (INamedTypeSymbol intf in item.Type.AllInterfaces)
            {
                if (intf.Name == "IEnumerable" && intf.TypeArguments.Length > 0 && intf.TypeArguments[0].Name == "KeyValuePair")
                {
                    key = intf.TypeArguments[0].GetMembers("Key").Length > 0 ? intf.TypeArguments[0].GetMembers("Key")[0] as IPropertySymbol : null;
                    value = intf.TypeArguments[0].GetMembers("Value").Length > 0 ? intf.TypeArguments[0].GetMembers("Value")[0] as IPropertySymbol : null;
                    if (key != null && value != null)
                    {
                        return true;
                    }
                }
            }

            key = null;
            value = null;
            return false;
        }
    }
}
