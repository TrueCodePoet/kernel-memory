// Copyright (c) Microsoft. All rights reserved.

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Result class for memory records with similarity score.
/// </summary>
internal sealed class TabularMemoryRecordResult : AzureCosmosDbTabularMemoryRecord
{
    /// <summary>
    /// Gets or sets the similarity score.
    /// </summary>
    public double SimilarityScore { get; init; }
}

/// <summary>
/// Azure Cosmos DB implementation of <see cref="IMemoryDb"/> for tabular data.
/// </summary>
internal sealed class AzureCosmosDbTabularMemory : IMemoryDb
{
    private readonly CosmosClient _cosmosClient;
    private readonly ITextEmbeddingGenerator _embeddingGenerator;
    private readonly ILogger _logger;
    private readonly string _databaseName;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureCosmosDbTabularMemory"/> class.
    /// </summary>
    /// <param name="cosmosClient">The Cosmos DB client.</param>
    /// <param name="embeddingGenerator">The text embedding generator.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">The configuration.</param>
    public AzureCosmosDbTabularMemory(
        CosmosClient cosmosClient,
        ITextEmbeddingGenerator embeddingGenerator,
        ILogger<AzureCosmosDbTabularMemory> logger,
        AzureCosmosDbTabularConfig config)
    {
        _cosmosClient = cosmosClient;
        _embeddingGenerator = embeddingGenerator;
        _logger = logger;
        _databaseName = config.DatabaseName;
    }

    /// <inheritdoc/>
    public async Task CreateIndexAsync(string index, int vectorSize, CancellationToken cancellationToken = default)
    {
        var databaseResponse = await _cosmosClient
            .CreateDatabaseIfNotExistsAsync(_databaseName, cancellationToken: cancellationToken);

        var containerProperties = AzureCosmosDbTabularConfig.GetContainerProperties(index);
        var containerResponse = await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            containerProperties,
            cancellationToken: cancellationToken);

        _logger.LogInformation("Created container {ContainerId} in database {Database}",
            containerResponse.Container.Id, containerResponse.Container.Database);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<string>> GetIndexesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<string>();

        using var feedIterator = _cosmosClient
            .GetDatabase(_databaseName)
            .GetContainerQueryIterator<string>("SELECT VALUE(c.id) FROM c");

        while (feedIterator.HasMoreResults)
        {
            var next = await feedIterator.ReadNextAsync(cancellationToken);
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

    /// <inheritdoc/>
    public async Task DeleteIndexAsync(string index, CancellationToken cancellationToken = default)
    {
        await _cosmosClient
            .GetDatabase(_databaseName)
            .GetContainer(index)
            .DeleteContainerAsync(cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> UpsertAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        // Extract tabular data from the record if available
        Dictionary<string, object>? data = null;
        Dictionary<string, string>? source = null;

        // Check if the record contains tabular data in its payload
        if (record.Payload.TryGetValue("tabular_data", out var tabularData) && tabularData is string tabularDataStr)
        {
            try
            {
                data = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    tabularDataStr,
                    AzureCosmosDbTabularConfig.DefaultJsonSerializerOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Failed to deserialize tabular data: {Message}", ex.Message);
            }
        }

        // Check if the record contains source information in its payload
        if (record.Payload.TryGetValue("source_info", out var sourceInfo) && sourceInfo is string sourceInfoStr)
        {
            try
            {
                source = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    sourceInfoStr,
                    AzureCosmosDbTabularConfig.DefaultJsonSerializerOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Failed to deserialize source information: {Message}", ex.Message);
            }
        }

        var memoryRecord = AzureCosmosDbTabularMemoryRecord.FromMemoryRecord(record, data, source);

        var result = await _cosmosClient
            .GetDatabase(_databaseName)
            .GetContainer(index)
            .UpsertItemAsync(
                memoryRecord,
                memoryRecord.GetPartitionKey(),
                cancellationToken: cancellationToken);

        return result.Resource.Id;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<(MemoryRecord, double)> GetSimilarListAsync(
        string index,
        string text,
        ICollection<MemoryFilter>? filters = null,
        double minRelevance = 0,
        int limit = 1,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var textEmbedding = await _embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken);

        // Process filters to extract both standard tag filters and structured data filters
        var (whereCondition, parameters) = ProcessFilters("c", filters);

        var sql =
            $"""
             SELECT Top @topN
                 {AzureCosmosDbTabularMemoryRecord.Columns("x", withEmbeddings)},
                 x.similarityScore
             FROM (
                 SELECT
                     {AzureCosmosDbTabularMemoryRecord.Columns("c", withEmbeddings)},
                     VectorDistance(c.embedding, @embedding) AS similarityScore 
                 FROM
                     c
                 {whereCondition}
             ) AS x
             WHERE x.similarityScore > @similarityScore
             ORDER BY x.similarityScore desc
             """;

        var queryDefinition = new QueryDefinition(sql)
            .WithParameter("@topN", limit)
            .WithParameter("@embedding", textEmbedding.Data)
            .WithParameter("@similarityScore", minRelevance);

        // Add all parameters from the filters
        foreach (var (name, value) in parameters)
        {
            queryDefinition.WithParameter(name, value);
        }

        using var feedIterator = _cosmosClient
            .GetDatabase(_databaseName)
            .GetContainer(index)
            .GetItemQueryIterator<TabularMemoryRecordResult>(queryDefinition);

        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync(cancellationToken);
            foreach (var memoryRecord in response)
            {
                _logger.LogDebug("Retrieved record {Id} with similarity score {SimilarityScore}",
                    memoryRecord.Id, memoryRecord.SimilarityScore);

                var relevanceScore = (memoryRecord.SimilarityScore + 1) / 2;
                if (relevanceScore >= minRelevance)
                {
                    yield return (memoryRecord.ToMemoryRecord(withEmbeddings), relevanceScore);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<MemoryRecord> GetListAsync(
        string index,
        ICollection<MemoryFilter>? filters = null,
        int limit = 1,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Process filters to extract both standard tag filters and structured data filters
        var (whereCondition, parameters) = ProcessFilters("c", filters);

        var sql = $"""
                   SELECT Top @topN
                     {AzureCosmosDbTabularMemoryRecord.Columns("c", withEmbeddings)}
                   FROM 
                     c
                   {whereCondition}
                   """;

        var queryDefinition = new QueryDefinition(sql)
            .WithParameter("@topN", limit);

        // Add all parameters from the filters
        foreach (var (name, value) in parameters)
        {
            queryDefinition.WithParameter(name, value);
        }

        using var feedIterator = _cosmosClient
            .GetDatabase(_databaseName)
            .GetContainer(index)
            .GetItemQueryIterator<AzureCosmosDbTabularMemoryRecord>(queryDefinition);

        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync(cancellationToken);
            foreach (var record in response)
            {
                yield return record.ToMemoryRecord(withEmbeddings);
            }
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            var id = AzureCosmosDbTabularMemoryRecord.FromMemoryRecord(record).Id;

            await _cosmosClient
                .GetDatabase(_databaseName)
                .GetContainer(index)
                .DeleteItemAsync<AzureCosmosDbTabularMemoryRecord>(
                    id,
                    new PartitionKey(record.GetFileId()),
                    cancellationToken: cancellationToken);

            _logger.LogDebug("Deleted record {Id} from index {Index}", id, index);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogTrace("Record {Id} not found in index {Index}, nothing to delete", record.Id, index);
        }
    }

    /// <summary>
    /// Processes memory filters to extract both standard tag filters and structured data filters.
    /// </summary>
    /// <param name="alias">The alias for the SQL query.</param>
    /// <param name="filters">The filters to process.</param>
    /// <returns>A tuple containing the WHERE clause and the parameters.</returns>
    private (string, IReadOnlyCollection<Tuple<string, object>>) ProcessFilters(string alias, ICollection<MemoryFilter>? filters = null)
    {
        if (filters is null || filters.Count == 0)
        {
            return (string.Empty, []);
        }

        var parameters = new List<Tuple<string, object>>();
        var builder = new StringBuilder();
        builder.Append("WHERE ( ");

        // Track if we've added any conditions yet
        bool hasConditions = false;

        // Process standard tag filters
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters.ElementAt(i);

            // Skip empty filters
            if (filter.IsEmpty())
            {
                continue;
            }

            if (hasConditions)
            {
                builder.Append(" OR ");
            }

            builder.Append("(");
            bool hasTagConditions = false;

            // Process tag conditions
            for (var j = 0; j < filter.Pairs.Count(); j++)
            {
                var pair = filter.Pairs.ElementAt(j);

                // Skip null values
                if (pair.Value is null)
                {
                    continue;
                }

                // Check if this is a structured data filter (special tag format)
                if (pair.Key.StartsWith("data."))
                {
                    // This will be handled in the next section
                    continue;
                }

                if (hasTagConditions)
                {
                    builder.Append(" AND ");
                }

                builder.Append(
                    $"ARRAY_CONTAINS({alias}.{AzureCosmosDbTabularMemoryRecord.TagsField}.{pair.Key}, @filter_{i}_{j}_value)");
                parameters.Add(new Tuple<string, object>($"@filter_{i}_{j}_value", pair.Value));

                hasTagConditions = true;
            }

            // If we didn't add any tag conditions, remove the opening parenthesis
            if (!hasTagConditions)
            {
                builder.Length -= 1; // Remove the "("
            }
            else
            {
                builder.Append(")");
                hasConditions = true;
            }
        }

        // Process structured data filters (from special tags with "data." prefix)
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters.ElementAt(i);

            // Skip empty filters
            if (filter.IsEmpty())
            {
                continue;
            }

            bool hasDataConditions = false;
            StringBuilder dataBuilder = new StringBuilder();
            dataBuilder.Append("(");

            // Process data conditions
            for (var j = 0; j < filter.Pairs.Count(); j++)
            {
                var pair = filter.Pairs.ElementAt(j);

                // Skip null values
                if (pair.Value is null)
                {
                    continue;
                }

                // Only process structured data filters (special tag format)
                if (!pair.Key.StartsWith("data."))
                {
                    continue;
                }

                // Extract the field name (remove "data." prefix)
                string fieldName = pair.Key.Substring(5);

                if (hasDataConditions)
                {
                    dataBuilder.Append(" AND ");
                }

                // Add the condition for the data field
                dataBuilder.Append(
                    $"{alias}.{AzureCosmosDbTabularMemoryRecord.DataField}[\"{fieldName}\"] = @data_{i}_{j}_value");
                parameters.Add(new Tuple<string, object>($"@data_{i}_{j}_value", pair.Value));

                hasDataConditions = true;
            }

            // If we added any data conditions, add them to the main builder
            if (hasDataConditions)
            {
                dataBuilder.Append(")");

                if (hasConditions)
                {
                    builder.Append(" OR ");
                }

                builder.Append(dataBuilder);
                hasConditions = true;
            }
        }

        // If we didn't add any conditions, return an empty string
        if (!hasConditions)
        {
            return (string.Empty, []);
        }

        builder.Append(" )");
        return (builder.ToString(), parameters);
    }
}
