// The row encoding shared by the fingerprints taken over parsed documents rather than over bytes.
//
// Both NetworkFingerprint and OsmFingerprint reduce a document to a set of rows, sort the rows, and
// hash the result, so that re-serialising, reordering or re-stamping the file cannot change the
// answer while a change to what it describes always does. The encoding lives here so the two agree
// on it, and so a third can be added without inventing a third format.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CarlaNet.Map;

/// <summary>
/// Encodes named fields into sortable rows and reduces a set of rows to a SHA-256. Internal: the
/// format is an implementation detail of the fingerprints built on it, not a published record.
/// </summary>
internal static class CanonicalDigest
{
    // Separators are drawn from the control range, which cannot occur in an XML attribute value. A
    // missing field encodes differently from an empty one, so `width=""` and no width at all do not
    // collide.
    private const byte FieldSeparator = 0x1F;
    private const byte RowSeparator = 0x1E;
    private const byte FieldAbsent = 0x00;

    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>One encoded row: the fields, separated, terminated by the row separator.</summary>
    internal static byte[] Row(params string?[] fields)
    {
        using var buffer = new MemoryStream();
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) { buffer.WriteByte(FieldSeparator); }
            if (fields[i] is null)
            {
                buffer.WriteByte(FieldAbsent);
            }
            else
            {
                byte[] encoded = Utf8.GetBytes(fields[i]!);
                buffer.Write(encoded, 0, encoded.Length);
            }
        }
        buffer.WriteByte(RowSeparator);
        return buffer.ToArray();
    }

    /// <summary>Lowercase hexadecimal SHA-256 over the rows, in the order given.</summary>
    internal static string Of(IEnumerable<byte[]> rows)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (byte[] row in rows)
        {
            sha.AppendData(row);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>
    /// Unsigned lexicographic order over raw bytes. Sorting the UTF-8 encoding rather than the
    /// string keeps the order identical in every language that computes one of these fingerprints;
    /// .NET's default string comparison is culture-aware and its ordinal comparison is over UTF-16
    /// code units, neither of which Python reproduces outside the ASCII range.
    /// </summary>
    internal sealed class ByteOrder : IComparer<byte[]>
    {
        internal static readonly ByteOrder Instance = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (left is null) { return right is null ? 0 : -1; }
            if (right is null) { return 1; }
            int shared = Math.Min(left.Length, right.Length);
            for (int i = 0; i < shared; i++)
            {
                if (left[i] != right[i]) { return left[i] < right[i] ? -1 : 1; }
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
