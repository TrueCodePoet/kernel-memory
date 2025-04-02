// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI; // Added back necessary using directive
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDb;

internal sealed class MemoryRecordResult : AzureCosmosDbMemoryRecord
{
    public double SimilarityScore { get; init; }
}

internal sealed class AzureCosmosDbMemory : IMemoryDb
{
    private const string DatabaseName = "memory";

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

        // Define and add the vector index policy dynamically
        containerProperties.IndexingPolicy.ExcludedPaths.Remove(new ExcludedPath { Path = $"/{AzureCosmosDbMemoryRecord.VectorField}/*" }); // Remove exclusion if it exists

        // Add the vector index definition using the correct structure
        containerProperties.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath
        {
            Path = $"/{AzureCosmosDbMemoryRecord.VectorField}",
            Type = VectorIndexType.Flat // Using Flat index type. Consider HNSW for larger datasets if needed.
            // Note: Dimensions and DistanceFunction are often part of the query, not the index definition itself in newer SDKs/APIs,
            // but let's stick to the example structure for now. If DistanceFunction is needed here, it would be on VectorIndexSpec if that class were used.
            // The VectorDistance function in the query implicitly uses the distance metric (Cosine by default or specified).
        });

        // Ensure the VectorEmbeddingPolicy is set (as per the example, though its exact role might differ slightly across minor versions)
        // We might not need a separate EmbeddingPath/VectorIndexSpec if VectorIndexPath is sufficient.
        // Let's try setting the policy directly on ContainerProperties if available, or skip if VectorIndexes is the primary mechanism.
        // Based on the example, it seems VectorEmbeddingPolicy might be set directly on ContainerProperties, not IndexingPolicy.
        // Let's adjust based on the example structure:
        // containerProperties.VectorEmbeddingPolicy = new VectorEmbeddingPolicy(new Collection<Embedding>(...)); // This seems complex and might not be needed just for index definition.

        // Let's stick to modifying the IndexingPolicy as it's the most common place.
        // If VectorIndexes.Add is the correct way, the previous VectorEmbeddingPolicies lines were incorrect.

        var containerResponse = await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            containerProperties,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        this._logger.LogInformation("{Database} {ContainerId}", containerResponse.Container.Database, containerResponse.Container.Id);
    }

    public async Task<IEnumerable<string>> GetIndexesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<string>();

        using var feedIterator = this._cosmosClient
            .GetDatabase(DatabaseName)
            .GetContainerQueryIterator<string>("SELECT VALUE(c.id) FROM c");

        while (feedIterator.HasMoreResults)
        {
            var next = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var containerName in next.Resource)
            {
                if (!string.IsNullOrEmpty(containerName))
                {
                    result.Add(containerName);
                }
            }
        }

        return result;
    }

    public async Task DeleteIndexAsync(string index, CancellationToken cancellationToken = default)
    {
        await this._cosmosClient
            .GetDatabase(DatabaseName)
            .GetContainer(index)
            .DeleteContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> UpsertAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        var memoryRecord = AzureCosmosDbMemoryRecord.FromMemoryRecord(record);

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
        int limit = 1,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Generate the embedding for the query text
        var queryEmbedding = await this._embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);

        // Prepare tag filters
        var (whereCondition, filterParameters) = WithTags("c", filters);

        // Construct the vector search query
        // Select the document fields and the calculated vector distance as SimilarityScore
        var sql = $"""
                   SELECT TOP @limit
                     {AzureCosmosDbMemoryRecord.Columns("c", withEmbeddings)}, VectorDistance(c.{AzureCosmosDbMemoryRecord.VectorField}, @queryEmbedding) AS SimilarityScore
                   FROM c
                   {whereCondition}
                   ORDER BY VectorDistance(c.{AzureCosmosDbMemoryRecord.VectorField}, @queryEmbedding) ASC
                   """; // ASC order because lower distance means higher similarity

        var queryDefinition = new QueryDefinition(sql)
            .WithParameter("@limit", limit)
            .WithParameter("@queryEmbedding", queryEmbedding.Data.ToArray()); // Pass embedding as parameter

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
            var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var memoryRecord in response)
            {
                // Convert Cosine distance (0 to 2) to relevance score (1 to 0)
                // Cosine Similarity = (2 - Cosine Distance) / 2
                // We use the distance directly for filtering, then convert for the final result.
                // Note: SimilarityScore here is actually the Cosine Distance from the query.
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
        int limit = 1,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (whereCondition, parameters) = WithTags("c", filters);

        var sql = $"""
                   SELECT Top @topN
                     {AzureCosmosDbMemoryRecord.Columns("c", withEmbeddings)}
                   FROM 
                     c
                   {whereCondition}
                   """;

        var queryDefinition = new QueryDefinition(sql)
            .WithParameter("@topN", limit);
        foreach (var (name, value) in parameters)
        {
            queryDefinition.WithParameter(name, value);
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
                yield return record.ToMemoryRecord();
            }
        }
    }

    private static (string, IReadOnlyCollection<Tuple<string, object>>) WithTags(string alias, ICollection<MemoryFilter>? filters = null)
    {
        if (filters is null || filters.Count == 0)
        {
            return (string.Empty, []);
        }

        var parameters = new List<Tuple<string, object>>();
        var builder = new StringBuilder();
        builder.Append("WHERE ( ");

        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters.ElementAt(i);

            if (i > 0)
            {
                builder.Append(" OR ");
            }

            for (var j = 0; j < filter.Pairs.Count(); j++)
            {
                var value = filter.Pairs.ElementAt(j);
                if (value.Value is null)
                {
                    continue;
                }

                if (j > 0)
                {
                    builder.Append(" AND ");
                }

                builder.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"ARRAY_CONTAINS({alias}.{AzureCosmosDbMemoryRecord.TagsField}.{value.Key}, @filter_{i}_{j}_value)");
                parameters.Add(new Tuple<string, object>($"@filter_{i}_{j}_value", value.Value));
            }
        }

        builder.Append(" )");
        return (builder.ToString(), parameters);
    }

    public async Task DeleteAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            await this._cosmosClient
                .GetDatabase(DatabaseName)
                .GetContainer(index)
                .DeleteItemAsync<MemoryRecord>(record.Id,
                    new PartitionKey(record.Id),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            this._logger.LogTrace("Index {0} record {1} not found, nothing to delete", index, record.Id);
        }
    }
}
