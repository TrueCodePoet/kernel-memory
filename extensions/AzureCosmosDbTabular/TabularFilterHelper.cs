// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Helper class for AI-driven filtering of tabular data.
/// </summary>
public class TabularFilterHelper
{
    private readonly IKernelMemory _memory;
    private readonly ILogger<TabularFilterHelper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TabularFilterHelper"/> class.
    /// </summary>
    /// <param name="memory">The kernel memory instance.</param>
    /// <param name="logger">Optional logger.</param>
    public TabularFilterHelper(
        IKernelMemory memory,
        ILogger<TabularFilterHelper>? logger = null)
    {
        this._memory = memory;
        this._logger = logger ?? Microsoft.KernelMemory.Diagnostics.DefaultLogger.Factory.CreateLogger<TabularFilterHelper>();
    }

    /// <summary>
    /// Gets the filterable fields from the index.
    /// </summary>
    /// <param name="indexName">The index name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A dictionary of field types and their available field names.</returns>
    public async Task<Dictionary<string, HashSet<string>>> GetFilterableFieldsAsync(
        string indexName,
        CancellationToken cancellationToken = default)
    {
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // Get the filterable fields
        return await memoryDb.GetFilterableFieldsAsync(indexName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the top values for a specific field.
    /// </summary>
    /// <param name="indexName">The index name.</param>
    /// <param name="fieldType">The field type (tag or data).</param>
    /// <param name="fieldName">The field name.</param>
    /// <param name="limit">The maximum number of values to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of field values and their occurrence counts.</returns>
    public async Task<List<(string Value, int Count)>> GetTopFieldValuesAsync(
        string indexName,
        string fieldType,
        string fieldName,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        // Get the memory DB instance
        var memoryDb = GetTabularMemoryDb();

        // Get the top field values
        return await memoryDb.GetTopFieldValuesAsync(indexName, fieldType, fieldName, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Generates a filter based on field and value.
    /// </summary>
    /// <param name="fieldType">The field type (tag or data).</param>
    /// <param name="fieldName">The field name.</param>
    /// <param name="value">The value to filter for.</param>
    /// <returns>The generated memory filter.</returns>
    public MemoryFilter GenerateFilter(string fieldType, string fieldName, string value)
    {
        var filter = new MemoryFilter();

        if (fieldType == "tag")
        {
            filter.Add(fieldName, value);
        }
        else // data field
        {
            filter.Add($"data.{fieldName}", value);
        }

        return filter;
    }

    /// <summary>
    /// Gets the tabular memory DB instance.
    /// </summary>
    /// <returns>The tabular memory DB instance.</returns>
    private AzureCosmosDbTabularMemory GetTabularMemoryDb()
    {
        // Get the memory DB instance using reflection
        var memoryDbField = this._memory.GetType().GetField("_memoryDb", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (memoryDbField == null)
        {
            throw new InvalidOperationException("Could not access the memory DB instance.");
        }

        var memoryDb = memoryDbField.GetValue(this._memory);
        if (memoryDb is not AzureCosmosDbTabularMemory tabularMemoryDb)
        {
            throw new InvalidOperationException("The memory DB is not an AzureCosmosDbTabularMemory instance.");
        }

        return tabularMemoryDb;
    }
}
