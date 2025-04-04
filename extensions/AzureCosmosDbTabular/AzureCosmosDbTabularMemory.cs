// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel; // Added for Collection<Embedding>
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

        // Define the vector field path
        string vectorFieldPath = $"/{AzureCosmosDbTabularMemoryRecord.VectorField}"; // "/embedding"

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
        var exclusionToRemove = containerProperties.IndexingPolicy.ExcludedPaths.FirstOrDefault(p => p.Path == vectorFieldPath + "/*");
        if (exclusionToRemove != null)
        {
            containerProperties.IndexingPolicy.ExcludedPaths.Remove(exclusionToRemove);
        }

        // Ensure the specific vector path is included if using wildcard includes
        if (!containerProperties.IndexingPolicy.IncludedPaths.Any(p => p.Path == vectorFieldPath + "/?"))
        {
            if (!containerProperties.IndexingPolicy.IncludedPaths.Any(p => p.Path == "/*"))
            {
                containerProperties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = vectorFieldPath + "/?" });
            }
        }

        // Add the vector index definition using the correct structure and path
        containerProperties.IndexingPolicy.VectorIndexes.Clear(); // Clear potentially incorrect definitions
        containerProperties.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath
        {
            Path = vectorFieldPath, // Reverted Path: Targeting root embedding property
            Type = VectorIndexType.QuantizedFlat // Switched to QuantizedFlat to support higher dimensions (e.g., 1536)
        });

        var containerResponse = await databaseResponse.Database.CreateContainerIfNotExistsAsync(
            containerProperties,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        this._logger.LogInformation("Created/Ensured container {Index} in database {Database} with Vector Index Path {VectorPath}",
            index, this._databaseName, vectorFieldPath); // Log the correct path used
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
        // Create the Cosmos DB record directly from the memory record
        // The FromMemoryRecord method now handles extracting tabular_data and source_info from the payload
        var memoryRecord = AzureCosmosDbTabularMemoryRecord.FromMemoryRecord(record);

        // Optional debugging: Log the structure before upserting
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

        // Construct the vector search query using the root path
        string vectorFieldQueryPath = $"c.{AzureCosmosDbTabularMemoryRecord.VectorField}"; // Reverted path c.embedding
        var sql = $"""
                   SELECT TOP @limit
                     {AzureCosmosDbTabularMemoryRecord.Columns("c", withEmbeddings)}, VectorDistance({vectorFieldQueryPath}, @queryEmbedding) AS SimilarityScore
                   FROM c
                   {whereCondition}
                   ORDER BY VectorDistance({vectorFieldQueryPath}, @queryEmbedding) ASC
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
                this._logger.LogError(ex, "Vector search failed. Ensure the vector index path '/{VectorField}' is correctly configured on container '{Index}'.", AzureCosmosDbTabularMemoryRecord.VectorField, index); // Updated log message path
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
        if (filters is null || !filters.Any(f => !f.IsEmpty()))
        {
            return (string.Empty, Array.Empty<Tuple<string, object>>());
        }

        var parameters = new List<Tuple<string, object>>();
        var outerBuilder = new StringBuilder(); // For OR between filters
        bool firstOuterFilter = true;

        foreach (var filter in filters)
        {
            if (filter.IsEmpty()) { continue; }

            var innerBuilder = new StringBuilder(); // For AND within a filter
            bool firstInnerCondition = true;

            foreach (var pair in filter)
            {
                if (pair.Value is null) { continue; } // Skip null values

                if (!firstInnerCondition)
                {
                    innerBuilder.Append(" AND ");
                }

                // Use a consistent parameter naming scheme
                string paramName = $"@p_{parameters.Count}";
                parameters.Add(Tuple.Create<string, object>(paramName, pair.Value));

                if (pair.Key.StartsWith("data.", StringComparison.Ordinal))
                {
                    // Normalize the field name (convert camelCase to snake_case)
                    string normalizedKey = NormalizeFieldName(pair.Key);

                    // Handle structured data filter
                    string fieldName = normalizedKey.Substring(5);
                    if (!string.IsNullOrEmpty(fieldName))
                    {
                        // Use bracket notation for field names that might contain special characters
                        innerBuilder.Append($"{alias}.{AzureCosmosDbTabularMemoryRecord.DataField}[\"{fieldName}\"] = {paramName}");
                        firstInnerCondition = false;

                        // Log the normalization if it happened
                        if (normalizedKey != pair.Key)
                        {
                            this._logger.LogDebug("Normalized field name: {OriginalKey} -> {NormalizedKey}", pair.Key, normalizedKey);
                        }
                    }
                    else
                    {
                        this._logger.LogWarning("Invalid structured data filter key found: {Key}", pair.Key);
                        // Remove the parameter added for the invalid key
                        parameters.RemoveAt(parameters.Count - 1);
                    }
                }
                else // Handle standard tag filter
                {
                    // Use bracket notation for tag keys that might contain special characters
                    innerBuilder.Append($"ARRAY_CONTAINS({alias}.{AzureCosmosDbTabularMemoryRecord.TagsField}[\"{pair.Key}\"], {paramName})");
                    firstInnerCondition = false;
                }
            }

            // Only add this filter's conditions if it generated any valid conditions
            if (!firstInnerCondition) // means innerBuilder is not empty
            {
                if (!firstOuterFilter)
                {
                    outerBuilder.Append(" OR ");
                }
                outerBuilder.Append('(').Append(innerBuilder).Append(')');
                firstOuterFilter = false;
            }
        }

        if (firstOuterFilter) // means outerBuilder is empty (no valid filters found)
        {
            return (string.Empty, Array.Empty<Tuple<string, object>>());
        }

        // Return the complete WHERE clause
        return ($"WHERE {outerBuilder}", parameters);
    }

    /// <summary>
    /// Normalizes field names from camelCase to snake_case.
    /// </summary>
    /// <param name="fieldName">The field name to normalize.</param>
    /// <returns>The normalized field name.</returns>
    private string NormalizeFieldName(string fieldName)
    {
        if (!fieldName.StartsWith("data.")) return fieldName;

        // Extract the part after "data."
        string field = fieldName.Substring(5);

        // Convert camelCase or PascalCase to snake_case
        // e.g., "serverPurpose" → "server_purpose"
        string snakeCase = System.Text.RegularExpressions.Regex.Replace(
            field,
            "(?<=[a-z])(?=[A-Z])",
            "_"
        ).ToLowerInvariant();

        return "data." + snakeCase;
    }

    /// <summary>
    /// Gets the filterable fields from the index.
    /// </summary>
    /// <param name="index">The index name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A dictionary of field types and their available field names.</returns>
    public async Task<Dictionary<string, HashSet<string>>> GetFilterableFieldsAsync(
        string index,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["tags"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["data"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        // Query for a sample of documents to extract field names
        var sql = "SELECT TOP 100 c.tags, c.data FROM c";
        var queryDefinition = new QueryDefinition(sql);

        try
        {
            using var feedIterator = this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(index)
                .GetItemQueryIterator<dynamic>(queryDefinition);

            while (feedIterator.HasMoreResults)
            {
                var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);

                foreach (var item in response)
                {
                    // Extract tag keys
                    if (item.tags != null)
                    {
                        foreach (var tagKey in ((IDictionary<string, object>)item.tags).Keys)
                        {
                            result["tags"].Add(tagKey);
                        }
                    }

                    // Extract data field keys
                    if (item.data != null)
                    {
                        foreach (var dataKey in ((IDictionary<string, object>)item.data).Keys)
                        {
                            result["data"].Add(dataKey);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error getting filterable fields from index {Index}", index);
        }

        return result;
    }

    /// <summary>
    /// Gets the top values for a specific field.
    /// </summary>
    /// <param name="index">The index name.</param>
    /// <param name="fieldType">The field type (tag or data).</param>
    /// <param name="fieldName">The field name.</param>
    /// <param name="limit">The maximum number of values to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of field values and their occurrence counts.</returns>
    public async Task<List<(string Value, int Count)>> GetTopFieldValuesAsync(
        string index,
        string fieldType,
        string fieldName,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var result = new List<(string Value, int Count)>();

        // Determine the field path based on the field type
        string fieldPath = fieldType.Equals("tag", StringComparison.OrdinalIgnoreCase)
            ? $"c.{AzureCosmosDbTabularMemoryRecord.TagsField}[\"{fieldName}\"]"
            : $"c.{AzureCosmosDbTabularMemoryRecord.DataField}[\"{fieldName}\"]";

        // Query for the top values of the specified field
        var sql = $@"
            SELECT {fieldPath} as value, COUNT(1) as count
            FROM c
            WHERE IS_DEFINED({fieldPath})
            GROUP BY {fieldPath}
            ORDER BY count DESC
            OFFSET 0 LIMIT {limit}
        ";

        var queryDefinition = new QueryDefinition(sql);

        try
        {
            using var feedIterator = this._cosmosClient
                .GetDatabase(this._databaseName)
                .GetContainer(index)
                .GetItemQueryIterator<dynamic>(queryDefinition);

            while (feedIterator.HasMoreResults)
            {
                var response = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);

                foreach (var item in response)
                {
                    string value = item.value.ToString();
                    int count = (int)item.count;

                    result.Add((value, count));
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error getting top values for field {FieldType}.{FieldName} from index {Index}",
                fieldType, fieldName, index);
        }

        return result;
    }
}
