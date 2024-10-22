// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma once

#include <unordered_set>
#include <cwctype>
#include <algorithm>
#include "DataTypes.h"

// Case-insensitive equality for wstrings
struct CaseInsensitiveStringComparer {
    bool operator()(const std::wstring& lhs, const std::wstring& rhs) const {
        if (lhs.length() == rhs.length()) {
            
            // If the strings happen to be identical, then we can just return
            if (lhs.data() == rhs.data())
            {
                return true;
            }

            return std::equal(rhs.begin(), rhs.end(), lhs.begin(),
                [](const wchar_t a, const wchar_t b)
                {
                    if (a == b)
                    {
                        return true;
                    }

                    return towlower(a) == towlower(b);
                });
        }
        else {
            return false;
        }
    }
};

// Case-insensitive 'less than' for wstrings. Uses a lexicographical comparison on lowercased characters
// for same-size strings and uses length to order the rest. Observe we just need a total order among strings
// such that is case-insensitive, so using length for different-size strings makes the comparison faster
struct CaseInsensitiveStringLessThan {
    bool operator()(const std::wstring& lhs, const std::wstring& rhs) const {
        if (lhs.length() == rhs.length())
        {
            // If the strings happen to be identical, then we can just return
            if (lhs.data() == rhs.data())
            {
                return false;
            }

            // Paths in the same process tend to share a significant prefix in common. Starting backwards
            // has a better chance to hit a difference first
            const auto result = std::lexicographical_compare(rhs.rbegin(), rhs.rend(), lhs.rbegin(), lhs.rend(),
                [](const wchar_t a, const wchar_t b)
                {
                    if (a == b)
                    {
                        return false;
                    }

                    return towlower(a) < towlower(b);
                });
           return result;
        }
        else
        {
            return lhs.length() < rhs.length();
        }
    }
};

// Case-insensitive hasher for wstrings
struct CaseInsensitiveStringHasher {
    size_t operator()(const std::wstring& str) const {
        std::wstring lowerstr(str);
        std::transform(lowerstr.begin(), lowerstr.end(), lowerstr.begin(), towlower);

        return std::hash<std::wstring>()(lowerstr);
    }
};
