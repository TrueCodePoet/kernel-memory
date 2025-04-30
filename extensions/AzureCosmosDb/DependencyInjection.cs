// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDb;

/// <summary>
/// Extension methods for adding Azure Cosmos DB memory connector to Kernel Memory.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Add Azure Cosmos DB memory connector to Kernel Memory.
    /// </summary>
    /// <param name="builder">The Kernel Memory builder.</param>
    /// <param name="endpoint">The Azure Cosmos DB endpoint.</param>
    /// <param name="apiKey">The Azure Cosmos DB API key.</param>
    /// <returns>The Kernel Memory builder.</returns>
    public static IKernelMemoryBuilder WithAzureCosmosDbMemory(
        this IKernelMemoryBuilder builder,
        string endpoint,
        string apiKey)
    {
        var config = new AzureCosmosDbConfig
        {
            Endpoint = endpoint,
            APIKey = apiKey
        };

        return builder.WithAzureCosmosDbMemory(config);
    }

    /// <summary>
    /// Add Azure Cosmos DB memory connector to Kernel Memory.
    /// </summary>
    /// <param name="builder">The Kernel Memory builder.</param>
    /// <param name="config">The Azure Cosmos DB configuration.</param>
    /// <returns>The Kernel Memory builder.</returns>
    public static IKernelMemoryBuilder WithAzureCosmosDbMemory(
        this IKernelMemoryBuilder builder,
        AzureCosmosDbConfig config)
    {
        // Create the Cosmos DB client
        var cosmosClient = new CosmosClient(
            config.Endpoint,
            config.APIKey,
            new CosmosClientOptions
            {
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                }             
            });

        // Register the memory DB implementation
        builder.Services.AddSingleton(cosmosClient);
        builder.Services.AddSingleton<IMemoryDb, AzureCosmosDbMemory>();

        return builder;
    }
}
