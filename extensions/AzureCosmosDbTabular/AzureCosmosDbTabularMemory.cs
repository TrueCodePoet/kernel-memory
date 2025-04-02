// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI; // Added back necessary using directive
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
        this._cosmosClient = cosmosClient;
        this._embeddingGenerator = embeddingGenerator;
        this._logger = logger;
        this._databaseName = config.DatabaseName;
    }

    /// <inheritdoc/>
    public async Task CreateIndexAsync(string index, int vectorSize, CancellationToken cancellationToken = default)
    {
        var databaseResponse = await this._cosmosClient
            .CreateDatabaseIfNotExistsAsync(this._databaseName, cancellationToken: cancellationToken).ConfigureAwait(false);

        var containerProperties = AzureCosmosDbTabularConfig.GetContainerProperties(index);

        // Define and add the vector index policy dynamically
        containerProperties.IndexingPolicy.ExcludedPaths.Remove(new ExcludedPath { Path = $"/{AzureCosmosDbTabularMemoryRecord.VectorField}/*" }); // Remove exclusion if it exists

        // Add the vector index definition using the correct structure
        containerProperties.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath
        {
            Path = $"/{AzureCosmosDbTabularMemoryRecord.VectorField}", // Use the constant from the Tabular record class
            Type = VectorIndexType.Flat // Using Flat index type.
        });

        var containerResponse = await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            containerProperties,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        this._logger.LogInformation("Created container {ContainerId} in database {Database}",
            containerResponse.Container.Id, containerResponse.Container.Database);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<string>> GetIndexesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<string>();

        using var feedIterator = this._cosmosClient
            .GetDatabase(this._databaseName)
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

    /// <inheritdoc/>
    public async Task DeleteIndexAsync(string index, CancellationToken cancellationToken = default)
    {
        await this._cosmosClient
            .GetDatabase(this._databaseName)
            .GetContainer(index)
            .DeleteContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
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
                this._logger.LogWarning("Failed to deserialize tabular data: {Message}", ex.Message);
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
                this._logger.LogWarning("Failed to deserialize source information: {Message}", ex.Message);
            }
        }

        var memoryRecord = AzureCosmosDbTabularMemoryRecord.FromMemoryRecord(record, data, source);

        var result = await this._cosmosClient
            .GetDatabase(this._databaseName)
            .GetContainer(index)
            .UpsertItemAsync(
                memoryRecord,
                memoryRecord.GetPartitionKey(),
                cancellationToken: cancellationToken).ConfigureAwait(false);

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
        // Generate the embedding for the query text
        var queryEmbedding = await this._embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);

        // Process filters to extract both standard tag filters and structured data filters
        var (whereCondition, filterParameters) = this.ProcessFilters("c", filters);

        // Construct the vector search query
        // Select the document fields and the calculated vector distance as SimilarityScore
        var sql = $"""
                   SELECT TOP @limit
                     {AzureCosmosDbTabularMemoryRecord.Columns("c", withEmbeddings)}, VectorDistance(c.{AzureCosmosDbTabularMemoryRecord.VectorField}, @queryEmbedding) AS SimilarityScore
                   FROM c
                   {whereCondition}
                   ORDER BY VectorDistance(c.{AzureCosmosDbTabularMemoryRecord.VectorField}, @queryEmbedding) ASC
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
            .GetDatabase(this._databaseName)
            .GetContainer(index)
            .GetItemQueryIterator<TabularMemoryRecordResult>(queryDefinition);

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

    /// <inheritdoc/>
    public async IAsyncEnumerable<MemoryRecord> GetListAsync(
        string index,
        ICollection<MemoryFilter>? filters = null,
        int limit = 1,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Process filters to extract both standard tag filters and structured data filters
        var (whereCondition, parameters) = this.ProcessFilters("c", filters);

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

        using var feedIterator = this._cosmosClient
            .GetDatabase(this._databaseName)
            .GetContainer(index)
            .GetItemQueryIterator<AzureCosmosDbTabularMemoryRecord>(queryDefinition);

        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
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

            await this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(index)
                .DeleteItemAsync<AzureCosmosDbTabularMemoryRecord>(
                    id,
                    new PartitionKey(record.GetFileId()),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            this._logger.LogDebug("Deleted record {Id} from index {Index}", id, index);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            this._logger.LogTrace("Record {Id} not found in index {Index}, nothing to delete", record.Id, index);
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
                builder.Append(" OR "); // CA1834: Use Append(char) for single char
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
                if (pair.Key.StartsWith("data.", StringComparison.Ordinal)) // CA1310: Specify StringComparison
                {
                    // This will be handled in the next section
                    continue;
                }

                if (hasTagConditions)
                {
                    builder.Append(" AND ");
                }

                builder.Append(System.Globalization.CultureInfo.InvariantCulture, // CA1305: Specify CultureInfo
                    $"ARRAY_CONTAINS({alias}.{AzureCosmosDbTabularMemoryRecord.TagsField}.{pair.Key}, @filter_{i}_{j}_value)");
                parameters.Add(new Tuple<string, object>($"@filter_{i}_{j}_value", pair.Value)); // IDE0090: Simplify new

                hasTagConditions = true;
            }

            // If we didn't add any tag conditions, remove the opening parenthesis
            if (!hasTagConditions)
            {
                builder.Length -= 1; // Remove the '('
            }
            else
            {
                builder.Append(')'); // CA1834: Use Append(char) for single char
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
            StringBuilder dataBuilder = new(); // IDE0090: Simplify new
            dataBuilder.Append('('); // CA1834: Use Append(char) for single char

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
                if (!pair.Key.StartsWith("data.", StringComparison.Ordinal)) // CA1310: Specify StringComparison
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
                dataBuilder.Append(System.Globalization.CultureInfo.InvariantCulture, // CA1305: Specify CultureInfo
                    $"{alias}.{AzureCosmosDbTabularMemoryRecord.DataField}[\"{fieldName}\"] = @data_{i}_{j}_value");
                parameters.Add(new Tuple<string, object>($"@data_{i}_{j}_value", pair.Value)); // IDE0090: Simplify new

                hasDataConditions = true;
            }

            // If we added any data conditions, add them to the main builder
            if (hasDataConditions)
            {
                dataBuilder.Append(')'); // CA1834: Use Append(char) for single char

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
