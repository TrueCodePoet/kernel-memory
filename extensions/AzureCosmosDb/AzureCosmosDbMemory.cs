// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel; // Added for Collection<Embedding>
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDb;

// Internal class to handle the query result including the similarity score
internal sealed class MemoryRecordResult : AzureCosmosDbMemoryRecord
{
    // SimilarityScore is populated by the VectorDistance function in the query
    public double SimilarityScore { get; init; }
}

internal sealed class AzureCosmosDbMemory : IMemoryDb
{
    private const string DatabaseName = "memory"; // Consider making this configurable

    private readonly CosmosClient _cosmosClient;
    private readonly ITextEmbeddingGenerator _embeddingGenerator;
    private readonly ILogger _logger;

    public AzureCosmosDbMemory(
        CosmosClient cosmosClient,
        ITextEmbeddingGenerator embeddingGenerator,
        ILogger<AzureCosmosDbMemory> logger)
    {
        this._cosmosClient = cosmosClient;
        this._embeddingGenerator = embeddingGenerator;
        this._logger = logger;
    }

    public async Task CreateIndexAsync(string index, int vectorSize, CancellationToken cancellationToken = default)
    {
        var databaseResponse = await this._cosmosClient
             .CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cancellationToken).ConfigureAwait(false);

        var containerProperties = AzureCosmosDbConfig.GetContainerProperties(index);

        // Define the vector field path
        // Note: Even though constant is VectorField = "embedding", the actual property is Vector which serializes to "vector"
        string vectorFieldPath = "/vector"; // Use the actual serialized field name

        // Define the vector embedding policy for the container
        var embeddings = new List<Microsoft.Azure.Cosmos.Embedding> // Specify the correct namespace
        {
            new()
            {
                Path = vectorFieldPath,
                DataType = VectorDataType.Float32,
                DistanceFunction = DistanceFunction.Cosine,
                Dimensions = vectorSize,
            }
        };
        containerProperties.VectorEmbeddingPolicy = new VectorEmbeddingPolicy(new Collection<Microsoft.Azure.Cosmos.Embedding>(embeddings)); // Specify the correct namespace

        // Remove any default exclusion for the vector path or its children
        // Iterate and remove the specific exclusion path if it exists
        var exclusionToRemove = containerProperties.IndexingPolicy.ExcludedPaths.FirstOrDefault(p => p.Path == vectorFieldPath + "/*");
        if (exclusionToRemove != null)
        {
            containerProperties.IndexingPolicy.ExcludedPaths.Remove(exclusionToRemove);
        }

        // Ensure the specific vector path is included if using wildcard includes
        // If "/*" is included, this might not be strictly necessary, but explicit is safer.
        if (!containerProperties.IndexingPolicy.IncludedPaths.Any(p => p.Path == vectorFieldPath + "/?"))
        {
            // Check if root wildcard exists, if not, add specific path
            if (!containerProperties.IndexingPolicy.IncludedPaths.Any(p => p.Path == "/*"))
            {
                containerProperties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = vectorFieldPath + "/?" });
            }
        }

        // Add the vector index definition using the correct structure and path
        containerProperties.IndexingPolicy.VectorIndexes.Clear(); // Clear any potentially incorrect definitions first
        containerProperties.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath
        {
            Path = vectorFieldPath, // Reverted Path: Targeting root embedding property
            Type = VectorIndexType.QuantizedFlat // Switched to QuantizedFlat to support higher dimensions (e.g., 1536)
        });

        var containerResponse = await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            containerProperties,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        this._logger.LogInformation("Created/Ensured container {Index} in database {Database} with Vector Index Path {VectorPath}",
            index, DatabaseName, vectorFieldPath); // Log the correct path used
    }

    public async Task<IEnumerable<string>> GetIndexesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<string>();
        try
        {
            using var feedIterator = this._cosmosClient
                .GetDatabase(DatabaseName)
                .GetContainerQueryIterator<ContainerProperties>("SELECT * FROM c");

            while (feedIterator.HasMoreResults)
            {
                var next = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                foreach (var containerProperties in next.Resource)
                {
                    if (!string.IsNullOrEmpty(containerProperties?.Id))
                    {
                        result.Add(containerProperties.Id);
                    }
                }
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            this._logger.LogWarning("Database {Database} not found.", DatabaseName);
            // Database doesn't exist, so no indexes exist. Return empty list.
        }

        return result;
    }

    public async Task DeleteIndexAsync(string index, CancellationToken cancellationToken = default)
    {
        try
        {
            await this._cosmosClient
                .GetDatabase(DatabaseName)
                .GetContainer(index)
                .DeleteContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            this._logger.LogWarning("Index {Index} or Database {Database} not found for deletion.", index, DatabaseName);
            // If it doesn't exist, consider the operation successful.
        }
    }

    public async Task<string> UpsertAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        var memoryRecord = AzureCosmosDbMemoryRecord.FromMemoryRecord(record);

        // Log the structure before upserting for debugging path issues
        // try {
        //     string json = System.Text.Json.JsonSerializer.Serialize(memoryRecord);
        //     this._logger.LogTrace("Upserting document structure: {Json}", json);
        // } catch (Exception e) {
        //     this._logger.LogError(e, "Error serializing record for trace log");
        // }

        var result = await this._cosmosClient
            .GetDatabase(DatabaseName)
            .GetContainer(index)
            .UpsertItemAsync(
                memoryRecord,
                memoryRecord.GetPartitionKey(),
                cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Resource.Id;
    }

    public async IAsyncEnumerable<(MemoryRecord, double)> GetSimilarListAsync(
        string index,
        string text,
        ICollection<MemoryFilter>? filters = null,
        double minRelevance = 0,
        int limit = 5, // Changed default to 5 to match tabular implementation
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Generate the embedding for the query text
        var queryEmbedding = await this._embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);

        // Prepare tag filters
        var (whereCondition, filterParameters) = WithTags("c", filters);

        // Construct the vector search query using the correct vector field name
        string vectorFieldQueryPath = $"c.vector"; // Use c.vector instead of c.embedding
        
        // Determine if we should use a limit
        string topClause = limit > 0 ? $"TOP @limit" : "";
        
        var sql = $"""
                   SELECT {topClause}
                     {AzureCosmosDbMemoryRecord.Columns("c", withEmbeddings)}, VectorDistance({vectorFieldQueryPath}, @queryEmbedding) AS SimilarityScore
                   FROM c
                   {whereCondition}
                   ORDER BY VectorDistance({vectorFieldQueryPath}, @queryEmbedding)
                   """; // No ASC/DESC needed - VectorDistance automatically sorts from most similar to least similar

        var queryDefinition = new QueryDefinition(sql);
        
        // Only add the limit parameter if we're using a limit
        if (limit > 0)
        {
            queryDefinition = queryDefinition.WithParameter("@limit", limit);
        }
        
        queryDefinition = queryDefinition.WithParameter("@queryEmbedding", queryEmbedding.Data.ToArray()); // Pass embedding data array as parameter

        // Add filter parameters
        foreach (var (name, value) in filterParameters)
        {
            queryDefinition = queryDefinition.WithParameter(name, value);
        }

        this._logger.LogTrace("Executing vector search query: {Query}", queryDefinition.QueryText);

        // Execute the query
        using var feedIterator = this._cosmosClient
            .GetDatabase(DatabaseName)
            .GetContainer(index)
            .GetItemQueryIterator<MemoryRecordResult>(queryDefinition);

        while (feedIterator.HasMoreResults)
        {
            FeedResponse<MemoryRecordResult> response;
            try
            {
                response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.Message.Contains("VectorDistance"))
            {
                // Provide detailed error information for easier troubleshooting
                this._logger.LogError(ex, 
                    "Vector search failed on container '{Index}'. The vector index path '/vector' may not be properly configured. " +
                    "Error details: {ErrorMessage}. " +
                    "Make sure you have created a vector index on the 'vector' field in your Cosmos DB container.",
                    index, ex.Message);
                
                Console.WriteLine("--- COSMOS DB VECTOR SEARCH ERROR ---");
                Console.WriteLine($"Error Message: {ex.Message}");
                Console.WriteLine($"Status Code: {ex.StatusCode}");
                Console.WriteLine($"Activity ID: {ex.ActivityId}");
                Console.WriteLine($"Request Charge: {ex.RequestCharge} RUs");
                Console.WriteLine("Possible solutions:");
                Console.WriteLine("1. Verify your Cosmos DB container has a vector index on the 'vector' field");
                Console.WriteLine("2. Check that vectorization is enabled for your Cosmos DB account");
                Console.WriteLine("3. Ensure vector dimensions match between stored data and queries");
                Console.WriteLine("4. Confirm your Azure region supports vector search capabilities");
                Console.WriteLine("--- END ERROR DETAILS ---");
                // Re-throw or handle as appropriate, here we break the loop
                yield break;
            }

            foreach (var memoryRecord in response)
            {
                // Convert Cosine distance (0 to 2) to relevance score (1 to 0)
                // Cosine Similarity = (2 - Cosine Distance) / 2
                double cosineDistance = memoryRecord.SimilarityScore;
                double relevanceScore = (2.0 - cosineDistance) / 2.0;

                this._logger.LogDebug("ID: {Id}, Distance: {Distance}, Relevance: {Relevance}", memoryRecord.Id, cosineDistance, relevanceScore);

                if (relevanceScore >= minRelevance)
                {
                    yield return (memoryRecord.ToMemoryRecord(withEmbeddings), relevanceScore);
                }
                else
                {
                    this._logger.LogDebug("ID: {Id} filtered out by minRelevance ({Relevance} < {MinRelevance})", memoryRecord.Id, relevanceScore, minRelevance);
                }
            }
        }
    }

    public async IAsyncEnumerable<MemoryRecord> GetListAsync(
        string index,
        ICollection<MemoryFilter>? filters = null,
        int limit = 5, // Changed default to 5 to match other methods
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (whereCondition, parameters) = WithTags("c", filters);

        // Determine if we should use a limit
        string topClause = limit > 0 ? $"TOP @limit" : "";
        
        var sql = $"""
                   SELECT {topClause}
                     {AzureCosmosDbMemoryRecord.Columns("c", withEmbeddings)}
                   FROM
                     c
                   {whereCondition}
                   """;

        var queryDefinition = new QueryDefinition(sql);
        
        // Only add the limit parameter if we're using a limit
        if (limit > 0)
        {
            queryDefinition = queryDefinition.WithParameter("@limit", limit);
        }
        foreach (var (name, value) in parameters)
        {
            queryDefinition = queryDefinition.WithParameter(name, value);
        }

        using var feedIterator = this._cosmosClient
            .GetDatabase(DatabaseName)
            .GetContainer(index)
            .GetItemQueryIterator<AzureCosmosDbMemoryRecord>(queryDefinition);
        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var record in response)
            {
                yield return record.ToMemoryRecord(withEmbeddings); // Pass withEmbeddings
            }
        }
    }

    // Helper method to build the WHERE clause for tag filtering
    private static (string, IReadOnlyCollection<Tuple<string, object>>) WithTags(string alias, ICollection<MemoryFilter>? filters = null)
    {
        if (filters is null || filters.Count == 0)
        {
            return (string.Empty, Array.Empty<Tuple<string, object>>());
        }

        var parameters = new List<Tuple<string, object>>();
        var queryBuilder = new StringBuilder();

        bool firstFilter = true;
        foreach (var filter in filters)
        {
            if (filter.IsEmpty()) { continue; }

            if (!firstFilter)
            {
                queryBuilder.Append(" OR ");
            }

            queryBuilder.Append('(');
            bool firstPair = true;
            foreach (var value in filter)
            {
                if (value.Value is null) { continue; } // Skip null values

                if (!firstPair)
                {
                    queryBuilder.Append(" AND ");
                }

                string paramName = $"@filter_{parameters.Count}";
                queryBuilder.Append($"ARRAY_CONTAINS({alias}.{AzureCosmosDbMemoryRecord.TagsField}.{value.Key}, {paramName})");
                parameters.Add(Tuple.Create<string, object>(paramName, value.Value));
                firstPair = false;
            }
            queryBuilder.Append(')');
            firstFilter = false;
        }

        if (parameters.Count == 0)
        {
            return (string.Empty, Array.Empty<Tuple<string, object>>());
        }

        return ($"WHERE ({queryBuilder})", parameters);
    }


    public async Task DeleteAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            // Use the encoded ID for deletion as stored in Cosmos DB
            var encodedId = AzureCosmosDbMemoryRecord.EncodeId(record.Id);
            await this._cosmosClient
                .GetDatabase(DatabaseName)
                .GetContainer(index)
                .DeleteItemAsync<AzureCosmosDbMemoryRecord>(encodedId, // Use encoded ID
                    new PartitionKey(record.GetFileId()), // Use original File ID for partition key
                    cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            this._logger.LogTrace("Index {Index} record {Id} (encoded: {EncodedId}) not found, nothing to delete", index, record.Id, AzureCosmosDbMemoryRecord.EncodeId(record.Id));
        }
    }
}
