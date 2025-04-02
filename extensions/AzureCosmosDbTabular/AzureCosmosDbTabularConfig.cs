// Copyright (c) Microsoft. All rights reserved.

// using System.Collections.Generic; // Removed unnecessary using directive
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
// using Microsoft.Azure.Cosmos.Serialization; // Removed unnecessary using directive

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Configuration for Azure Cosmos DB Tabular Data connector.
/// </summary>
public sealed class AzureCosmosDbTabularConfig
{
    /// <summary>
    /// Azure Cosmos DB endpoint URL.
    /// </summary>
    [Required] public required string Endpoint { get; init; }

    /// <summary>
    /// Azure Cosmos DB API key.
    /// </summary>
    public string? APIKey { get; init; }

    /// <summary>
    /// Name of the database to use. Defaults to "memory".
    /// </summary>
    public string DatabaseName { get; init; } = "memory";

    /// <summary>
    /// Default JSON serializer options.
    /// </summary>
    internal static readonly JsonSerializerOptions DefaultJsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Gets the container properties for the specified container ID.
    /// </summary>
    /// <param name="containerId">The container ID.</param>
    /// <returns>The container properties.</returns>
    internal static ContainerProperties GetContainerProperties(string containerId)
    {
        var properties = new ContainerProperties(
            containerId,
            $"/{AzureCosmosDbTabularMemoryRecord.FileField}");

        // Include all paths in the indexing policy
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });

        // Exclude the vector field from indexing
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = $"/{AzureCosmosDbTabularMemoryRecord.VectorField}/*" });

        // Ensure the data field is indexed for efficient querying
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = $"/{AzureCosmosDbTabularMemoryRecord.DataField}/*" });

        return properties;
    }

    // Static constructor removed to address CA1810
}
