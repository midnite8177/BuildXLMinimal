// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma once

#include <windows.h>

#include "DataTypes.h"
#include "PolicyResult.h"
#include "globals.h"

// ----------------------------------------------------------------------------
// FUNCTION DECLARATIONS
// ----------------------------------------------------------------------------

void ReportFileAccess(
    FileOperationContext const& fileOperationContext,
    FileAccessStatus status,
    PolicyResult const& policyResult,
    AccessCheckResult const& accessCheckResult,
    DWORD error,
    USN usn,
	wchar_t const* filter = nullptr);

void ReportProcessData(
    IO_COUNTERS const&  ioCounters,
    FILETIME const& creationTime,
    FILETIME const& exitTime,
    FILETIME const& kernelTime,
    FILETIME const& userTime,
    DWORD const& exitCode,
    DWORD const& parentProcessId,
    LONG64 const& detoursMaxMemHeapSize);

void ReportProcessDetouringStatus(
    ProcessDetouringStatus status,
    const LPCWSTR lpApplicationName,
    const LPWSTR lpCommandLine,
    const BOOL needsInjection,
    const BOOL isCurrent64BitProcess,
    const BOOL isCurrentWow64Process,
    const BOOL isProcessWow64,
    const BOOL needsRemoteInjection,
    const HANDLE hJob,
    const BOOL disableDetours,
    const DWORD dwCreationFlags,
    const BOOL detoured,
    const DWORD error,
    const CreateDetouredProcessStatus createProcessStatus);
