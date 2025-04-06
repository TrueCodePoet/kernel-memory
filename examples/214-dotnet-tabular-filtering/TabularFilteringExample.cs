// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

/// <summary>
/// Example demonstrating how to use the TabularFilterHelper with AzureCosmosDbTabular
/// to enable AI-driven filtering of tabular data.
/// </summary>
public class TabularFilteringExample
{
    private readonly IKernelMemory _memory;
    private readonly IKernel _kernel;
    private readonly TabularFilterHelper _filterHelper;
    private readonly string _indexName;
    private readonly ILogger<TabularFilteringExample> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TabularFilteringExample"/> class.
    /// </summary>
    /// <param name="memory">The kernel memory instance.</param>
    /// <param name="kernel">The semantic kernel instance.</param>
    /// <param name="indexName">The index name.</param>
    /// <param name="logger">Optional logger.</param>
    public TabularFilteringExample(
        IKernelMemory memory,
        IKernel kernel,
        string indexName,
        ILogger<TabularFilteringExample>? logger = null)
    {
        this._memory = memory;
        this._kernel = kernel;
        this._indexName = indexName;
        this._logger = logger ?? Microsoft.KernelMemory.Diagnostics.DefaultLogger.Factory.CreateLogger<TabularFilteringExample>();
        this._filterHelper = new TabularFilterHelper(memory);
    }

    /// <summary>
    /// Process a natural language query and generate an appropriate filter.
    /// </summary>
    /// <param name="userQuery">The user's natural language query.</param>
    /// <returns>The search result.</returns>
    public async Task<MemoryAnswer> ProcessQueryAsync(string userQuery)
    {
        this._logger.LogInformation("Processing query: {Query}", userQuery);

        // 1. Use LLM to identify potential filter criteria (field hints and value hints)
        var filterCriteria = await this.IdentifyFilterCriteriaAsync(userQuery);

        if (filterCriteria.Count == 0)
        {
            this._logger.LogInformation("No specific filter criteria identified. Performing general search.");
            return await this._memory.AskAsync(userQuery, index: this._indexName);
        }

        this._logger.LogInformation("Identified filter criteria: {Criteria}",
            JsonSerializer.Serialize(filterCriteria));

        // 2. Build the filter using the helper and semantic matching
        var finalFilter = new MemoryFilter();
        bool filterApplied = false;

        foreach (var criterion in filterCriteria.OrderByDescending(c => c.Importance))
        {
            // 2a. Find the best matching field name
            var fields = await this._filterHelper.GetFilterableFieldsAsync(this._indexName);
            var (fieldType, fieldName, fieldConfidence) = await this.FindBestFieldMatchAsync(
                fields, criterion.FieldNameHint);

            this._logger.LogInformation("Field match for '{FieldHint}': Found '{FieldName}' ({FieldType}) with confidence {Confidence:P1}",
                criterion.FieldNameHint, fieldName, fieldType, fieldConfidence);

            if (fieldConfidence < 0.6 || fieldName == "none")
            {
                this._logger.LogInformation("Field match confidence too low. Skipping criterion.");
                continue;
            }

            // 2b. Find the best matching value for that field
            var topValues = await this._filterHelper.GetTopFieldValuesAsync(
                this._indexName, fieldType, fieldName);

            var (value, valueConfidence) = await this.FindBestValueMatchAsync(
                fieldName, topValues, criterion.ValueHint);

            this._logger.LogInformation("Value match for '{ValueHint}' in field '{FieldName}': Found '{Value}' with confidence {Confidence:P1}",
                criterion.ValueHint, fieldName, value, valueConfidence);

            if (valueConfidence < 0.6)
            {
                this._logger.LogInformation("Value match confidence too low. Skipping criterion.");
                continue;
            }

            // 2c. Add the confirmed criterion to the final filter
            if (fieldType == "tag")
            {
                finalFilter.Add(fieldName, value);
            }
            else // data field
            {
                finalFilter.Add($"data.{fieldName}", value);
            }

            filterApplied = true;
            this._logger.LogInformation("Added filter: {FieldType}.{FieldName} = {Value}",
                fieldType, fieldName, value);

            // Optional: Stop after finding the most important, high-confidence filter
            if (criterion.Importance >= 4 && fieldConfidence > 0.8 && valueConfidence > 0.8)
            {
                this._logger.LogInformation("Stopping filter generation after high-confidence primary criterion.");
                break;
            }
        }

        // 3. Execute the search/ask with the generated filter
        this._logger.LogInformation("Executing query with final filter: {Filter}",
            JsonSerializer.Serialize(finalFilter));

        var answer = await this._memory.AskAsync(userQuery, index: this._indexName,
            filter: filterApplied ? finalFilter : null);

        return answer;
    }

    /// <summary>
    /// Use the LLM to identify potential filter criteria from a natural language query.
    /// </summary>
    /// <param name="userQuery">The user's natural language query.</param>
    /// <returns>A list of filter criteria.</returns>
    private async Task<List<FilterCriterion>> IdentifyFilterCriteriaAsync(string userQuery)
    {
        string promptTemplate = @"
Analyze the user's question about tabular data (like server inventories, logs, etc.)
Identify specific criteria mentioned that could be used for filtering.

User question: {{$question}}

Extract up to 3 potential filtering criteria. For each, provide:
1. A hint for the field name (e.g., 'environment', 'status', 'location', 'server type')
2. The specific value mentioned (e.g., 'Production', 'Running', 'East US', 'Database')
3. The type of field you think it is ('tag' or 'data') - default to 'data' if unsure.
4. An importance score (1-5, 5=most important)

Return ONLY the JSON array, nothing else. Example:
[
  { ""fieldType"": ""data"", ""fieldNameHint"": ""environment"", ""valueHint"": ""Production"", ""importance"": 5 },
  { ""fieldType"": ""data"", ""fieldNameHint"": ""status"", ""valueHint"": ""Running"", ""importance"": 3 }
]

If no specific criteria are found, return an empty JSON array [].

JSON response:";

        var function = this._kernel.CreateFunctionFromPrompt(
            promptTemplate,
            new OpenAIPromptExecutionSettings { Temperature = 0.0 });

        var result = await this._kernel.InvokeAsync(function, new KernelArguments
        {
            ["question"] = userQuery
        });

        try
        {
            return JsonSerializer.Deserialize<List<FilterCriterion>>(result.GetValue<string>())
                ?? new List<FilterCriterion>();
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error parsing filter criteria from LLM");
            return new List<FilterCriterion>();
        }
    }

    /// <summary>
    /// Find the best matching field for a field name hint.
    /// </summary>
    /// <param name="availableFields">The available fields.</param>
    /// <param name="fieldNameHint">The field name hint.</param>
    /// <returns>A tuple containing the field type, field name, and confidence score.</returns>
    private async Task<(string FieldType, string FieldName, double Confidence)> FindBestFieldMatchAsync(
        Dictionary<string, HashSet<string>> availableFields,
        string fieldNameHint)
    {
        // Build a description of available fields
        var fieldDescriptions = new List<(string Type, string Name, string Description)>();

        foreach (var tagField in availableFields["tags"])
        {
            fieldDescriptions.Add(("tag", tagField, $"Tag: {tagField}"));
        }

        foreach (var dataField in availableFields["data"])
        {
            // Convert snake_case to readable format
            string readableName = string.Join(" ", dataField.Split('_')
                .Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1)));

            fieldDescriptions.Add(("data", dataField, $"Data field: {readableName}"));
        }

        // Use the LLM to find the best matching field
        string promptTemplate = @"
You are matching a user's filter intent to the most relevant available database field.

Available fields:
{{$fields}}

User's filter intent: {{$fieldNameHint}}

Analyze the intent and determine the best matching field.
Return ONLY JSON with properties: fieldType, fieldName, confidence (0-1), reasoning.
If no good match, use fieldName 'none'.

JSON response:";

        var fieldsList = string.Join("\n", fieldDescriptions.Select(f => $"- {f.Description} (internal name: {f.Name})"));

        var function = this._kernel.CreateFunctionFromPrompt(
            promptTemplate,
            new OpenAIPromptExecutionSettings { Temperature = 0.0 });

        var result = await this._kernel.InvokeAsync(function, new KernelArguments
        {
            ["fields"] = fieldsList,
            ["fieldNameHint"] = fieldNameHint
        });

        try
        {
            var response = JsonSerializer.Deserialize<FieldMatchResponse>(result.GetValue<string>());
            return (response.FieldType, response.FieldName, response.Confidence);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error parsing field match response");
            return ("none", "none", 0.0);
        }
    }

    /// <summary>
    /// Find the best matching value for a field based on a value hint.
    /// </summary>
    /// <param name="fieldName">The field name.</param>
    /// <param name="topValues">The top values for the field.</param>
    /// <param name="valueHint">The value hint.</param>
    /// <returns>A tuple containing the value and confidence score.</returns>
    private async Task<(string Value, double Confidence)> FindBestValueMatchAsync(
        string fieldName,
        List<(string Value, int Count)> topValues,
        string valueHint)
    {
        if (topValues.Count == 0)
        {
            return (valueHint, 0.5); // No values to match against, use the hint as is with medium confidence
        }

        // Use the LLM to find the best matching value
        string promptTemplate = @"
You are matching a user's filter value intent to the most relevant value for a database field.

Field: {{$fieldName}}
Available values (with counts):
{{$values}}

User's value intent: {{$valueHint}}

Analyze the intent and determine the best matching value.
Return ONLY JSON with properties: value, confidence (0-1), reasoning.
If no good match, suggest an appropriate value based on the intent.

JSON response:";

        var valuesList = string.Join("\n", topValues.Select(v => $"- {v.Value} ({v.Count} occurrences)"));

        var function = this._kernel.CreateFunctionFromPrompt(
            promptTemplate,
            new OpenAIPromptExecutionSettings { Temperature = 0.0 });

        var result = await this._kernel.InvokeAsync(function, new KernelArguments
        {
            ["fieldName"] = fieldName,
            ["values"] = valuesList,
            ["valueHint"] = valueHint
        });

        try
        {
            var response = JsonSerializer.Deserialize<ValueMatchResponse>(result.GetValue<string>());
            return (response.Value, response.Confidence);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error parsing value match response");
            return (valueHint, 0.0);
        }
    }

    /// <summary>
    /// Filter criterion identified by the LLM.
    /// </summary>
    private class FilterCriterion
    {
        public string FieldType { get; set; } = "data";
        public string FieldNameHint { get; set; } = string.Empty;
        public string ValueHint { get; set; } = string.Empty;
        public int Importance { get; set; } = 3;
    }

    /// <summary>
    /// Response from the LLM for field matching.
    /// </summary>
    private class FieldMatchResponse
    {
        public string FieldType { get; set; } = "none";
        public string FieldName { get; set; } = "none";
        public double Confidence { get; set; } = 0.0;
        public string Reasoning { get; set; } = string.Empty;
    }

    /// <summary>
    /// Response from the LLM for value matching.
    /// </summary>
    private class ValueMatchResponse
    {
        public string Value { get; set; } = string.Empty;
        public double Confidence { get; set; } = 0.0;
        public string Reasoning { get; set; } = string.Empty;
    }
}
