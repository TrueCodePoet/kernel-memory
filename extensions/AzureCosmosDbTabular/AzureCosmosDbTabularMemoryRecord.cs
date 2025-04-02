// Copyright (c) Microsoft. All rights reserved.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryStorage;

using Embedding = Microsoft.KernelMemory.Embedding;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

/// <summary>
/// Represents a memory record in Azure Cosmos DB for tabular data.
/// </summary>
internal class AzureCosmosDbTabularMemoryRecord
{
    /// <summary>
    /// Field name for the vector embedding.
    /// </summary>
    internal const string VectorField = "embedding";

    /// <summary>
    /// Field name for the file identifier (used as partition key).
    /// </summary>
    internal const string FileField = "file";

    /// <summary>
    /// Field name for the tags collection.
    /// </summary>
    internal const string TagsField = "tags";

    /// <summary>
    /// Field name for the tabular data.
    /// </summary>
    internal const string DataField = "data";

    private const string IdField = "id";
    private const string PayloadField = "payload";
    private const string SourceField = "source";

    /// <summary>
    /// Gets or sets the unique identifier.
    /// </summary>
    [JsonPropertyName(IdField)]
    public required string Id { get; init; }

    /// <summary>
    /// Gets or sets the file identifier (used as partition key).
    /// </summary>
    [JsonPropertyName(FileField)]
    public required string File { get; init; }

    /// <summary>
    /// Gets or sets the payload.
    /// </summary>
    [JsonPropertyName(PayloadField)]
    public required Dictionary<string, object> Payload { get; init; } = [];

    /// <summary>
    /// Gets or sets the tags.
    /// </summary>
    [JsonPropertyName(TagsField)]
    public TagCollection Tags { get; init; } = [];

    /// <summary>
    /// Gets or sets the vector embedding.
    /// </summary>
    [JsonPropertyName(VectorField)]
    [JsonConverter(typeof(Embedding.JsonConverter))]
    public Embedding Vector { get; init; }

    /// <summary>
    /// Gets or sets the tabular data as key-value pairs.
    /// </summary>
    [JsonPropertyName(DataField)]
    public Dictionary<string, object> Data { get; init; } = [];

    /// <summary>
    /// Gets or sets the source information (e.g., sheet name, row number).
    /// </summary>
    [JsonPropertyName(SourceField)]
    public Dictionary<string, string> Source { get; init; } = [];

    /// <summary>
    /// Gets the partition key for this record.
    /// </summary>
    /// <returns>The partition key.</returns>
    internal PartitionKey GetPartitionKey() => new(File);

    /// <summary>
    /// Gets the column names for a SQL query.
    /// </summary>
    /// <param name="alias">Optional alias for the columns.</param>
    /// <param name="withEmbeddings">Whether to include the embedding field.</param>
    /// <returns>A comma-separated list of column names.</returns>
    internal static string Columns(string? alias = default, bool withEmbeddings = false) =>
        string.Join(',', GetColumns(alias, withEmbeddings));

    private static IEnumerable<string> GetColumns(string? alias = default, bool withEmbeddings = false)
    {
        string[] fieldNames = [IdField, FileField, TagsField, DataField, SourceField, VectorField, PayloadField];
        foreach (var name in fieldNames)
        {
            if (!withEmbeddings
                && string.Equals(name, VectorField, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return string.IsNullOrEmpty(alias) ? name : $"{alias}.{name}";
        }
    }

    /// <summary>
    /// Converts this record to a memory record.
    /// </summary>
    /// <param name="withEmbedding">Whether to include the embedding.</param>
    /// <returns>The memory record.</returns>
    internal MemoryRecord ToMemoryRecord(bool withEmbedding = true)
    {
        var id = DecodeId(Id);
        var memoryRecord = new MemoryRecord
        {
            Id = id,
            Payload = Payload,
            Tags = Tags
        };

        // Add the tabular data to the payload
        if (Data.Count > 0)
        {
            memoryRecord.Payload["tabular_data"] = JsonSerializer.Serialize(Data, AzureCosmosDbTabularConfig.DefaultJsonSerializerOptions);
        }

        // Add the source information to the payload
        if (Source.Count > 0)
        {
            memoryRecord.Payload["source_info"] = JsonSerializer.Serialize(Source, AzureCosmosDbTabularConfig.DefaultJsonSerializerOptions);
        }

        if (withEmbedding)
        {
            memoryRecord.Vector = Vector;
        }

        return memoryRecord;
    }

    /// <summary>
    /// Creates a memory record from a memory record.
    /// </summary>
    /// <param name="record">The memory record.</param>
    /// <param name="data">Optional tabular data to include.</param>
    /// <param name="source">Optional source information to include.</param>
    /// <returns>The memory record.</returns>
    internal static AzureCosmosDbTabularMemoryRecord FromMemoryRecord(
        MemoryRecord record,
        Dictionary<string, object>? data = null,
        Dictionary<string, string>? source = null)
    {
        var id = EncodeId(record.Id);
        var fileId = record.GetFileId();

        var memoryRecord = new AzureCosmosDbTabularMemoryRecord
        {
            Id = id,
            File = fileId,
            Payload = record.Payload,
            Tags = record.Tags,
            Vector = record.Vector,
            Data = data ?? new Dictionary<string, object>(),
            Source = source ?? new Dictionary<string, string>()
        };

        // Extract tabular data from payload if provided
        if (data == null && record.Payload.TryGetValue("tabular_data", out var tabularData) && tabularData is string tabularDataStr)
        {
            try
            {
                var extractedData = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    tabularDataStr,
                    AzureCosmosDbTabularConfig.DefaultJsonSerializerOptions);

                if (extractedData != null)
                {
                    memoryRecord.Data = extractedData;
                }
            }
            catch (JsonException ex)
            {
                // Log or handle the exception as needed
            }
        }

        // Extract source information from payload if provided
        if (source == null && record.Payload.TryGetValue("source_info", out var sourceInfo) && sourceInfo is string sourceInfoStr)
        {
            try
            {
                var extractedSource = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    sourceInfoStr,
                    AzureCosmosDbTabularConfig.DefaultJsonSerializerOptions);

                if (extractedSource != null)
                {
                    memoryRecord.Source = extractedSource;
                }
            }
            catch (JsonException ex)
            {
                // Log or handle the exception as needed
            }
        }

        return memoryRecord;
    }

    private static string EncodeId(string recordId)
    {
        var bytes = Encoding.UTF8.GetBytes(recordId);
        return Convert.ToBase64String(bytes).Replace('=', '_');
    }

    private static string DecodeId(string encodedId)
    {
        var bytes = Convert.FromBase64String(encodedId.Replace('_', '='));
        return Encoding.UTF8.GetString(bytes);
    }
}
