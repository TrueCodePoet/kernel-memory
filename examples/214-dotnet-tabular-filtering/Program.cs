// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

/// <summary>
/// Example program demonstrating AI-driven filtering of tabular data using AzureCosmosDbTabular.
/// </summary>
public class Program
{
    /// <summary>
    /// Main entry point.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== Tabular Filtering Example ===");

        // 1. Create a Kernel Memory instance with AzureCosmosDbTabular
        var memory = new KernelMemoryBuilder()
            .WithAzureOpenAITextEmbeddingGeneration(
                endpoint: Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "https://your-openai-endpoint.com",
                apiKey: Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "your-openai-api-key",
                deploymentName: Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") ?? "text-embedding-ada-002")
            .WithAzureCosmosDbTabularMemory(
                endpoint: Environment.GetEnvironmentVariable("AZURE_COSMOSDB_ENDPOINT") ?? "https://your-cosmosdb-account.documents.azure.com:443/",
                apiKey: Environment.GetEnvironmentVariable("AZURE_COSMOSDB_API_KEY") ?? "your-cosmosdb-api-key")
            .WithTabularExcelDecoder(config =>
            {
                // Configure Excel parsing options
                config.UseFirstRowAsHeader = true;
                config.PreserveDataTypes = true;
                config.ProcessAllWorksheets = true;
                config.NormalizeHeaderNames = true; // Convert spaces to underscores, etc.
            })
            .Build();

        // 2. Create a Semantic Kernel instance for LLM-based filtering
        var kernel = Kernel.CreateBuilder()
            .AddAzureOpenAIChatCompletion(
                deploymentName: Environment.GetEnvironmentVariable("AZURE_OPENAI_CHAT_DEPLOYMENT") ?? "gpt-4",
                endpoint: Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "https://your-openai-endpoint.com",
                apiKey: Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "your-openai-api-key")
            .Build();

        // 3. Create the TabularFilteringExample
        var filteringExample = new TabularFilteringExample(
            memory: memory,
            kernel: kernel,
            indexName: "servers-inventory" // Replace with your actual index name
        );

        // 4. Import an Excel file (if it doesn't exist yet)
        if (args.Length > 0 && args[0] == "--import")
        {
            Console.WriteLine("Importing Excel file...");
            await memory.ImportDocumentAsync("servers.xlsx", documentId: "servers-inventory");
            Console.WriteLine("Import complete.");
        }

        // 5. Process natural language queries
        await ProcessQueryAsync(filteringExample, "Show me all production servers");
        await ProcessQueryAsync(filteringExample, "What database servers are running in East US?");
        await ProcessQueryAsync(filteringExample, "List servers related to the TAMI application");

        // 6. Custom query from command line
        if (args.Length > 0 && args[0] != "--import")
        {
            string userQuery = string.Join(" ", args);
            await ProcessQueryAsync(filteringExample, userQuery);
        }
    }

    /// <summary>
    /// Process a query and display the results.
    /// </summary>
    /// <param name="filteringExample">The filtering example.</param>
    /// <param name="query">The query.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private static async Task ProcessQueryAsync(TabularFilteringExample filteringExample, string query)
    {
        Console.WriteLine($"\n\nQuery: {query}");
        Console.WriteLine("Processing...");

        var answer = await filteringExample.ProcessQueryAsync(query);

        Console.WriteLine($"Answer: {answer.Result}");

        if (answer.RelevantSources.Count == 0)
        {
            Console.WriteLine("No relevant sources found.");
        }
        else
        {
            Console.WriteLine("Relevant sources:");
            foreach (var source in answer.RelevantSources)
            {
                Console.WriteLine($"- {source.SourceName}");

                if (source.Partitions.Count > 0)
                {
                    var partition = source.Partitions[0];

                    // Display source metadata if available
                    if (partition.AdditionalMetadata != null &&
                        partition.AdditionalMetadata.TryGetValue("_worksheet", out var worksheet) &&
                        partition.AdditionalMetadata.TryGetValue("_rowNumber", out var rowNumber))
                    {
                        Console.WriteLine($"  Worksheet: {worksheet}, Row: {rowNumber}");
                    }

                    // Display a few data fields if available
                    if (partition.AdditionalMetadata != null)
                    {
                        foreach (var kvp in partition.AdditionalMetadata.Take(5))
                        {
                            if (!kvp.Key.StartsWith("_"))
                            {
                                Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
                            }
                        }
                    }
                }
            }
        }
    }
}
