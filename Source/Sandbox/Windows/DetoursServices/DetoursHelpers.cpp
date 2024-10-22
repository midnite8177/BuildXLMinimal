// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#include "stdafx.h"

#include "DebuggingHelpers.h"
#include "DetoursHelpers.h"
#include "DetoursServices.h"
#include "globals.h"
#include "buildXL_mem.h"
#include "SendReport.h"
#include "StringOperations.h"
#include "DeviceMap.h"
#include "CanonicalizedPath.h"
#include "PolicyResult.h"
#include <list>
#include <string>
#include <stdio.h>
#include <stack>

using std::unique_ptr;
using std::basic_string;

// ----------------------------------------------------------------------------
// FUNCTION DEFINITIONS
// ----------------------------------------------------------------------------

/// <summary>
/// Gets the normalized (or subst'ed) path from a full path.
/// </summary>
void TranslateFilePath(_In_ const std::wstring& inFileName, _Out_ std::wstring& outFileName)
{
    outFileName.assign(inFileName);

    if (g_pManifestTranslatePathTuples->empty())
    {
        // Nothing to translate.
        return;
    }

    // If the string coming in is null or empty, just return. No need to do anything.
    if (inFileName.empty() || inFileName.c_str() == nullptr)
    {
        return;
    }

    CanonicalizedPath canonicalizedPath = CanonicalizedPath::Canonicalize(inFileName.c_str());
    std::wstring tempStr(canonicalizedPath.GetPathString());

    // If the canonicalized string is null or empty, just return. No need to do anything.
    if (tempStr.empty() || tempStr.c_str() == nullptr)
    {
        return;
    }

    const std::wstring prefix(L"\\??\\");
    bool hasPrefix = !tempStr.compare(0, prefix.size(), prefix);

    const std::wstring prefixNt(L"\\\\?\\");
    bool hasPrefixNt = !tempStr.compare(0, prefixNt.size(), prefixNt);

    tempStr.assign(canonicalizedPath.GetPathStringWithoutTypePrefix());

    bool translated = false;
    bool needsTranslation = true;

    std::list<TranslatePathTuple*> manifestTranslatePathTuples(g_pManifestTranslatePathTuples->begin(), g_pManifestTranslatePathTuples->end());

    while (needsTranslation)
    {
        needsTranslation = false;
        size_t longestPath = 0;
        std::list<TranslatePathTuple*>::iterator replacementIt;

        std::wstring lowCaseFinalPath(tempStr);
        for (basic_string<wchar_t>::iterator p = lowCaseFinalPath.begin();
            p != lowCaseFinalPath.end(); ++p) {
            *p = towlower(*p);
        }

        // Find the longest path that can be used for translation from the g_pManifestTranslatePathTuples list.
        // Note: The g_pManifestTranslatePathTuples always comes canonicalized from the managed code.
        for (std::list<TranslatePathTuple*>::iterator it = manifestTranslatePathTuples.begin(); it != manifestTranslatePathTuples.end(); ++it)
        {
            TranslatePathTuple* tpTuple = *it;
            const std::wstring& lowCaseTargetPath = tpTuple->GetFromPath();
            size_t targetLen = lowCaseTargetPath.length();
            bool mayBeDirectoryPath = false;

            int comp = lowCaseFinalPath.compare(0, targetLen, lowCaseTargetPath);

            if (comp != 0)
            {
                // The path to be translated can be a directory path that does not have trailing '\\'.

                if (lowCaseFinalPath.back() != L'\\'
                    && lowCaseTargetPath.back() == L'\\'
                    && lowCaseFinalPath.length() == (targetLen - 1))
                {
                    std::wstring lowCaseFinalPathWithBs = lowCaseFinalPath + L'\\';
                    comp = lowCaseFinalPathWithBs.compare(0, targetLen, lowCaseTargetPath);
                    mayBeDirectoryPath = true;
                }
            }

            if (comp == 0)
            {
                if (longestPath < targetLen)
                {
                    replacementIt = it;
                    longestPath = !mayBeDirectoryPath ? targetLen : targetLen - 1;
                    translated = true;
                    needsTranslation = true;
                }
            }
        }

        // Translate using the longest translation path.
        if (needsTranslation)
        {
            TranslatePathTuple* replacementTuple = *replacementIt;

            std::wstring t(replacementTuple->GetToPath());
            t.append(tempStr, longestPath);

            tempStr.assign(t);
            manifestTranslatePathTuples.erase(replacementIt);
        }
    }

    if (translated)
    {
        if (hasPrefix)
        {
            outFileName.assign(prefix);
        }
        else
        {
            if (hasPrefixNt)
            {
                outFileName.assign(prefixNt);
            }
            else
            {
                outFileName.assign(L"");
            }
        }

        outFileName.append(tempStr);
    }
}

bool GetSpecialCaseRulesForWindows(
    __in  PCWSTR absolutePath,
    __in  size_t absolutePathLength,
    __out FileAccessPolicy& policy)
{
    assert(absolutePath);
    assert(absolutePathLength == wcslen(absolutePath));

    size_t rootLength = GetRootLength(absolutePath);
    if (HasPrefix(absolutePath + rootLength, L"$Extend\\$Deleted"))
    {
        // Windows can have an "unlink" behavior where deleted files are not really deleted if there's an opened handle.
        // This behavior is possible because a process can open a file with FILE_SHARE_DELETE that makes other processes able to delete it.
        // If a file is opened by specifying the FILE_SHARE_DELETE flag for the CreateFile function and another process tries to delete it,
        // the file is actually moved to the “\$Extend\$Deleted” directory on the same volume. When the last handle to such a file is closed,
        // it's deleted as usual. When the file system is mounted, all existing files in the “\$Extend\$Deleted” directory, if any, are deleted,
        // The same logic also applies to deleted directories.
        // Details can be found in this unofficial documentation: https://dfir.ru/2020/03/21/the-extenddeleted-directory/
#if SUPER_VERBOSE
        Dbg(L"special case: files in staged deletion: %s", absolutePath);
#endif // SUPER_VERBOSE
        policy = FileAccessPolicy::FileAccessPolicy_AllowAll;
        return true;
    }

    return false;
}

// Some perform file accesses, which don't yet fall into any configurable file access manifest category.
// These files now can be allowlisted, but there are already users deployed without the allowlisting feature
// that rely on these file accesses not blocked.
// These are some tools that use internal files or do some implicit directory creation, etc.
// In this list the tools are the CCI based set of products, csc compiler, resource compiler, build.exe trace log, etc.
// For such tools we allow file accesses on the special file patterns and report the access to BuildXL. BuildXL filters these
// accesses, but makes sure that there are reports for these accesses if some of them are declared as outputs.
bool GetSpecialCaseRulesForSpecialTools(
    __in  PCWSTR absolutePath,
    __in  size_t absolutePathLength,
    __out FileAccessPolicy& policy)
{
    assert(absolutePath);
    assert(absolutePathLength == wcslen(absolutePath));

    switch (GetProcessKind())
    {
    case SpecialProcessKind::Csc:
    case SpecialProcessKind::Cvtres:
    case SpecialProcessKind::Resonexe:
        // Some tools emit temporary files into the same directory
        // as the final output file.
        if (HasSuffix(absolutePath, absolutePathLength, L".tmp")) {
#if SUPER_VERBOSE
            Dbg(L"special case: temp file: %s", absolutePath);
#endif // SUPER_VERBOSE
            int intPolicy = (int)policy | (int)FileAccessPolicy_AllowAll;
            policy = (FileAccessPolicy)intPolicy;
            return true;
        }
        break;

    case SpecialProcessKind::RC:
        // The native resource compiler (RC) emits temporary files into the same
        // directory as the final output file.
        if (StringLooksLikeRCTempFile(absolutePath, absolutePathLength)) {
#if SUPER_VERBOSE
            Dbg(L"special case: temp file: %s", absolutePath);
#endif // SUPER_VERBOSE
            int intPolicy = (int)policy | (int)FileAccessPolicy_AllowAll;
            policy = (FileAccessPolicy)intPolicy;
            return true;
        }
        break;

    case SpecialProcessKind::Mt:
        // The Mt tool emits temporary files into the same directory as the final output file.
        if (StringLooksLikeMtTempFile(absolutePath, absolutePathLength, L".tmp")) {
#if SUPER_VERBOSE
            Dbg(L"special case: temp file: %s", absolutePath);
#endif // SUPER_VERBOSE
            int intPolicy = (int)policy | (int)FileAccessPolicy_AllowAll;
            policy = (FileAccessPolicy)intPolicy;
            return true;
        }
        break;

    case SpecialProcessKind::CCCheck:
    case SpecialProcessKind::CCDocGen:
    case SpecialProcessKind::CCRefGen:
    case SpecialProcessKind::CCRewrite:
        // The cc-line of tools like to find pdb files by using the pdb path embedded in a dll/exe.
        // If the dll/exe was built with different roots, then this results in somewhat random file accesses.
        if (HasSuffix(absolutePath, absolutePathLength, L".pdb")) {
#if SUPER_VERBOSE
            Dbg(L"special case: pdb file: %s", absolutePath);
#endif // SUPER_VERBOSE
            int intPolicy = (int)policy | (int)FileAccessPolicy_AllowAll;
            policy = (FileAccessPolicy)intPolicy;
            return true;
        }
        break;

    case SpecialProcessKind::WinDbg:
    case SpecialProcessKind::NotSpecial:
        // no special treatment
        break;
    }

    // build.exe and tracelog.dll capture dependency information in temporary files in the object root called _buildc_dep_out.<pass#>
    if (StringLooksLikeBuildExeTraceLog(absolutePath, absolutePathLength)) {
        int intPolicy = (int)policy | (int)FileAccessPolicy_AllowAll;
        policy = (FileAccessPolicy)intPolicy;
#if SUPER_VERBOSE
        Dbg(L"Build.exe trace log path: %s", absolutePath);
#endif // SUPER_VERBOSE
        return true;
    }

    return false;
}

// This functions allows file accesses for special undeclared files.
// In the special set set we include:
//     1. Code coverage runs
//     2. Te drive devices
//     3. Dos devices and special system devices/names (pipes, null dev etc).
// These accesses now should be allowlisted, but many users have deployed products that have specs not declaring such accesses.
bool GetSpecialCaseRulesForCoverageAndSpecialDevices(
    __in  PCWSTR absolutePath,
    __in  size_t absolutePathLength,
    __in  PathType pathType,
    __out FileAccessPolicy& policy)
{
    assert(absolutePath);
    assert(absolutePathLength == wcslen(absolutePath));

    // When running test cases with Code Coverage enabled, some more files are loaded that we should ignore
    if (IgnoreCodeCoverage()) {
        if (HasSuffix(absolutePath, absolutePathLength, L".pdb") ||
            HasSuffix(absolutePath, absolutePathLength, L".nls") ||
            HasSuffix(absolutePath, absolutePathLength, L".dll"))
        {
#if SUPER_VERBOSE
            Dbg(L"Ignoring possibly code coverage related path: %s", absolutePath);
#endif // SUPER_VERBOSE
            int intPolicy = (int)policy | (int)FileAccessPolicy_AllowAll;
            policy = (FileAccessPolicy)intPolicy;
            return true;
        }
    }

    if (pathType == PathType::LocalDevice || pathType == PathType::Win32Nt) {
        bool maybeStartsWithDrive = absolutePathLength >= 2 && IsDriveLetter(absolutePath[0]) && absolutePath[1] == L':';

        // For a normal Win32 path, C: means C:<current directory on C> or C:\ if one is not set. But \\.\C:, \\?\C:, and \??\C:
        // mean 'the device C:'. We don't care to model access to devices (volumes in this case).
        if (maybeStartsWithDrive && absolutePathLength == 2) {
#if SUPER_VERBOSE
            Dbg(L"Ignoring access to drive device (not the volume root; missing a trailing slash): %s", absolutePath);
#endif // SUPER_VERBOSE
            policy = FileAccessPolicy_AllowAll;
            return true;
        }

        // maybeStartsWithDrive => absolutePathLength >= 3
        assert(!maybeStartsWithDrive || absolutePathLength >= 3);

        // We do not provide a special case for e.g. \\.\C:\foo (equivalent to the Win32 C:\foo) but we do want to allow access
        // to non-drive DosDevices. For example, the Windows DNS API ends up(indirectly) calling CreateFile("\\\\.\\Nsi").
        // Note that this also allows access to the named pipe filesystem under \\.\pipe.
        bool startsWithDriveRoot = maybeStartsWithDrive && absolutePath[2] == L'\\';
        if (!startsWithDriveRoot) {
#if SUPER_VERBOSE
            Dbg(L"Ignoring non-drive device path: %s", absolutePath);
#endif // SUPER_VERBOSE
            policy = FileAccessPolicy_AllowAll;
            return true;
        }
    }

    if (IsPathToNamedStream(absolutePath, absolutePathLength)) {
#if SUPER_VERBOSE
        Dbg(L"Ignoring path to a named stream: %s", absolutePath);
#endif // SUPER_VERBOSE
        policy = FileAccessPolicy_AllowAll;
        return true;
    }

    return false;
}

bool WantsWriteAccess(DWORD access)
{
    return (access & (GENERIC_ALL | GENERIC_WRITE | DELETE | FILE_WRITE_DATA | FILE_WRITE_ATTRIBUTES | FILE_WRITE_EA | FILE_APPEND_DATA)) != 0;
}

bool WantsReadAccess(DWORD access)
{
    return (access & (GENERIC_READ | FILE_READ_DATA)) != 0;
}

bool WantsReadOnlyAccess(DWORD access)
{
    return WantsReadAccess(access) && !WantsWriteAccess(access);
}

bool WantsProbeOnlyAccess(DWORD access)
{
    return !WantsReadAccess(access)
        && !WantsWriteAccess(access)
        && (access == 0 || (access & (FILE_READ_ATTRIBUTES | FILE_READ_EA)) != 0);
}

bool WantsDeleteOnlyAccess(DWORD access)
{
    return access == DELETE;
}

/* Indicates if a path contains a wildcard that may be interpreted by FindFirstFile / FindFirstFileEx. */
bool PathContainsWildcard(LPCWSTR path) {
    for (WCHAR const* pch = path; *pch != L'\0'; pch++) {
        if (*pch == L'?' || *pch == L'*') {
            return true;
        }
    }

    return false;
}

bool ParseUInt64Arg(
    __inout PCWSTR& pos,
    int radix,
    __out ulong& value)
{
    PWSTR nextPos;
    value = _wcstoui64(pos, &nextPos, radix);
    if (nextPos == NULL) {
        return false;
    }

    if (*nextPos == L',') {
        ++nextPos;
    }
    else if (*nextPos != 0) {
        return false;
    }

    pos = nextPos;
    return true;
}

bool LocateFileAccessManifest(
    __out const void*& manifest,
    __out DWORD& manifestSize)
{
    manifest = NULL;
    manifestSize = 0;

    HMODULE previousModule = NULL;
    for (;;) {
        HMODULE currentModule = DetourEnumerateModules(previousModule);
        if (currentModule == NULL) {
            Dbg(L"Did not find Detours payload.");
            return false;
        }

        previousModule = currentModule;
        DWORD payloadSize;
        const void* payload = DetourFindPayload(currentModule, __uuidof(IDetourServicesManifest), &payloadSize);
        if (payload != NULL) {
#if SUPER_VERBOSE
            Dbg(L"Found Detours payload at %p len 0x%x", payload, payloadSize);
#endif // SUPER_VERBOSE
            manifest = payload;
            manifestSize = payloadSize;
            return true;
        }
    }
}

/// VerifyManifestTree
///
/// Run through the tree and perform integrity checks on everything reachable in the tree,
/// to detect the possibility of data corruption in the tree.
///
/// This check is O(m) where m is the number of entries in the manifest.
/// Only use it for debugging when a corrupted binary structure is suspected.
#pragma warning( push )
#pragma warning( disable: 4100 ) // in release builds, record is unused
inline void VerifyManifestTree(PCManifestRecord const record)
{
#ifdef _DEBUG
    record->AssertValid();

    // loop through every item on every level recursively and verify tags are correct
    ManifestRecord::BucketCountType numBuckets = record->BucketCount;
    for (ManifestRecord::BucketCountType i = 0; i < numBuckets; i++)
    {
        PCManifestRecord child = record->GetChildRecord(i);

        if (child != nullptr)
        {
            VerifyManifestTree(child);
        }
    }
#endif
}
#pragma warning( pop )

/// VerifyManifestRoot
///
/// Check that the root is a valid root record by checking the tag and that
/// the path of the root scope is an empty string.
#pragma warning( push )
#pragma warning( disable: 4100 ) // in release builds, root is unused
inline void VerifyManifestRoot(PCManifestRecord const root)
{
#ifdef _DEBUG
    root->AssertValid();
#endif

    assert(root->GetPartialPath()[0] == 0); // the root path should be an empty string
}
#pragma warning( pop )

void WriteToInternalErrorsFile(PCWSTR format, ...)
{
    if (g_internalDetoursErrorNotificationFile != nullptr)
    {
        DWORD error = GetLastError();

        while (true)
        {
            // Get a file handle.
            HANDLE openedFile = CreateFileW(
                g_internalDetoursErrorNotificationFile,
                FILE_APPEND_DATA,
                0,
                NULL,
                OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                NULL);

            if (openedFile == INVALID_HANDLE_VALUE)
            {
                // Wait to get exclusive access to the file.
                if (GetLastError() == ERROR_SHARING_VIOLATION)
                {
                    Sleep(10);
                    continue;
                }

                // Failure to open the file. if that happens, we miss logging this message log, so just continue.
                break;
            }
            else
            {
                // File was successfully opened --> format error message and write it to file
                va_list args;
                va_start(args, format);
                std::wstring errorMessage = DebugStringFormatArgs(format, args);
                WriteFile(openedFile, errorMessage.c_str(), (DWORD)(errorMessage.length() * sizeof(wchar_t)), nullptr, nullptr);
                va_end(args);
                CloseHandle(openedFile);

                break;
            }
        }

        SetLastError(error);
    }
}

static inline byte ParseByte(const byte* payloadBytes, size_t& offset)
{
    byte b = payloadBytes[offset];
    offset += sizeof(byte);
    return b;
}

static inline uint32_t ParseUint32(const byte *payloadBytes, size_t &offset)
{
    uint32_t i = *(uint32_t*)(&payloadBytes[offset]);
    offset += sizeof(uint32_t);
    return i;
}

/// Decodes a length plus UTF-16 non-null-terminated string written by FileAccessManifest.WriteChars()
/// into an allocated, null-terminated string. Returns nullptr if the encoded string length is zero.
wchar_t *CreateStringFromWriteChars(const byte *payloadBytes, size_t &offset, uint32_t *pStrLen = nullptr)
{
    uint32_t len = ParseUint32(payloadBytes, offset);
    if (pStrLen != nullptr)
    {
        *pStrLen = len;
    }

    WCHAR *pStr = nullptr;
    if (len != 0)
    {
        pStr = new wchar_t[len + 1]; // Reserve some space for \0 terminator at end.
        uint32_t strSizeBytes = sizeof(wchar_t) * (len + 1);
        ZeroMemory((void*)pStr, strSizeBytes);
        memcpy_s((void*)pStr, strSizeBytes, (wchar_t*)(&payloadBytes[offset]), sizeof(wchar_t) * len);
        offset += sizeof(wchar_t) * len;
    }

    return pStr;
}

void AppendStringFromWriteChars(const byte* payloadBytes, size_t& offset, _Out_ std::wstring& result)
{
    uint32_t len = ParseUint32(payloadBytes, offset);
    if (len == 0)
    {
        return;
    }

    result.append((wchar_t*)(&payloadBytes[offset]), len);
    offset += sizeof(wchar_t) * len;
}

static inline void SkipWriteCharsString(const byte* payloadBytes, size_t& offset)
{
    uint32_t len = ParseUint32(payloadBytes, offset);
    offset += sizeof(wchar_t) * len;
}

static SubstituteProcessExecutionPluginFunc GetSubstituteProcessExecutionPluginFunc()
{
    assert(g_SubstituteProcessExecutionPluginDllHandle != nullptr);

    // Different compiler or different compiler settings can result in different function name variants.
    //
    // X64 version typically has:
    //     ordinal hint RVA      name
    //
    //     1    0 00011069 CommandMatches = @ILT + 100(CommandMatches)
    //
    // X86 version can have:
    //     ordinal hint RVA      name
    //
    //     1    0 00011276 _CommandMatches@24 = @ILT + 625(_CommandMatches@24)


    // (1) Check for CommandMatches.
    std::string winApiProcName("CommandMatches");
    SubstituteProcessExecutionPluginFunc substituteProcessExecutionPluginFunc = reinterpret_cast<SubstituteProcessExecutionPluginFunc>(
        reinterpret_cast<void*>(GetProcAddress(g_SubstituteProcessExecutionPluginDllHandle, winApiProcName.c_str())));
    if (substituteProcessExecutionPluginFunc != nullptr)
    {
        return substituteProcessExecutionPluginFunc;
    }

    // (2) Check for CommandMatches@<param_size> based on platform.
#if defined(_WIN64)
    winApiProcName.append("@48"); // 6 64-bit parameters
#elif defined(_WIN32)
    winApiProcName.append("@24"); // 6 32-bit parameters
#endif
    substituteProcessExecutionPluginFunc = reinterpret_cast<SubstituteProcessExecutionPluginFunc>(
        reinterpret_cast<void*>(GetProcAddress(g_SubstituteProcessExecutionPluginDllHandle, winApiProcName.c_str())));
    if (substituteProcessExecutionPluginFunc != nullptr)
    {
        return substituteProcessExecutionPluginFunc;
    }

    // (3) Check for _CommandMatches@<param_size>.
    winApiProcName.insert(0, 1, '_');
    substituteProcessExecutionPluginFunc = reinterpret_cast<SubstituteProcessExecutionPluginFunc>(
        reinterpret_cast<void*>(GetProcAddress(g_SubstituteProcessExecutionPluginDllHandle, winApiProcName.c_str())));
    if (substituteProcessExecutionPluginFunc != nullptr)
    {
        return substituteProcessExecutionPluginFunc;
    }

    Dbg(L"Unable to find 'CommandMatches', 'CommandMatches@<param_size>', or '_CommandMatches@<param_size>' functions in SubstituteProcessExecutionPluginFunc '%s', lasterr=%d", g_SubstituteProcessExecutionPluginDllPath, GetLastError());
    return nullptr;
}

static void LoadSubstituteProcessExecutionPluginDll()
{
    assert(g_SubstituteProcessExecutionPluginDllPath != nullptr);
    // Since we call LoadLibrary with this path, we need to ensure that it is a full path.
    assert(GetRootLength(g_SubstituteProcessExecutionPluginDllPath) > 0);

    Dbg(L"Loading substitute process plugin DLL at '%s'", g_SubstituteProcessExecutionPluginDllPath);

    g_SubstituteProcessExecutionPluginDllHandle = LoadLibraryW(g_SubstituteProcessExecutionPluginDllPath);

    if (g_SubstituteProcessExecutionPluginDllHandle != nullptr)
    {
        g_SubstituteProcessExecutionPluginFunc = GetSubstituteProcessExecutionPluginFunc();

        if (g_SubstituteProcessExecutionPluginFunc == nullptr)
        {
            FreeLibrary(g_SubstituteProcessExecutionPluginDllHandle);
        }
    }
    else
    {
        Dbg(L"Failed LoadLibrary for LoadSubstituteProcessExecutionPluginDll %s, lasterr=%d", g_SubstituteProcessExecutionPluginDllPath, GetLastError());
    }
}


/// <summary>
/// Gets the final full path by handle.
/// </summary>
/// <remarks>
/// This function encapsulates calls to <code>GetFinalPathNameByHandleW</code> and allocates memory as needed.
/// </remarks>
static DWORD DetourGetFinalPathByHandle(_In_ HANDLE hFile, _Inout_ std::wstring& fullPath)
{
    // First, try with a fixed-sized buffer which should be good enough for all practical cases.
    wchar_t wszBuffer[MAX_PATH];
    DWORD nBufferLength = std::extent<decltype(wszBuffer)>::value;

    DWORD result = GetFinalPathNameByHandleW(hFile, wszBuffer, nBufferLength, FILE_NAME_NORMALIZED);
    if (result == 0)
    {
        DWORD ret = GetLastError();
        return ret;
    }

    if (result < nBufferLength)
    {
        // The buffer was big enough. The return value indicates the length of the full path, NOT INCLUDING the terminating null character.
        // https://msdn.microsoft.com/en-us/library/windows/desktop/aa364962(v=vs.85).aspx
        fullPath.assign(wszBuffer, static_cast<size_t>(result));
    }
    else
    {
        // Second, if that buffer wasn't big enough, we try again with a dynamically allocated buffer with sufficient size.
        // Note that in this case, the return value indicates the required buffer length, INCLUDING the terminating null character.
        // https://msdn.microsoft.com/en-us/library/windows/desktop/aa364962(v=vs.85).aspx
        unique_ptr<wchar_t[]> buffer(new wchar_t[result]);
        assert(buffer.get());

        DWORD next_result = GetFinalPathNameByHandleW(hFile, buffer.get(), result, FILE_NAME_NORMALIZED);
        if (next_result == 0)
        {
            DWORD ret = GetLastError();
            return ret;
        }

        if (next_result < result)
        {
            fullPath.assign(buffer.get(), next_result);
        }
        else
        {
            return ERROR_NOT_ENOUGH_MEMORY;
        }
    }

    return ERROR_SUCCESS;
}

/// <summary>
/// Checks if Detours should resolve all reparse points contained in a path.
/// Only used when creating process to resolve the path to executable.
/// </summary>
static bool ShouldResolveReparsePointsInPath(
    _In_     const PolicyResult& policyResult)
{
    bool ignoreReparsePointForPath =
        IgnoreReparsePoints() ||
        (IgnoreFullReparsePointResolving() && !policyResult.EnableFullReparsePointParsing()) ||
        policyResult.IndicateUntracked();
    return !ignoreReparsePointForPath;
}

bool ParseFileAccessManifest(
    const void* payload,
    DWORD)
{
    //
    // Parse the file access manifest payload
    //

    assert(payload != nullptr);

    std::wstring initErrorMessage;
    uint32_t payloadSize;
    LPCBYTE payloadBytes = nullptr;

    if (!g_pDetouredProcessInjector->Init(reinterpret_cast<const byte *>(payload), initErrorMessage, &payloadBytes, payloadSize))
    {
        // Error initializing injector due to incorrect content of payload.
        std::wstring errorMsg = DebugStringFormat(L"ParseFileAccessManifest: Error initializing process injector: %s", initErrorMessage.c_str());
        HandleDetoursInjectionAndCommunicationErrors(DETOURS_PAYLOAD_PARSE_FAILED_19, errorMsg.c_str(), DETOURS_WINDOWS_LOG_MESSAGE_19);
        return false;
    }

    assert(payloadSize > 0);
    assert(payloadBytes != nullptr);

    g_currentProcessId = GetCurrentProcessId();

    g_currentProcessCommandLine = GetCommandLine();

    g_lpDllNameX86 = NULL;
    g_lpDllNameX64 = NULL;

    g_manifestSize = payloadSize;
    size_t offset = 0;

    PCManifestDebugFlag debugFlag = reinterpret_cast<PCManifestDebugFlag>(&payloadBytes[offset]);
    if (!debugFlag->CheckValidityAndHandleInvalid())
    {
        HandleDetoursInjectionAndCommunicationErrors(DETOURS_PAYLOAD_PARSE_FAILED_15, L"ParseFileAccessManifest: Error invalid debugFlag", DETOURS_WINDOWS_LOG_MESSAGE_15);
        return false;
    }

    offset += debugFlag->GetSize();

    PCManifestInjectionTimeout injectionTimeoutFlag = reinterpret_cast<PCManifestInjectionTimeout>(&payloadBytes[offset]);
    if (!injectionTimeoutFlag->CheckValidityAndHandleInvalid())
    {
        HandleDetoursInjectionAndCommunicationErrors(DETOURS_PAYLOAD_PARSE_FAILED_16, L"ParseFileAccessManifest: Error invalid injectionTimeoutFlag", DETOURS_WINDOWS_LOG_MESSAGE_16);
        return false;
    }

    g_injectionTimeoutInMinutes = static_cast<unsigned long>(injectionTimeoutFlag->Flags);

    // Make sure the injectionTimeout is not less than 10 min.
    if (g_injectionTimeoutInMinutes < 10)
    {
        g_injectionTimeoutInMinutes = 10;
    }

    offset += injectionTimeoutFlag->GetSize();

    g_manifestChildProcessesToBreakAwayFromJob = reinterpret_cast<const PManifestChildProcessesToBreakAwayFromJob>(&payloadBytes[offset]);
    g_manifestChildProcessesToBreakAwayFromJob->AssertValid();
    offset += g_manifestChildProcessesToBreakAwayFromJob->GetSize();

    for (uint32_t i = 0; i < g_manifestChildProcessesToBreakAwayFromJob->Count; i++)
    {
        std::wstring processName(L"");
        AppendStringFromWriteChars(payloadBytes, offset, processName);
        if (!processName.empty())
        {   
            std::wstring requiredCommandLineArgsSubstring(L"");
            AppendStringFromWriteChars(payloadBytes, offset, requiredCommandLineArgsSubstring);
            g_breakawayChildProcesses->push_back(BreakawayChildProcess(processName, requiredCommandLineArgsSubstring, ParseByte(payloadBytes, offset) == 1U));
        }
    }

    g_manifestTranslatePathsStrings = reinterpret_cast<const PManifestTranslatePathsStrings>(&payloadBytes[offset]);
    g_manifestTranslatePathsStrings->AssertValid();
    offset += g_manifestTranslatePathsStrings->GetSize();

    for (uint32_t i = 0; i < g_manifestTranslatePathsStrings->Count; i++)
    {
        std::wstring translateFrom(L"");
        AppendStringFromWriteChars(payloadBytes, offset, translateFrom);

        if (!translateFrom.empty())
        {
            for (basic_string<wchar_t>::iterator p = translateFrom.begin(); p != translateFrom.end(); ++p)
            {
                *p = towlower(*p);
            }
        }

        std::wstring translateTo(L"");
        AppendStringFromWriteChars(payloadBytes, offset, translateTo);

        if (!translateFrom.empty() && !translateTo.empty())
        {
            g_pManifestTranslatePathTuples->push_back(new TranslatePathTuple(translateFrom, translateTo));

            if (translateFrom.back() == L'\\')
            {
                translateFrom.pop_back();
            }

            std::transform(translateFrom.begin(), translateFrom.end(), translateFrom.begin(), std::towupper);

            if (translateTo.back() == L'\\')
            {
                translateTo.pop_back();
            }

            std::transform(translateTo.begin(), translateTo.end(), translateTo.begin(), std::towupper);

            g_pManifestTranslatePathLookupTable->insert(translateFrom);
            g_pManifestTranslatePathLookupTable->insert(translateTo);
        }
    }

    g_manifestInternalDetoursErrorNotificationFileString = reinterpret_cast<const PManifestInternalDetoursErrorNotificationFileString>(&payloadBytes[offset]);
    g_manifestInternalDetoursErrorNotificationFileString->AssertValid();
#ifdef _DEBUG
    offset += sizeof(uint32_t);
#endif
    uint32_t manifestInternalDetoursErrorNotificationFileSize;
    g_internalDetoursErrorNotificationFile = CreateStringFromWriteChars(payloadBytes, offset, &manifestInternalDetoursErrorNotificationFileSize);

    PCManifestFlags flags = reinterpret_cast<PCManifestFlags>(&payloadBytes[offset]);
    flags->AssertValid();
    g_fileAccessManifestFlags = static_cast<FileAccessManifestFlag>(flags->Flags);
    offset += flags->GetSize();

    PCManifestExtraFlags extraFlags = reinterpret_cast<PCManifestExtraFlags>(&payloadBytes[offset]);
    extraFlags->AssertValid();
    g_fileAccessManifestExtraFlags = static_cast<FileAccessManifestExtraFlag>(extraFlags->ExtraFlags);
    g_pDetouredProcessInjector->SetAlwaysRemoteInjectFromWow64Process(CheckAlwaysRemoteInjectDetoursFrom32BitProcess(g_fileAccessManifestExtraFlags));
    g_pDetouredProcessInjector->SetPayload(payloadBytes, payloadSize);
    offset += extraFlags->GetSize();

    PCManifestPipId pipId = reinterpret_cast<PCManifestPipId>(&payloadBytes[offset]);
    pipId->AssertValid();
    g_FileAccessManifestPipId = static_cast<uint64_t>(pipId->PipId);
    offset += pipId->GetSize();

    // Semaphore names don't allow '\\'
    if (CheckDetoursMessageCount() && g_internalDetoursErrorNotificationFile != nullptr)
    {
        wchar_t* helperString = new wchar_t[manifestInternalDetoursErrorNotificationFileSize + 3];
        ZeroMemory((void*)helperString, sizeof(wchar_t) * (manifestInternalDetoursErrorNotificationFileSize + 3));

        for (uint32_t i = 0; i < manifestInternalDetoursErrorNotificationFileSize; i++)
        {
            if (g_internalDetoursErrorNotificationFile[i] == L'\\')
            {
                helperString[i] = L'_';
            }
            else
            {
                helperString[i] = g_internalDetoursErrorNotificationFile[i];
            }
        }

        helperString[manifestInternalDetoursErrorNotificationFileSize] = L'_';
        helperString[manifestInternalDetoursErrorNotificationFileSize + 1] = L'1';
        g_messageCountSemaphore = OpenSemaphore(SEMAPHORE_ALL_ACCESS, FALSE, helperString);

        if (g_messageCountSemaphore == nullptr || g_messageCountSemaphore == INVALID_HANDLE_VALUE)
        {
            DWORD error = GetLastError();
            std::wstring errorMsg = DebugStringFormat(L"ParseFileAccessManifest: Failed to open message-count tracking semaphore '%s' (error code: 0x%0X8)", helperString, (int)error);
            Dbg(errorMsg.c_str());
            HandleDetoursInjectionAndCommunicationErrors(DETOURS_SEMAPHOREOPEN_ERROR_6, errorMsg.c_str(), DETOURS_WINDOWS_LOG_MESSAGE_6);
        }

        helperString[manifestInternalDetoursErrorNotificationFileSize + 1] = L'2';
        g_messageSentCountSemaphore = OpenSemaphore(SEMAPHORE_ALL_ACCESS, FALSE, helperString);

        if (g_messageSentCountSemaphore == nullptr || g_messageSentCountSemaphore == INVALID_HANDLE_VALUE)
        {
            DWORD error = GetLastError();
            std::wstring errorMsg = DebugStringFormat(L"ParseFileAccessManifest: Failed to open message-count tracking semaphore '%s' (error code: 0x%0X8)", helperString, (int)error);
            Dbg(errorMsg.c_str());
            HandleDetoursInjectionAndCommunicationErrors(DETOURS_SEMAPHOREOPEN_ERROR_6, errorMsg.c_str(), DETOURS_WINDOWS_LOG_MESSAGE_6);
        }

        delete[] helperString;
    }

    PCManifestReport report = reinterpret_cast<PCManifestReport>(&payloadBytes[offset]);
    report->AssertValid();

    if (report->IsReportPresent()) {
        if (report->IsReportHandle()) {
            g_reportFileHandle = g_pDetouredProcessInjector->ReportPipe();
#ifdef _DEBUG
#pragma warning( push )
#pragma warning( disable: 4302 4310 4311 4826 )
#if SUPER_VERBOSE
            Dbg(L"report file handle: %llu", (unsigned long long)g_reportFileHandle);
#endif // SUPER_VERBOSE
#pragma warning( pop )
#endif
        }
        else {
            // NOTE: This calls the real CreateFileW(), not our detoured version, because we have not yet installed
            // our detoured functions.
            g_reportFileHandle = CreateFileW(
                report->Report.ReportPath,
                FILE_WRITE_ACCESS,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                NULL,
                OPEN_ALWAYS,
                0,
                NULL);

            if (g_reportFileHandle == INVALID_HANDLE_VALUE) {
                DWORD error = GetLastError();
                g_reportFileHandle = NULL;
                // No need to call Dbg since calling Dbg with invalid or NULL report handle is noop.
                std::wstring errorMsg = DebugStringFormat(L"ParseFileAccessManifest: Failed to open report file '%s' (error code: 0x%08X)", report->Report.ReportPath, (int)error);
                HandleDetoursInjectionAndCommunicationErrors(DETOURS_PAYLOAD_PARSE_FAILED_17, errorMsg.c_str(), DETOURS_WINDOWS_LOG_MESSAGE_17);
                return false;
            }

#if SUPER_VERBOSE
            Dbg(L"report file opened: %s", report->Report.ReportPath);
#endif // SUPER_VERBOSE
        }
    }
    else {
        g_reportFileHandle = NULL;
    }

    offset += report->GetSize();

    PCManifestDllBlock dllBlock = reinterpret_cast<PCManifestDllBlock>(&payloadBytes[offset]);
    dllBlock->AssertValid();

    g_lpDllNameX86 = dllBlock->GetDllString(0);
    g_lpDllNameX64 = dllBlock->GetDllString(1);

    // Update the injector with the DLLs
    g_pDetouredProcessInjector->SetDlls(g_lpDllNameX86, g_lpDllNameX64);
    offset += dllBlock->GetSize();

    PCManifestSubstituteProcessExecutionShim pShimInfo = reinterpret_cast<PCManifestSubstituteProcessExecutionShim>(&payloadBytes[offset]);
    pShimInfo->AssertValid();
    offset += pShimInfo->GetSize();
    g_SubstituteProcessExecutionShimPath = CreateStringFromWriteChars(payloadBytes, offset);
    if (g_SubstituteProcessExecutionShimPath != nullptr)
    {
        g_ProcessExecutionShimAllProcesses = pShimInfo->ShimAllProcesses != 0;

        // Both _WIN32 and _WIN64 are defined when targeting 32-bit windows from 64-bit windows.
        // See: https://docs.microsoft.com/en-us/cpp/preprocessor/predefined-macros?redirectedfrom=MSDN&view=vs-2019
#if defined(_WIN64) // Defined as 1 when the compilation target is 64-bit ARM or x64. Otherwise, undefined.
        SkipWriteCharsString(payloadBytes, offset);  // Skip 32-bit path.
        g_SubstituteProcessExecutionPluginDllPath = CreateStringFromWriteChars(payloadBytes, offset);
#elif defined(_WIN32) // Defined as 1 when the compilation target is 32-bit ARM, 64-bit ARM, x86, or x64
        g_SubstituteProcessExecutionPluginDllPath = CreateStringFromWriteChars(payloadBytes, offset);
        SkipWriteCharsString(payloadBytes, offset);  // Skip 64-bit path.
#endif
        uint32_t numProcessMatches = ParseUint32(payloadBytes, offset);
        g_pShimProcessMatches = new vector<ShimProcessMatch*>();
        for (uint32_t i = 0; i < numProcessMatches; i++)
        {
            wchar_t *processName = CreateStringFromWriteChars(payloadBytes, offset);
            wchar_t *argumentMatch = CreateStringFromWriteChars(payloadBytes, offset);
            g_pShimProcessMatches->push_back(new ShimProcessMatch(processName, argumentMatch));
        }
    }

    if (g_SubstituteProcessExecutionPluginDllPath != nullptr)
    {
        LoadSubstituteProcessExecutionPluginDll();
    }

    g_manifestTreeRoot = reinterpret_cast<PCManifestRecord>(&payloadBytes[offset]);
    VerifyManifestRoot(g_manifestTreeRoot);

    //
    // Try to read module file and check permissions.
    //

    WCHAR wszFileName[MAX_PATH];
    DWORD nFileName = GetModuleFileNameW(NULL, wszFileName, MAX_PATH);
    if (nFileName == 0 || nFileName == MAX_PATH) {
        FileOperationContext fileOperationContextWithoutModuleName(
            L"Process",
            GENERIC_READ,
            FILE_SHARE_READ,
            OPEN_EXISTING,
            0,
            nullptr);

        ReportFileAccess(
            fileOperationContextWithoutModuleName,
            FileAccessStatus::FileAccessStatus_CannotDeterminePolicy,
            PolicyResult(), // Indeterminate
            AccessCheckResult(RequestedAccess::None, ResultAction::Deny, ReportLevel::Report),
            GetLastError(),
            -1);
        return true;
    }

    FileOperationContext fileOperationContext = FileOperationContext::CreateForRead(L"Process", wszFileName);

    PolicyResult policyResult;
    if (!policyResult.Initialize(wszFileName)) {
        policyResult.ReportIndeterminatePolicyAndSetLastError(fileOperationContext);
        return true;
    }

    FileReadContext fileReadContext;
    fileReadContext.Existence = FileExistence::Existent; // Clearly this process started somehow.
    fileReadContext.OpenedDirectory = false;

    if (ShouldResolveReparsePointsInPath(policyResult))
    {
        HANDLE hFile = CreateFileW(
            wszFileName,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE,
                NULL,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                NULL);

        if (hFile == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        std::wstring fullyResolvedPath;
        DWORD getFinalNameResult = DetourGetFinalPathByHandle(hFile, fullyResolvedPath);
        CloseHandle(hFile);
        if (getFinalNameResult != ERROR_SUCCESS)
        {
            return false;
        }

        std::wstring translatedName;
        TranslateFilePath(fullyResolvedPath, translatedName);

        std::wstring canonicalizedPathNoPrefix = std::wstring(CanonicalizedPath::Canonicalize(translatedName.c_str()).GetPathStringWithoutTypePrefix());
        std::wstring canonicalizedPath = std::wstring(CanonicalizedPath::Canonicalize(translatedName.c_str()).GetPathString());

        // Reset policy result because the fully resolved path is likely to be different.
        PolicyResult newPolicyResult;
        if (!newPolicyResult.Initialize(canonicalizedPathNoPrefix.c_str()))
        {
            fileOperationContext.AdjustPath(canonicalizedPath.c_str());
            newPolicyResult.ReportIndeterminatePolicyAndSetLastError(fileOperationContext);
            return true;
        }

        std::wstring newPolicyPath = std::wstring(newPolicyResult.GetCanonicalizedPath().GetPathString());
        size_t newLen = newPolicyPath.length();
        std::wstring oldPolicyPath = std::wstring(policyResult.GetCanonicalizedPath().GetPathString());
        size_t oldLen = oldPolicyPath.length();

        Dbg(L"Resolved reparse point from:\t'%ws' to '%ws'\ttranslated to:\t%ws\tcanonicalized to:\t%ws\twithout prefix: %ws\tnew policy path:\t%ws %zu [%wc]\told policy result path:\t%ws %zu [%wc] [%wc] [%wc] [%wc] [%wc]",
            wszFileName,
            fullyResolvedPath.c_str(),
            translatedName.c_str(),
            canonicalizedPath.c_str(),
            canonicalizedPathNoPrefix.c_str(),
            newPolicyPath.c_str(),
            newLen,
            newPolicyPath[newLen - 1],
            oldPolicyPath.c_str(),
            oldLen,
            oldPolicyPath[0],
            oldPolicyPath[1],
            oldPolicyPath[10],
            oldPolicyPath[50],
            oldPolicyPath[oldLen - 1]);
        fileOperationContext.AdjustPath(newPolicyPath.c_str());
        policyResult = newPolicyResult;
    }

    AccessCheckResult readCheck = policyResult.CheckReadAccess(RequestedReadAccess::Read, fileReadContext);

    ReportFileAccess(
        fileOperationContext,
        readCheck.GetFileAccessStatus(),
        policyResult,
        readCheck,
        ERROR_SUCCESS, // No interesting error code to observe or return to anyone.
        -1);

    return true;
}

bool LocateAndParseFileAccessManifest()
{
    const void* manifest;
    DWORD manifestSize;

    if (!LocateFileAccessManifest(/*out*/ manifest, /*out*/ manifestSize)) {
        HandleDetoursInjectionAndCommunicationErrors(
            DETOURS_NO_PAYLOAD_FOUND_8,
            L"LocateAndParseFileAccessManifest: Failed to find payload coming from Detours",
            DETOURS_WINDOWS_LOG_MESSAGE_8);
        return false;
    }

    return ParseFileAccessManifest(manifest, manifestSize);
}

SpecialProcessKind  g_ProcessKind = SpecialProcessKind::NotSpecial;

void InitProcessKind()
{
    struct ProcessPair {
        LPCWSTR Name;
        SpecialProcessKind Kind;
    };

    // This list must be kept in sync with those in C# SandboxedProcessPipExecutor.cs
    const struct ProcessPair pairs[] = {
            { L"csc.exe", SpecialProcessKind::Csc },
            { L"rc.exe", SpecialProcessKind::RC },
            { L"mt.exe", SpecialProcessKind::Mt },
            { L"cvtres.exe", SpecialProcessKind::Cvtres },
            { L"resonexe.exe", SpecialProcessKind::Resonexe},
            { L"windbg.exe", SpecialProcessKind::WinDbg },
            { L"ccrewrite.exe", SpecialProcessKind::CCRewrite },
            { L"cccheck.exe", SpecialProcessKind::CCCheck },
            { L"ccrefgen.exe", SpecialProcessKind::CCRefGen },
            { L"ccdocgen.exe", SpecialProcessKind::CCDocGen } };

    size_t count = sizeof(pairs) / sizeof(pairs[0]);

    WCHAR wszFileName[MAX_PATH];
    DWORD nFileName = GetModuleFileNameW(NULL, wszFileName, MAX_PATH);
    if (nFileName == 0 || nFileName == MAX_PATH) {
        return;
    }

    for (size_t i = 0; i < count; i++) {
        if (HasSuffix(wszFileName, nFileName, pairs[i].Name)) {
            g_ProcessKind = pairs[i].Kind;
            return;
        }
    }
}

void ReportIfNeeded(AccessCheckResult const& checkResult, FileOperationContext const& context, PolicyResult const& policyResult, DWORD error, USN usn, wchar_t const* filter) {
    if (!checkResult.ShouldReport()) {
        return;
    }

    ReportFileAccess(
        context,
        checkResult.GetFileAccessStatus(),
        policyResult,
        checkResult,
        error,
        usn,
        filter);
}

bool EnumerateDirectory(
    const std::wstring& directoryPath,
    const std::wstring& filter,
    bool recursive,
    bool treatReparsePointAsFile,
    _Inout_ std::vector<std::pair<std::wstring, DWORD>>& filesAndDirectories)
{
    HANDLE hFind = INVALID_HANDLE_VALUE;
    WIN32_FIND_DATA ffd;
    std::stack<std::wstring> directoriesToEnumerate;

    directoriesToEnumerate.push(directoryPath);
    filesAndDirectories.clear();

    while (!directoriesToEnumerate.empty()) {
        std::wstring directoryToEnumerate = directoriesToEnumerate.top();
        std::wstring spec = PathCombine(directoryToEnumerate, filter.c_str());

        directoriesToEnumerate.pop();

        hFind = FindFirstFileW(NormalizePath(spec).c_str(), &ffd);
        if (hFind == INVALID_HANDLE_VALUE) {
            return false;
        }

        do {
            if (wcscmp(ffd.cFileName, L".") != 0 &&
                wcscmp(ffd.cFileName, L"..") != 0) {

                std::wstring path = PathCombine(directoryToEnumerate, ffd.cFileName);

                filesAndDirectories.push_back(std::make_pair(path, ffd.dwFileAttributes));

                if (recursive) {

                    bool isDirectory = (ffd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;

                    if (isDirectory && treatReparsePointAsFile) {
                        isDirectory = (ffd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0;
                    }

                    if (isDirectory) {
                        directoriesToEnumerate.push(path);
                    }
                }
            }
        } while (FindNextFile(hFind, &ffd) != 0);

        if (GetLastError() != ERROR_NO_MORE_FILES) {
            FindClose(hFind);
            return false;
        }

        FindClose(hFind);
        hFind = INVALID_HANDLE_VALUE;
    }

    return true;
}

bool ExistsAsFile(_In_ PCWSTR path)
{
    DWORD dwAttrib = GetFileAttributesW(path);

    return (dwAttrib != INVALID_FILE_ATTRIBUTES && !(dwAttrib & FILE_ATTRIBUTE_DIRECTORY));
}

static DWORD SearchFullPath(
    _In_ LPCWSTR lpPath,
    _In_ LPCWSTR lpFileName,
    _In_ LPCWSTR lpExtension,
    _Inout_ std::wstring& fullPath)
{
    // First, we try with a fixed-sized buffer, which should be good enough for all practical cases.

    wchar_t wszBuffer[MAX_PATH];
    DWORD nBufferLength = std::extent<decltype(wszBuffer)>::value;
    LPWSTR filePart;

    DWORD result = SearchPathW(lpPath, lpFileName, lpExtension, nBufferLength, wszBuffer, &filePart);

    if (result == 0)
    {
        DWORD ret = GetLastError();
        return ret;
    }

    if (result < nBufferLength)
    {
        fullPath.assign(wszBuffer, static_cast<size_t>(result));
    }
    else
    {
        // Second, if that buffer wasn't big enough, we try again with a dynamically allocated buffer with sufficient size.

        // Note that in this case, the return value indicates the required buffer length, INCLUDING the terminating null character.
        // https://docs.microsoft.com/en-us/windows/win32/api/processenv/nf-processenv-searchpathw
        unique_ptr<wchar_t[]> buffer(new wchar_t[result]);
        assert(buffer.get());

        DWORD result2 = SearchPathW(lpPath, lpFileName, lpExtension, result, buffer.get(), &filePart);

        if (result2 == 0)
        {
            DWORD ret = GetLastError();
            return ret;
        }

        if (result2 < result)
        {
            fullPath.assign(buffer.get(), result2);
        }
        else
        {
            return ERROR_NOT_ENOUGH_MEMORY;
        }
    }

    return ERROR_SUCCESS;
}

static bool ExistsImageFile(_In_ CanonicalizedPath& candidatePath)
{
    if (candidatePath.IsNull())
    {
        return false;
    }

    return ExistsAsFile(candidatePath.GetPathString());
}

static bool TryFindImagePath(_In_ std::wstring& candidatePath, _Out_opt_ CanonicalizedPath& imagePath)
{
    imagePath = CanonicalizedPath::Canonicalize(candidatePath.c_str());
    if (ExistsImageFile(imagePath))
    {
        return true;
    }

    if (HasSuffix(candidatePath.c_str(), candidatePath.length(), L".exe"))
    {
        // Candidate path has .exe already, and it does not exist.
        return false;
    }

    std::wstring candidatePathExe(candidatePath);
    candidatePathExe.append(L".exe");
    imagePath = CanonicalizedPath::Canonicalize(candidatePathExe.c_str());

    return ExistsImageFile(imagePath);
}

static CanonicalizedPath GetCanonicalizedApplicationPath(_In_ LPCWSTR lpApplicationName)
{
    if (GetRootLength(lpApplicationName) > 0)
    {
        // Path is rooted.
        return CanonicalizedPath::Canonicalize(lpApplicationName);
    }

    // Path is not rooted.
    // For example, lpApplicationName can be just "cmd.exe". In this case, we rely on SearchPathW
    // to find the full path. We cannot rely on GetFullPathNameW (as in CanonicalizedPath) because
    // GetFullPathNameW will simply prepend the file name with the current directory, which result in
    // a non-existent path for executables like "cmd.exe".
    std::wstring applicationPath;
    return SearchFullPath(nullptr, lpApplicationName, L".exe", applicationPath) != ERROR_SUCCESS
        ? CanonicalizedPath()
        : CanonicalizedPath::Canonicalize(applicationPath.c_str());;
}

CanonicalizedPath GetImagePath(_In_opt_ LPCWSTR lpApplicationName, _In_opt_ LPWSTR lpCommandLine)
{
    if (lpApplicationName != nullptr)
    {
        return GetCanonicalizedApplicationPath(lpApplicationName);
    }

    if (lpCommandLine == nullptr)
    {
        return CanonicalizedPath();
    }

    LPWSTR cursor = lpCommandLine;
    LPWSTR start = lpCommandLine;
    size_t length = 0;
    std::wstring applicationNamePath(L"");

    if (*cursor == L'\"')
    {
        start = ++cursor;

        while (*cursor && *cursor != L'\"')
        {
            ++cursor;
            ++length;
        }

        // Unlike the implementation of CreateProcessW that runs the expanded path logic (as in the else branch below),
        // we simply search for the ending quote and use the found path as the application path.
        // We do this because we don't want to slow down 99% cases by going to the file system to check file existence.
        applicationNamePath.assign(start, length);
        return GetCanonicalizedApplicationPath(applicationNamePath.c_str());
    }
    else
    {
        // Skip past space and tab.
        while (*cursor && (*cursor == L' ' || *cursor == L'\t'))
        {
            ++cursor;
        }

        do
        {
            start = cursor;
            length = 0;

            // Skip past space and tab.
            while (*cursor && (*cursor == L' ' || *cursor == L'\t'))
            {
                ++cursor;
                ++length;
            }

            // Look for the first whitespace/tab.
            while (*cursor && *cursor != L' ' && *cursor != L'\t')
            {
                ++cursor;
                ++length;
            }

            CanonicalizedPath imagePath;
            applicationNamePath.append(start, length);

            if (GetRootLength(applicationNamePath.c_str()) > 0)
            {
                if (TryFindImagePath(applicationNamePath, imagePath))
                {
                    return imagePath;
                }
            }
            else
            {
                // For non-rooted path, check path existence using SearchFullPath.
                imagePath = GetCanonicalizedApplicationPath(applicationNamePath.c_str());
                if (!imagePath.IsNull())
                {
                    return imagePath;
                }
            }
        } while (*cursor);

        return CanonicalizedPath();
    }
}