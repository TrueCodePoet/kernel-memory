// Copyright (c) Microsoft. All rights reserved.

// using System; // Removed unnecessary using directive
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDb;

/// <summary>
/// Extension methods for MemoryRecord.
/// </summary>
internal static class MemoryRecordExtensions
{
    /// <summary>
    /// Gets the file ID from a memory record.
    /// </summary>
    /// <param name="record">The memory record.</param>
    /// <returns>The file ID.</returns>
    public static string GetFileId(this MemoryRecord record)
    {
        // Use the record ID as the file ID if no file ID is available
        return record.Id;
    }
}
