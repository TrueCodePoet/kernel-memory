# Azure Cosmos DB Connector for Kernel Memory

This extension provides integration between [Microsoft Kernel Memory](https://github.com/microsoft/kernel-memory) and [Azure Cosmos DB](https://azure.microsoft.com/en-us/products/cosmos-db/) for storing and retrieving memory records with vector search capabilities.

## Features

- Store memory records in Azure Cosmos DB NoSQL containers
- Perform vector similarity search using Azure Cosmos DB's vector search capabilities
- Filter memory records based on metadata
- Fully implements the `IMemoryDb` interface

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

The extension creates a database named "memory" in your Azure Cosmos DB account. Each memory index is stored as a separate container within this database. The containers are configured with vector search capabilities using the Quantized Flat index type and Cosine distance function.

Memory records are stored with the following structure:
- `id`: Base64-encoded unique identifier
- `file`: File identifier (used as partition key)
- `tags`: Collection of metadata tags
- `embedding`: Vector representation of the memory content
- `payload`: Additional data associated with the memory record
