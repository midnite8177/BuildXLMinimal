// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace CodeGenerationHelper {

    @@public
    export const dll = BuildXLSdk.library({
        assemblyName: "BuildXL.Utilities.CodeGenerationHelper",
        sources: globR(d`.`, "*.cs"),
        references: [
            ...addIf(BuildXLSdk.isFullFramework,
                NetFx.System.Xml.Linq.dll
            )
        ],
    });
}
