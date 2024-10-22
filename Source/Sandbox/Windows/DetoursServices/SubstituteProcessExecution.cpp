// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#include "stdafx.h"

#include <cwctype>

#include "DebuggingHelpers.h"
#include "DetouredFunctions.h"
#include "DetoursHelpers.h"
#include "DetoursServices.h"
#include "FileAccessHelpers.h"
#include "StringOperations.h"
#include "UnicodeConverter.h"
#include "SubstituteProcessExecution.h"

using std::wstring;
using std::unique_ptr;
using std::vector;

/// Runs an injected substitute shim instead of the actual child process, passing the
/// original command and arguments to the shim along with, implicitly,
/// the current working directory and environment.
static BOOL WINAPI InjectShim(
    wstring               &commandWithoutQuotes,
    wstring               &argumentsWithoutCommand,
    LPSECURITY_ATTRIBUTES lpProcessAttributes,
    LPSECURITY_ATTRIBUTES lpThreadAttributes,
    BOOL                  bInheritHandles,
    DWORD                 dwCreationFlags,
    LPVOID                lpEnvironment,
    LPCWSTR               lpCurrentDirectory,
    LPSTARTUPINFOW        lpStartupInfo,
    LPPROCESS_INFORMATION lpProcessInformation)
{
    // Create a final buffer for the original command line - we prepend the original command
    // (if present) in quotes for easier parsing in the shim, ahead of the original argument list if provided.
    // This is an over-allocation because if lpCommandLine is non-null, lpCommandLine starts with
    // the contents of lpApplicationName, which we'll remove and replace with a quoted version.
    size_t fullCmdLineSizeInChars =
        commandWithoutQuotes.length() + argumentsWithoutCommand.length() +
        4;  // Command quotes and space and trailing null
    wchar_t *fullCommandLine = new wchar_t[fullCmdLineSizeInChars];
    if (fullCommandLine == nullptr)
    {
        Dbg(L"Failure running substitute shim process - failed to allocate buffer of size %d.", fullCmdLineSizeInChars * sizeof(WCHAR));
        SetLastError(ERROR_OUTOFMEMORY);
        return FALSE;
    }

    fullCommandLine[0] = L'"';
    wcscpy_s(fullCommandLine + 1, fullCmdLineSizeInChars, commandWithoutQuotes.c_str());
    wcscat_s(fullCommandLine, fullCmdLineSizeInChars, L"\" ");
    wcscat_s(fullCommandLine, fullCmdLineSizeInChars, argumentsWithoutCommand.c_str());

    Dbg(L"Injecting substitute shim '%s' for process command line '%s'", g_SubstituteProcessExecutionShimPath, fullCommandLine);
    BOOL rv = Real_CreateProcessW(
        /*lpApplicationName:*/ g_SubstituteProcessExecutionShimPath,
        /*lpCommandLine:*/ fullCommandLine,
        lpProcessAttributes,
        lpThreadAttributes,
        bInheritHandles,
        dwCreationFlags,
        lpEnvironment,
        lpCurrentDirectory,
        lpStartupInfo,
        lpProcessInformation);

    delete[] fullCommandLine;
    return rv;
}

// trim from start (in place)
// https://stackoverflow.com/questions/216823/whats-the-best-way-to-trim-stdstring
static inline void ltrim_inplace(std::wstring& s) {
    s.erase(s.begin(), std::find_if(s.begin(), s.end(), [](wchar_t ch) {
        return !std::iswspace(ch);
        }));
}

// trim from end (in place)
// https://stackoverflow.com/questions/216823/whats-the-best-way-to-trim-stdstring
static inline void rtrim_inplace(std::wstring& s) {
    s.erase(std::find_if(s.rbegin(), s.rend(), [](wchar_t ch) {
        return !std::iswspace(ch);
        }).base(), s.end());
}

// trim from both ends (in place)
// https://stackoverflow.com/questions/216823/whats-the-best-way-to-trim-stdstring
static inline void trim_inplace(std::wstring& s) {
    ltrim_inplace(s);
    rtrim_inplace(s);
}

// Returns in 'command' the command from lpCommandLine without quotes, and in commandArgs the arguments from the remainder of the string.
void FindApplicationNameFromCommandLine(const wchar_t *lpCommandLine, _Out_ std::wstring &command, _Out_ std::wstring &commandArgs)
{
    wstring fullCommandLine(lpCommandLine);
    if (fullCommandLine.length() == 0)
    {
        command = wstring();
        commandArgs = wstring();
        return;
    }

    size_t argStartIndex;
    const size_t fullCommandLineLength = fullCommandLine.length();

    if (fullCommandLine[0] == L'"')
    {
        // Find the close quote. Might not be present which means the command
        // is the full command line minus the initial quote.
        size_t closeQuoteIndex = fullCommandLine.find(L'"', 1);
        if (closeQuoteIndex == wstring::npos)
        {
            // No close quote. Take everything through the end of the command line as the command.
            command = fullCommandLine.substr(1);
            trim_inplace(command);
            commandArgs = wstring();
            argStartIndex = fullCommandLineLength;
        }
        else
        {
            if (closeQuoteIndex == fullCommandLine.length() - 1)
            {
                // Quotes cover entire command line.
                command = fullCommandLine.substr(1, fullCommandLine.length() - 2);
                argStartIndex = fullCommandLineLength;
            }
            else
            {
                wstring noQuoteCommand = fullCommandLine.substr(1, closeQuoteIndex - 1);

                // Find the next delimiting space after the close double-quote.
                // For example a command like "c:\program files"\foo we need to
                // keep \foo and cut the quotes to produce c:\program files\foo
                size_t spaceDelimiterIndex = fullCommandLine.find(L' ', closeQuoteIndex + 1);
                if (spaceDelimiterIndex == wstring::npos)
                {
                    // No space, take everything through the end of the command line.
                    spaceDelimiterIndex = fullCommandLineLength;
                }

                command = (noQuoteCommand +
                    fullCommandLine.substr(closeQuoteIndex + 1, spaceDelimiterIndex - closeQuoteIndex - 1));

                argStartIndex = spaceDelimiterIndex + 1;
            }
        }
    }
    else
    {
        // No open quote, pure space delimiter.
        size_t spaceDelimiterIndex = fullCommandLine.find(L' ');
        if (spaceDelimiterIndex == wstring::npos)
        {
            // No space, take everything through the end of the command line.
            spaceDelimiterIndex = fullCommandLineLength;
        }

        command = fullCommandLine.substr(0, spaceDelimiterIndex);
        argStartIndex = spaceDelimiterIndex + 1;
    }

    trim_inplace(command);

    if (argStartIndex < fullCommandLineLength)
    {
        commandArgs = fullCommandLine.substr(argStartIndex);
        trim_inplace(commandArgs);
    }
    else
    {
        commandArgs = wstring();
    }
}

static bool CommandArgsContainMatch(const wchar_t *commandArgs, const wchar_t *argMatch)
{
    if (argMatch == nullptr)
    {
        // No optional match, meaning always match.
        return true;
    }

    return wcsstr(commandArgs, argMatch) != nullptr;
}

static bool CallPluginFunc(
    const wstring& command,
    const wstring& commandArgs,
    LPVOID lpEnvironment,
    LPCWSTR lpWorkingDirectory,
    LPWSTR* modifiedArguments)
{
    assert(g_SubstituteProcessExecutionPluginFunc != nullptr);

    if (lpEnvironment == nullptr)
    {
        lpEnvironment = GetEnvironmentStrings();
    }

    wchar_t curDir[MAX_PATH];
    if (lpWorkingDirectory == nullptr)
    {
        GetCurrentDirectory(ARRAYSIZE(curDir), curDir);
        lpWorkingDirectory = curDir;
    }

    return g_SubstituteProcessExecutionPluginFunc(
        command.c_str(),
        commandArgs.c_str(),
        lpEnvironment,
        lpWorkingDirectory,
        modifiedArguments,
        Dbg) != 0;
}

static bool ShouldSubstituteShim(
    const wstring &command,
    const wstring& commandArgs,
    LPVOID lpEnvironment,
    LPCWSTR lpWorkingDirectory,
    LPWSTR* modifiedArguments)
{
    assert(g_SubstituteProcessExecutionShimPath != nullptr);

    // Easy cases.
    if (g_pShimProcessMatches == nullptr || g_pShimProcessMatches->empty())
    {
        if (g_SubstituteProcessExecutionPluginFunc != nullptr)
        {
            // Filter meaning is exclusive if we're shimming all processes, inclusive otherwise.
            bool filterMatch = CallPluginFunc(command, commandArgs, lpEnvironment, lpWorkingDirectory, modifiedArguments);

            Dbg(L"Shim: Empty matches command='%s', args='%s', filterMatch=%d, g_ProcessExecutionShimAllProcesses=%d", command.c_str(), commandArgs.c_str(), filterMatch, g_ProcessExecutionShimAllProcesses);

            return filterMatch != g_ProcessExecutionShimAllProcesses;
        }

        Dbg(L"Shim: Empty matches command='%s', args='%s', g_ProcessExecutionShimAllProcesses=%d", command.c_str(), commandArgs.c_str(), g_ProcessExecutionShimAllProcesses);

        // Shim everything or shim nothing if there are no matches to compare and no filter DLL.
        return g_ProcessExecutionShimAllProcesses;
    }

    size_t commandLen = command.length();

    bool foundMatch = false;

    for (std::vector<ShimProcessMatch*>::iterator it = g_pShimProcessMatches->begin(); it != g_pShimProcessMatches->end(); ++it)
    {
        ShimProcessMatch* pMatch = *it;

        const wchar_t* processName = pMatch->ProcessName.get();
        size_t processLen = wcslen(processName);

        // lpAppName is longer than e.g. "cmd.exe", see if lpAppName ends with e.g. "\cmd.exe"
        if (processLen < commandLen)
        {
            if (command[commandLen - processLen - 1] == L'\\' &&
                _wcsicmp(command.c_str() + commandLen - processLen, processName) == 0)
            {
                if (CommandArgsContainMatch(commandArgs.c_str(), pMatch->ArgumentMatch.get()))
                {
                    foundMatch = true;
                    break;
                }
            }

            continue;
        }

        if (processLen == commandLen)
        {
            if (_wcsicmp(processName, command.c_str()) == 0)
            {
                if (CommandArgsContainMatch(commandArgs.c_str(), pMatch->ArgumentMatch.get()))
                {
                    foundMatch = true;
                    break;
                }
            }
        }
    }

    // Filter meaning is exclusive if we're shimming all processes, inclusive otherwise.
    bool filterMatch = !g_ProcessExecutionShimAllProcesses;

    if (foundMatch)
    {
        // Refine match by calling plugin.
        if (g_SubstituteProcessExecutionPluginFunc != nullptr)
        {
            filterMatch = CallPluginFunc(command, commandArgs, lpEnvironment, lpWorkingDirectory, modifiedArguments) != 0;
        }
    }

    Dbg(L"Shim: Non-empty matches command='%s', args='%s', foundMatch=%d, filterMatch=%d, g_ProcessExecutionShimAllProcesses=%d",
        command.c_str(),
        commandArgs.c_str(),
        foundMatch,
        filterMatch,
        g_ProcessExecutionShimAllProcesses);

    // When ShimAllProcesses is false,
    //     shim a process if a match is found, and the match is filtered in (filterMatch: true) by the plugin, when the plugin exists.
    // When ShimAllProecsses is true,
    //     shim a process if no match is found, or, if a match is found, it is filtered out (filterMatch: false) by the plugin, when the plugin exists.
    return !g_ProcessExecutionShimAllProcesses
        ? foundMatch && filterMatch
        : !foundMatch || !filterMatch;
 }

void WINAPI FreeModifiedArguments(LPWSTR modifiedArguments)
{
    if (modifiedArguments == nullptr)
    {
        return;
    }

    HANDLE hDefaultProcessHeap = GetProcessHeap();

    if (hDefaultProcessHeap == NULL)
    {
        Dbg(L"Shim: Failed to retrieve the default process heap with LastError %d", GetLastError());
    }
    else if (HeapFree(hDefaultProcessHeap, 0, (LPVOID)modifiedArguments) == FALSE)
    {
        Dbg(L"Shim: Failed to free allocation of modified arguments from default process heap");
    }
}

BOOL WINAPI MaybeInjectSubstituteProcessShim(
    _In_opt_    LPCWSTR               lpApplicationName,
    _In_opt_    LPCWSTR               lpCommandLine,
    _In_opt_    LPSECURITY_ATTRIBUTES lpProcessAttributes,
    _In_opt_    LPSECURITY_ATTRIBUTES lpThreadAttributes,
    _In_        BOOL                  bInheritHandles,
    _In_        DWORD                 dwCreationFlags,
    _In_opt_    LPVOID                lpEnvironment,
    _In_opt_    LPCWSTR               lpCurrentDirectory,
    _In_        LPSTARTUPINFOW        lpStartupInfo,
    _Out_       LPPROCESS_INFORMATION lpProcessInformation,
    _Out_       bool&                 injectedShim)
{
    if (g_SubstituteProcessExecutionShimPath == nullptr)
    {
        return FALSE;
    }

    if (lpCommandLine == nullptr && lpApplicationName == nullptr)
    {
        Dbg(L"Shim: Not inject shim because command line and application name are not found");
    }

    // When lpCommandLine is null we just use lpApplicationName as the command line to parse.
    // When lpCommandLine is not null, it contains the command, possibly with quotes containing spaces,
    // as the first whitespace-delimited token; we can ignore lpApplicationName in this case.
    Dbg(L"Shim: Finding command and args from lpApplicationName='%s', lpCommandLine='%s'", lpApplicationName, lpCommandLine);
    LPCWSTR cmdLine = lpCommandLine == nullptr ? lpApplicationName : lpCommandLine;
    wstring command;
    wstring commandArgs;
    FindApplicationNameFromCommandLine(cmdLine, command, commandArgs);
    Dbg(L"Shim: Found command='%s', args='%s' from lpApplicationName='%s', lpCommandLine='%s'", command.c_str(), commandArgs.c_str(), lpApplicationName, lpCommandLine);

    LPWSTR modifiedArguments = nullptr;
    
    if (ShouldSubstituteShim(command, commandArgs, lpEnvironment, lpCurrentDirectory, &modifiedArguments))
    {
        // Instead of Detouring the child, run the requested shim
        // passing the original command line, but only for appropriate commands.

        if (modifiedArguments != nullptr)
        {
            Dbg(L"Shim: Modified arguments command='%s', args='%s', modifedArgs:'%s'", command.c_str(), commandArgs.c_str(), modifiedArguments);

            commandArgs.assign(modifiedArguments);
            FreeModifiedArguments(modifiedArguments);
        }

        Dbg(L"Shim: Inject shim command='%s', args='%s'", command.c_str(), commandArgs.c_str());

        injectedShim = true;
        return InjectShim(
            command,
            commandArgs,
            lpProcessAttributes,
            lpThreadAttributes,
            bInheritHandles,
            dwCreationFlags,
            lpEnvironment,
            lpCurrentDirectory,
            lpStartupInfo,
            lpProcessInformation);
    }

    FreeModifiedArguments(modifiedArguments);

    Dbg(L"Shim: Not substitute command='%s', args='%s'", command.c_str(), commandArgs.c_str());
    
    injectedShim = false;
    return FALSE;
}
