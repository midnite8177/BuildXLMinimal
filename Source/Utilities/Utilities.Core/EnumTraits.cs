// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.Reflection;

namespace BuildXL.Utilities.Core
{
    /// <summary>
    /// Traits for an enum type (validation, int casting, etc.)
    /// These traits may only be instantiated for enum types which are bijective; no two enum constants may have the same
    /// value.
    /// </summary>
    public static class EnumTraits<TEnum>
        where TEnum : System.Enum
    {
        private static readonly Dictionary<ulong, TEnum> s_integerToValue;
        private static readonly Dictionary<TEnum, ulong> s_valueToInteger;
        private static readonly ulong s_allFlags;

        static EnumTraits()
        {
            Contract.Assume(typeof(TEnum).GetTypeInfo().IsEnum, "EnumTraits may only be instantiated for an enum type");

            s_integerToValue = new Dictionary<ulong, TEnum>();
            s_valueToInteger = new Dictionary<TEnum, ulong>();

            ulong? max = null;
            ulong? min = null;
            foreach (FieldInfo field in typeof(TEnum).GetFields())
            {
                if (field.IsSpecialName)
                {
                    continue;
                }

                Contract.Assume(field.FieldType == typeof(TEnum));

                ulong intVal = Convert.ToUInt64(field.GetValue(null), CultureInfo.InvariantCulture);

                var val = (TEnum)field.GetValue(null);

                if (!max.HasValue || max.Value < intVal)
                {
                    max = intVal;
                }

                if (!min.HasValue || min.Value > intVal)
                {
                    min = intVal;
                }

                Contract.Assume(!s_integerToValue.ContainsKey(intVal), "Two enum values have the same integer representation.");
                s_integerToValue.Add(intVal, val);
                s_valueToInteger.Add(val, intVal);
                s_allFlags |= intVal;
            }

            MinValue = min ?? 0;
            MaxValue = max ?? 0;
        }

        /// <summary>
        /// Gets whether flags exist for all bits set in the integral value of the enum
        /// </summary>
        public static bool AreFlagsDefined(ulong value)
        {
            return (s_allFlags & value) == value;
        }

        /// <summary>
        /// Gets the count of values for the enum
        /// </summary>
        public static int ValueCount => s_valueToInteger.Count;

        /// <summary>
        /// Minimum integer value corresponding to an enum constant.
        /// </summary>
        public static ulong MinValue { get; }

        /// <summary>
        /// Maximum integer value corresponding to an enum constant.
        /// </summary>
        public static ulong MaxValue { get; }

        /// <summary>
        /// Returns an enumerable for all values of the enum.
        /// </summary>
        public static IEnumerable<TEnum> EnumerateValues()
        {
            return s_valueToInteger.Keys;
        }

        /// <summary>
        /// Tries to return the enum constant for a given integer value.
        /// </summary>
        /// <remarks>
        /// This conversion will fail if <paramref name="intValue" /> doesn't correspond to a declared constant,
        /// such as if it represents a combination of flags.
        /// </remarks>
        public static bool TryConvert(ulong intValue, out TEnum value)
        {
            return s_integerToValue.TryGetValue(intValue, out value);
        }

        /// <summary>
        /// Tries to return the enum constant for a given integer value.
        /// </summary>
        /// <remarks>
        /// This conversion will fail if <paramref name="intValue" /> doesn't correspond to a declared constant,
        /// such as if it represents a combination of flags.
        /// </remarks>
        public static bool TryConvert(long intValue, out TEnum value)
        {
            return s_integerToValue.TryGetValue(unchecked((ulong)intValue), out value);
        }

        /// <summary>
        /// Returns the integer value for a given enum constant.
        /// </summary>
        /// <remarks>
        /// It is an error to attempt this conversion if <paramref name="value" /> is not a declared constant,
        /// such as if it represents a combination of flags.
        /// </remarks>
        public static ulong ToInteger(TEnum value)
        {
            return s_valueToInteger[value];
        }
    }
}
