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
        string vectorFieldPath = $"/{AzureCosmosDbTabularMemoryRecord.VectorField}"; // "/embedding"
        string vectorDataPath = $"{vectorFieldPath}/Data"; // Assumed path: "/embedding/Data"

        // Remove any default exclusion for the vector path or its children
        var exclusionToRemove = containerProperties.IndexingPolicy.ExcludedPaths.FirstOrDefault(p => p.Path == vectorFieldPath + "/*");
        if (exclusionToRemove != null)
        {
            containerProperties.IndexingPolicy.ExcludedPaths.Remove(exclusionToRemove);
        }

        // Ensure the specific vector data path is included if using wildcard includes
        if (!containerProperties.IndexingPolicy.IncludedPaths.Any(p => p.Path == vectorDataPath + "/?"))
        {
            if (!containerProperties.IndexingPolicy.IncludedPaths.Any(p => p.Path == "/*"))
            {
                containerProperties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = vectorDataPath + "/?" });
            }
        }

        // Add the vector index definition using the correct structure and path
        containerProperties.IndexingPolicy.VectorIndexes.Clear(); // Clear potentially incorrect definitions
        containerProperties.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath
        {
            Path = vectorDataPath, // Corrected Path: Targeting nested Data property
            Type = VectorIndexType.Flat // Using Flat index type.
        });

        var containerResponse = await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            containerProperties,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        this._logger.LogInformation("Created/Ensured container {Index} in database {Database} with Vector Index Path {VectorPath}",
            index, this._databaseName, vectorDataPath);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<string>> GetIndexesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<string>();
        try
        {
            using var feedIterator = this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainerQueryIterator<ContainerProperties>("SELECT * FROM c"); // Query properties to check index later if needed

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
            this._logger.LogWarning("Database {Database} not found.", this._databaseName);
            // Database doesn't exist, so no indexes exist. Return empty list.
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task DeleteIndexAsync(string index, CancellationToken cancellationToken = default)
    {
        try
        {
            await this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(index)
                .DeleteContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            this._logger.LogWarning("Index {Index} or Database {Database} not found for deletion.", index, this._databaseName);
            // If it doesn't exist, consider the operation successful.
        }
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

        // Log the structure before upserting for debugging path issues
        // try {
        //     string json = System.Text.Json.JsonSerializer.Serialize(memoryRecord);
        //     this._logger.LogTrace("Upserting document structure: {Json}", json);
        // } catch (Exception e) {
        //     this._logger.LogError(e, "Error serializing record for trace log");
        // }

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

        // Construct the vector search query using the corrected path
        string vectorDataPath = $"c.{AzureCosmosDbTabularMemoryRecord.VectorField}.Data"; // Corrected path c.embedding.Data
        var sql = $"""
                   SELECT TOP @limit
                     {AzureCosmosDbTabularMemoryRecord.Columns("c", withEmbeddings)}, VectorDistance({vectorDataPath}, @queryEmbedding) AS SimilarityScore
                   FROM c
                   {whereCondition}
                   ORDER BY VectorDistance({vectorDataPath}, @queryEmbedding) ASC
                   """; // ASC order because lower distance means higher similarity

        var queryDefinition = new QueryDefinition(sql)
            .WithParameter("@limit", limit)
            .WithParameter("@queryEmbedding", queryEmbedding.Data.ToArray()); // Pass embedding data array as parameter

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
            FeedResponse<TabularMemoryRecordResult> response;
            try
            {
                response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (CosmosException ex) when (ex.Message.Contains("VectorDistance"))
            {
                // Provide a more specific error if VectorDistance fails (e.g., index not configured correctly)
                this._logger.LogError(ex, "Vector search failed. Ensure the vector index path '/{VectorField}/Data' is correctly configured on container '{Index}'.", AzureCosmosDbTabularMemoryRecord.VectorField, index);
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
                   SELECT Top @limit
                     {AzureCosmosDbTabularMemoryRecord.Columns("c", withEmbeddings)}
                   FROM
                     c
                   {whereCondition}
                   """; // Using @limit instead of @topN

        var queryDefinition = new QueryDefinition(sql)
            .WithParameter("@limit", limit);

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
                yield return record.ToMemoryRecord(withEmbeddings); // Pass withEmbeddings
            }
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            // Use the encoded ID for deletion as stored in Cosmos DB
            var encodedId = AzureCosmosDbTabularMemoryRecord.EncodeId(record.Id);

            await this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(index)
                .DeleteItemAsync<AzureCosmosDbTabularMemoryRecord>(
                    encodedId, // Use encoded ID
                    new PartitionKey(record.GetFileId()), // Use original File ID for partition key
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            this._logger.LogDebug("Deleted record {Id} from index {Index}", encodedId, index);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            this._logger.LogTrace("Record {Id} (encoded: {EncodedId}) not found in index {Index}, nothing to delete", record.Id, AzureCosmosDbTabularMemoryRecord.EncodeId(record.Id), index);
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
            return (string.Empty, Array.Empty<Tuple<string, object>>());
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

            // Build conditions for a single filter (AND logic within the filter)
            var singleFilterBuilder = new StringBuilder();
            bool hasTagConditions = false;
            for (var j = 0; j < filter.Pairs.Count(); j++)
            {
                var pair = filter.Pairs.ElementAt(j);
                if (pair.Value is null) { continue; } // Skip null values

                // Check if this is a structured data filter (special tag format)
                if (pair.Key.StartsWith("data.", StringComparison.Ordinal))
                {
                    // This will be handled in the next section
                    continue;
                }

                if (hasTagConditions)
                {
                    singleFilterBuilder.Append(" AND ");
                }

                string paramName = $"@filter_{parameters.Count}";
                singleFilterBuilder.Append($"ARRAY_CONTAINS({alias}.{AzureCosmosDbTabularMemoryRecord.TagsField}.{pair.Key}, {paramName})");
                parameters.Add(Tuple.Create<string, object>(paramName, pair.Value));
                hasTagConditions = true;
            }

            // Append the conditions for this filter if any tags were found
            if (hasTagConditions)
            {
                if (hasConditions) { builder.Append(" OR "); } // OR logic between different filters
                builder.Append('(').Append(singleFilterBuilder).Append(')');
                hasConditions = true;
            }
        }

        // Process structured data filters (from special tags with "data." prefix)
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters.ElementAt(i);
            if (filter.IsEmpty()) { continue; }

            // Build conditions for a single filter (AND logic within the filter)
            var singleDataFilterBuilder = new StringBuilder();
            bool hasDataConditions = false;
            for (var j = 0; j < filter.Pairs.Count(); j++)
            {
                var pair = filter.Pairs.ElementAt(j);
                if (pair.Value is null) { continue; } // Skip null values

                // Only process structured data filters
                if (!pair.Key.StartsWith("data.", StringComparison.Ordinal))
                {
                    continue;
                }

                // Extract the field name (remove "data." prefix)
                string fieldName = pair.Key.Substring(5);
                if (string.IsNullOrEmpty(fieldName)) { continue; } // Skip if key is just "data."

                if (hasDataConditions)
                {
                    singleDataFilterBuilder.Append(" AND ");
                }

                // Add the condition for the data field
                string paramName = $"@data_{parameters.Count}";
                singleDataFilterBuilder.Append($"{alias}.{AzureCosmosDbTabularMemoryRecord.DataField}[\"{fieldName}\"] = {paramName}");
                parameters.Add(Tuple.Create<string, object>(paramName, pair.Value));
                hasDataConditions = true;
            }

            // Append the conditions for this filter if any data fields were found
            if (hasDataConditions)
            {
                if (hasConditions) { builder.Append(" OR "); } // OR logic between different filters
                builder.Append('(').Append(singleDataFilterBuilder).Append(')');
                hasConditions = true;
            }
        }

        // If we didn't add any conditions, return an empty string
        if (!hasConditions)
        {
            return (string.Empty, Array.Empty<Tuple<string, object>>());
        }

        builder.Append(" )");
        return (builder.ToString(), parameters);
    }
}
