// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryStorage;

// No longer using the alias as we change the property type
// using Embedding = Microsoft.KernelMemory.Embedding; 

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
    // Remove JsonConverter attribute and change type to float[]
    // [JsonConverter(typeof(Embedding.JsonConverter))] 
    public float[] Vector { get; init; } = Array.Empty<float>();

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

        if (withEmbedding && this.Vector.Length > 0) // Use Length for array
        {
            // Reconstruct Embedding object from the float array
            memoryRecord.Vector = new Embedding(this.Vector);
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

        // Initialize with empty data/source dictionaries
        Dictionary<string, object> extractedData = new();
        Dictionary<string, string> extractedSource = new();

        // Extract structured data from the text field in the payload
        if (record.Payload.TryGetValue("text", out var textObj) && textObj is string text && !string.IsNullOrEmpty(text))
        {
            try
            {
                // Check if text is in the new sentence format
                if (text.StartsWith("Record from worksheet"))
                {
                    ParseSentenceFormat(text, extractedData, extractedSource);
                }
                // Check if text is in the old key-value format
                else if (text.Contains("\n") && text.Contains(":"))
                {
                    ParseKeyValueFormat(text, extractedData, extractedSource);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN: Failed to parse text field for record {record.Id}: {ex.Message}");
            }
        }

        // Use provided data/source parameters as fallback if available
        var finalData = data ?? extractedData;
        var finalSource = source ?? extractedSource;

        // Create a single record instance with all the data
        var memoryRecord = new AzureCosmosDbTabularMemoryRecord
        {
            Id = id,
            File = fileId,
            Payload = record.Payload,
            Tags = record.Tags,
            // Vector assignment remains unchanged
            Vector = record.Vector.Data.ToArray(),
            Data = finalData,
            Source = finalSource
        };

        return memoryRecord;
    }

    internal static string EncodeId(string recordId) // Changed from private to internal static
    {
        var bytes = Encoding.UTF8.GetBytes(recordId);
        return Convert.ToBase64String(bytes).Replace('=', '_');
    }

    private static string DecodeId(string encodedId)
    {
        var bytes = Convert.FromBase64String(encodedId.Replace('_', '='));
        return Encoding.UTF8.GetString(bytes);
    }

    // Parse text in the new sentence format: "Record from worksheet Sheet1, row 123: Column1 is Value1. Column2 is Value2."
    private static void ParseSentenceFormat(string text, Dictionary<string, object> data, Dictionary<string, string> source)
    {
        // Extract worksheet and row info
        int worksheetStart = text.IndexOf("Record from worksheet ") + "Record from worksheet ".Length;
        int rowStart = text.IndexOf(", row ");
        if (rowStart > worksheetStart)
        {
            string worksheet = text.Substring(worksheetStart, rowStart - worksheetStart);

            int rowEnd = text.IndexOf(":", rowStart);
            if (rowEnd > rowStart)
            {
                string rowStr = text.Substring(rowStart + ", row ".Length, rowEnd - (rowStart + ", row ".Length));
                if (int.TryParse(rowStr, out int rowNum))
                {
                    source["_worksheet"] = worksheet;
                    source["_rowNumber"] = rowNum.ToString();

                    // Now parse the column data
                    string dataSection = text.Substring(rowEnd + 1).Trim();
                    string[] pairs = dataSection.Split('.');

                    foreach (string pair in pairs)
                    {
                        string trimmed = pair.Trim();
                        if (string.IsNullOrEmpty(trimmed)) continue;

                        int isIndex = trimmed.IndexOf(" is ");
                        if (isIndex > 0)
                        {
                            string key = trimmed.Substring(0, isIndex).Trim();
                            string valueStr = trimmed.Substring(isIndex + " is ".Length).Trim();

                            // Convert value to appropriate type
                            object value = ConvertToTypedValue(valueStr);
                            data[key] = value;
                        }
                    }
                }
            }
        }
    }

    // Parse text in the old key-value format: "Key1: Value1\nKey2: Value2"
    private static void ParseKeyValueFormat(string text, Dictionary<string, object> data, Dictionary<string, string> source)
    {
        string[] lines = text.Split('\n');

        foreach (string line in lines)
        {
            int colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) continue;

            string key = line.Substring(0, colonIndex).Trim();
            string valueStr = line.Substring(colonIndex + 1).Trim();

            // Handle worksheet and row number specially
            if (key == "Worksheet")
            {
                // Format: "Worksheet: SheetName, Row: 123"
                int commaIndex = valueStr.IndexOf(',');
                if (commaIndex > 0)
                {
                    string worksheet = valueStr.Substring(0, commaIndex).Trim();
                    source["_worksheet"] = worksheet;

                    string rowPart = valueStr.Substring(commaIndex + 1).Trim();
                    if (rowPart.StartsWith("Row: ") && int.TryParse(rowPart.Substring("Row: ".Length), out int rowNum))
                    {
                        source["_rowNumber"] = rowNum.ToString();
                    }
                }
            }
            // Handle metadata fields
            else if (key.StartsWith("_"))
            {
                source[key] = valueStr;
            }
            // Handle regular data fields
            else
            {
                // Convert value to appropriate type
                object value = ConvertToTypedValue(valueStr);
                data[key] = value;
            }
        }
    }

    // Convert string values to appropriate types
    private static object ConvertToTypedValue(string valueStr)
    {
        // Handle null values
        if (valueStr == "NULL" || string.IsNullOrEmpty(valueStr))
        {
            return null;
        }

        // Try to convert to boolean
        if (bool.TryParse(valueStr, out bool boolValue))
        {
            return boolValue;
        }

        // Try to convert to integer
        if (int.TryParse(valueStr, out int intValue))
        {
            return intValue;
        }

        // Try to convert to double
        if (double.TryParse(valueStr, out double doubleValue))
        {
            return doubleValue;
        }

        // Default to string
        return valueStr;
    }
}
