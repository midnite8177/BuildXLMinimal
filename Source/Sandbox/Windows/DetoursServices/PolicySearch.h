// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#ifndef policysearch_h
#define policysearch_h

#include "DataTypes.h"

// ----------------------------------------------------------------------------
// Manifest policy tree search
// ----------------------------------------------------------------------------

// Represents the continuation state of a search for a policy (via FindFileAccessPolicyInTree).
// When a search completes, the resulting cursor allows a subsequent search rooted beneath the
// already-found policy - i.e., Find(<root cursor>, "C:\foo") -> Cursor ; Find(Cursor, "bar") is
// equivalent to Find("C:\foo\bar"); but repeated work is saved and the original path is not needed.
struct PolicySearchCursor {
#if _WIN32
    typedef std::shared_ptr<PolicySearchCursor> PPolicySearchCursor;
#define MakePPolicySearchCursor(cursor) std::make_shared<PolicySearchCursor>(cursor)
#else
    typedef PolicySearchCursor* PPolicySearchCursor;
#define MakePPolicySearchCursor(cursor) nullptr
#endif

    PolicySearchCursor()
        : Record(nullptr), Level(0), Parent(nullptr), SearchWasTruncated(true)
    {
        assert(!IsValid());
    };

    // Implicit conversion constructor to start a search from a manifest record.
    PolicySearchCursor(ManifestRecord const* record)
        : Record(record), Level(0), Parent(nullptr), SearchWasTruncated(false)
    {
        assert(record != nullptr);
    }

    PolicySearchCursor(ManifestRecord const* record, size_t level, PPolicySearchCursor parent)
        : Record(record), Level(level), Parent(parent), SearchWasTruncated(false)
    { 
        assert(record != nullptr);
    }

    PolicySearchCursor(ManifestRecord const* record, size_t level, PPolicySearchCursor parent, bool searchWasTruncated)
        : Record(record), Level(level), Parent(parent), SearchWasTruncated(searchWasTruncated)
    { 
        assert(record != nullptr);
    }

    // Gets the expected USN corresponding to this match. Returns -1 if this match was not for the complete
    // path (and so a USN is not known) or if the cursor is invalid.
    USN GetExpectedUsn() const {
        if (SearchWasTruncated || !IsValid()) {
            return -1;
        }
        else {
            return Record->GetExpectedUsn();
        }
    }
    
    // Indicates if this cursor is valid. The Record field of an invalid cursor should not be used.
    bool IsValid() const {
        return Record != nullptr;
    }

    ManifestRecord const* Record;

    // The level of the paths contained under this record.
    // d: is level 1, d:\a is level 2, d:\a\b is level 3, etc...
    size_t Level;

    PPolicySearchCursor Parent;

    // Indicates if the search generating this cursor was truncated due to reaching the bottom of the tree.
    // A search for "C:\foo\A" in a tree containing only the leaf C:\foo\B will point to the C:\foo record, but will
    // be marked truncated. Resuming a search for "B" should still return C:\foo (for a hypothetical C:\foo\A\B) rather
    // than matching to C:\foo\B.
    bool SearchWasTruncated;
};

// Given a start cursor (which may be the root of a policy tree),
// finds the closest matching policy node for absolutePath.
// The returned cursor allows resuming the search, as if absolutePath had further path components.
PolicySearchCursor FindFileAccessPolicyInTreeEx(
    __in  PolicySearchCursor const& startCursor,
    __in  PCPathChar absolutePath,
    __in  size_t absolutePathLength);

// This is equivalent to FindFileAccessPolicyInTreeEx, but taking just a start record
// rather than a full cursor, and returning only the matched record details rather than a cursor.
// This is a simplified variant for easier C#-side testing.
BOOL WINAPI FindFileAccessPolicyInTree(
    __in  PCManifestRecord record,
    __in  PCPathChar absolutePath,
    __in  size_t absolutePathLength,
	__out FileAccessPolicy& conePolicy,
	__out FileAccessPolicy& nodePolicy,
    __out DWORD& pathId,
    __out USN& expectedUsn);

#endif
