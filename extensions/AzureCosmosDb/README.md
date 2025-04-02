# Azure Cosmos DB Connector for Kernel Memory

This extension provides integration between [Microsoft Kernel Memory](https://github.com/microsoft/kernel-memory) and [Azure Cosmos DB](https://azure.microsoft.com/en-us/products/cosmos-db/) for storing and retrieving memory records.

## Features

- Store memory records (including embeddings) in Azure Cosmos DB NoSQL containers.
- Retrieve records based on similarity using a basic placeholder query (Note: True vector search using Cosmos DB's native capabilities is not currently implemented in this connector).
- Filter memory records based on tags.
- Fully implements the `IMemoryDb` interface.

## Requirements

- Azure Cosmos DB account with NoSQL API
- .NET 8.0 or later

## Configuration

To use this extension, you need to provide:

1. Azure Cosmos DB endpoint URL
2. Azure Cosmos DB API key

## Usage

### Basic Setup

```csharp
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDb;

// Create a memory builder with Azure Cosmos DB
var memory = new KernelMemoryBuilder()
    .WithAzureOpenAITextEmbeddingGeneration(
        endpoint: "https://your-openai-endpoint.com",
        apiKey: "your-openai-api-key",
        deploymentName: "your-embedding-deployment-name")
    .WithAzureCosmosDbMemory(
        endpoint: "https://your-cosmosdb-account.documents.azure.com:443/",
        apiKey: "your-cosmosdb-api-key")
    .Build();
```

### Advanced Configuration

```csharp
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDb;

// Create a custom configuration
var cosmosConfig = new AzureCosmosDbConfig
{
    Endpoint = "https://your-cosmosdb-account.documents.azure.com:443/",
    APIKey = "your-cosmosdb-api-key"
};

// Create a memory builder with the custom configuration
var memory = new KernelMemoryBuilder()
    .WithAzureOpenAITextEmbeddingGeneration(
        endpoint: "https://your-openai-endpoint.com",
        apiKey: "your-openai-api-key",
        deploymentName: "your-embedding-deployment-name")
    .WithAzureCosmosDbMemory(cosmosConfig)
    .Build();
```

## Implementation Details

The extension creates a database named "memory" (hardcoded) in your Azure Cosmos DB account. Each memory index is stored as a separate container within this database.

**Important Note on Vector Search:** The current implementation **does not** configure or utilize Azure Cosmos DB's native vector indexing or search features. The `GetSimilarListAsync` method uses a basic placeholder query and does not perform true vector distance calculations. Implementing and configuring native vector search would require modifications to this connector.

Memory records are stored with the following structure:
- `id`: The original `MemoryRecord.Id`, Base64 encoded for compatibility with Cosmos DB ID constraints.
- `file`: The file identifier derived from `MemoryRecord.Id`, used as the **partition key** for the container.
- `tags`: Collection of metadata tags.
- `embedding`: Vector representation of the memory content (stored but not currently used for native vector search by this connector).
- `payload`: Additional data associated with the memory record.
