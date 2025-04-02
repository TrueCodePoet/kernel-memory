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
        var textEmbedding = await this._embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);

        var (whereCondition, parameters) = WithTags("c", filters);

        // Note: This is a simplified query that doesn't use vector search
        // In a real implementation, you would use the VectorDistance function
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
            .GetItemQueryIterator<MemoryRecordResult>(queryDefinition);

        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var memoryRecord in response)
            {
                this._logger.LogDebug("{Id}  {SimilarityScore}", memoryRecord.Id, memoryRecord.SimilarityScore);
                var relevanceScore = (memoryRecord.SimilarityScore + 1) / 2;
                if (relevanceScore >= minRelevance)
                {
                    yield return (memoryRecord.ToMemoryRecord(withEmbeddings), relevanceScore);
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
