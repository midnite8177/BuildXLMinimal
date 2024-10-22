// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as Native from "Sdk.Native";
import {Transformer} from "Sdk.Transformers";

namespace Core {
    export declare const qualifier: BuildXLSdk.PlatformDependentQualifier;

    const headers = [
        f`Assertions.h`,
        f`DataTypes.h`,
        f`DetouredFunctions.h`,
        f`DebuggingHelpers.h`,
        f`DetouredFunctionTypes.h`,
        f`DetoursHelpers.h`,
        f`UtilityHelpers.h`,
        f`DetoursServices.h`,
        f`CanonicalizedPath.h`,
        f`PolicyResult.h`,
        f`FileAccessHelpers.h`,
        f`globals.h`,
        f`buildXL_mem.h`,
        f`DetouredScope.h`,
        f`SendReport.h`,
        f`StringOperations.h`,
        f`UnicodeConverter.h`,
        f`stdafx.h`,
        f`stdafx-win.h`,
        f`stdafx-unix-common.h`,
        f`stdafx-mac-interop.h`,
        f`stdafx-mac-kext.h`,
        f`targetver.h`,
        f`MetadataOverrides.h`,
        f`HandleOverlay.h`,
        f`PolicySearch.h`,
        f`DeviceMap.h`,
        f`DetouredProcessInjector.h`,
        f`UniqueHandle.h`,
        f`SubstituteProcessExecution.h`,
        f`FilesCheckedForAccess.h`,
        f`ResolvedPathCache.h`,
        f`PathTree.h`,
        f`TreeNode.h`
    ];

    @@public
    export const includes = [
        Transformer.sealPartialDirectory(d`.`, headers),
        importFrom("BuildXL.Sandbox.Common").Include.includes,
    ];

    export const pathToDeviceMapLib: PathAtom = a`${qualifier.platform.replace("x", qualifier.configuration)}`;

    const sharedSettings = Runtime.isHostOsWindows && Detours.Lib.nativeDllBuilderDefaultValue.merge<Native.Dll.Arguments>({
            includes: [
                ...headers,
                importFrom("BuildXL.Sandbox.Common").Include.includes,
                importFrom("BuildXL.DeviceMap").Contents.all,
                Detours.Include.includes,
                importFrom("WindowsSdk").UM.include,
                importFrom("WindowsSdk").Shared.include,
                importFrom("WindowsSdk").Ucrt.include,
                importFrom("VisualCpp").include,
            ],
            preprocessorSymbols: [
                {name: "DETOURSSERVICES_EXPORTS"},
                ...addIf(BuildXLSdk.Flags.isMicrosoftInternal,
                    {name: "FEATURE_DEVICE_MAP"}
                ),
            ],
            libraries: [
                Detours.Lib.lib.binaryFile,
                ...importFrom("WindowsSdk").UM.standardLibs,
                ...addIfLazy(BuildXLSdk.Flags.isMicrosoftInternal, () => [
                    importFrom("BuildXL.DeviceMap").Contents.all.getFile(r`${pathToDeviceMapLib}/DeviceMap.lib`),
                ]),
                importFrom("VisualCpp").lib,
                importFrom("WindowsSdk").Ucrt.lib,
            ],
    });

    @@public
    export const nativesDll: Native.Dll.NativeDllImage = Runtime.isHostOsWindows && BuildXLSdk.Native.library(
        sharedSettings.merge<Native.Dll.Arguments>({
            outputFileName: PathAtom.create("BuildXLNatives.dll"),
            preprocessorSymbols: [{name: "BUILDXL_NATIVES_LIBRARY"}],
            sources: [
                f`Assertions.cpp`,
                f`DebuggingHelpers.cpp`,
                f`DetoursServices.cpp`,
                f`DetouredScope.cpp`,
                f`StringOperations.cpp`,
                f`stdafx.cpp`,
                f`MetadataOverrides.cpp`,
                f`PolicySearch.cpp`,
                f`DeviceMap.cpp`,
                f`SendReport.cpp`,
                f`DetouredProcessInjector.cpp`,
                f`SubstituteProcessExecution.cpp`,
                f`PathTree.cpp`,
                f`TreeNode.cpp`
            ],

            exports: [
                {name: "DllMain"},
                {name: "IsDetoursDebug"},
                {name: "CreateDetachedProcess"},
                {name: "FindFileAccessPolicyInTree"},
                {name: "NormalizeAndHashPath"},
                {name: "AreBuffersEqual"},
                {name: "RemapDevices"},
                {name: "CreateDetouredProcess"},
                {name: "DetouredProcessInjector_Create"},
                {name: "DetouredProcessInjector_Destroy"},
                {name: "DetouredProcessInjector_Inject"},
            ],
        })
    );

    // This dll contains libraries used in detours that are consumed by tests (which do not run under detours)
    @@public
    export const testDll: Native.Dll.NativeDllImage = Runtime.isHostOsWindows && BuildXLSdk.Native.library(
    {
        outputFileName: PathAtom.create("BuildXLTestNatives.dll"),
        preprocessorSymbols: [
            {name: "BUILDXL_NATIVES_LIBRARY"}, 
            {name: "TEST"}],
        includes: [
            f`PathTree.h`,
            f`TreeNode.h`,
            f`stdafx.h`,
            f`stdafx-win.h`,
            f`targetver.h`,
            f`UtilityHelpers.h`,
            f`DataTypes.h`,
            f`StringOperations.h`,
            f`DebuggingHelpers.h`,
            f`Assertions.h`,
            Detours.Include.includes,
            importFrom("WindowsSdk").UM.include,
            importFrom("WindowsSdk").Shared.include,
            importFrom("WindowsSdk").Ucrt.include,
            importFrom("VisualCpp").include,
        ],
        sources: [
            f`Assertions.cpp`,
            f`StringOperations.cpp`,
            f`PathTree.cpp`,
            f`TreeNode.cpp`
        ],
        libraries: [
            ...importFrom("WindowsSdk").UM.standardLibs,
            importFrom("VisualCpp").lib,
            importFrom("WindowsSdk").Ucrt.lib
        ]
    });

    @@public
    export const detoursDll: Native.Dll.NativeDllImage = Runtime.isHostOsWindows && BuildXLSdk.Native.library(
        sharedSettings.merge<Native.Dll.Arguments>({
            outputFileName: PathAtom.create("DetoursServices.dll"),
            preprocessorSymbols: [{name: "DETOURS_SERVICES_NATIVES_LIBRARY"}],
            sources: [
                f`Assertions.cpp`,
                f`CanonicalizedPath.cpp`,
                f`PolicyResult.cpp`,
                f`PolicyResult_common.cpp`,
                f`DebuggingHelpers.cpp`,
                f`DetoursServices.cpp`,
                f`DetouredFunctions.cpp`,
                f`DetoursHelpers.cpp`,
                f`FileAccessHelpers.cpp`,
                f`DetouredScope.cpp`,
                f`StringOperations.cpp`,
                f`SendReport.cpp`,
                f`stdafx.cpp`,
                f`MetadataOverrides.cpp`,
                f`HandleOverlay.cpp`,
                f`PolicySearch.cpp`,
                f`DeviceMap.cpp`,
                f`DetouredProcessInjector.cpp`,
                f`SubstituteProcessExecution.cpp`,
                f`FilesCheckedForAccess.cpp`,
                f`PathTree.cpp`,
                f`TreeNode.cpp`
            ],

            exports: [
                {name: "DllMain"},
                {name: "CreateDetouredProcess"},
                {name: "DetouredProcessInjector_Create"},
                {name: "DetouredProcessInjector_Destroy"},
                {name: "DetouredProcessInjector_Inject"},
            ],
        })
    );
}
