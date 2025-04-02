// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

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
    internal static readonly JsonSerializerOptions DefaultJsonSerializerOptions;

    /// <summary>
    /// Vector embedding policy for the container.
    /// </summary>
    private static readonly VectorEmbeddingPolicy VectorEmbeddingPolicy = new(
    [
        new Embedding
        {
            DataType = VectorDataType.Float32,
            Dimensions = 1536,
            DistanceFunction = DistanceFunction.Cosine,
            Path = $"/{AzureCosmosDbTabularMemoryRecord.VectorField}",
        }
    ]);

    /// <summary>
    /// Indexing policy for the container.
    /// </summary>
    private static readonly IndexingPolicy IndexingPolicy = new()
    {
        VectorIndexes =
        [
            new()
            {
                Path = $"/{AzureCosmosDbTabularMemoryRecord.VectorField}",
                Type = VectorIndexType.QuantizedFlat,
            }
        ],
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
            $"/{AzureCosmosDbTabularMemoryRecord.FileField}")
        {
            VectorEmbeddingPolicy = VectorEmbeddingPolicy,
            IndexingPolicy = IndexingPolicy,
        };

        // Include all paths in the indexing policy
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });

        // Exclude the vector field from indexing (it's handled by the vector index)
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = $"/{AzureCosmosDbTabularMemoryRecord.VectorField}/*" });

        // Ensure the data field is indexed for efficient querying
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = $"/{AzureCosmosDbTabularMemoryRecord.DataField}/*" });

        return properties;
    }

    /// <summary>
    /// Static constructor to initialize the default JSON serializer options.
    /// </summary>
    static AzureCosmosDbTabularConfig()
    {
        var jsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
        jsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        DefaultJsonSerializerOptions = jsonSerializerOptions;
    }
}
