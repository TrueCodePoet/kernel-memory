// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos; // Already present, but ensuring it's clear
using Microsoft.Azure.Cosmos.Serialization;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDb;

public sealed class AzureCosmosDbConfig
{
    [Required] public required string Endpoint { get; init; }

    public string? APIKey { get; init; }

    /// <summary>
    /// Default JSON serializer options.
    /// </summary>
    internal static readonly JsonSerializerOptions DefaultJsonSerializerOptions;

    /// <summary>
    /// Gets the container properties for the specified container ID.
    /// </summary>
    /// <param name="containerId">The container ID.</param>
    /// <returns>The container properties.</returns>
    internal static ContainerProperties GetContainerProperties(string containerId)
    {
        var properties = new ContainerProperties(
            containerId,
            $"/{AzureCosmosDbMemoryRecord.FileField}");

        // Configure indexing policy
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = $"/{AzureCosmosDbMemoryRecord.VectorField}/*" }); // Keep vector field excluded here, will handle in CreateIndexAsync

        return properties;
    }

    static AzureCosmosDbConfig()
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
