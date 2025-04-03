# Azure Cosmos DB Tabular Data Connector for Kernel Memory

This extension provides integration between [Microsoft Kernel Memory](https://github.com/microsoft/kernel-memory) and [Azure Cosmos DB](https://azure.microsoft.com/en-us/products/cosmos-db/) for storing and querying tabular data (Excel, CSV, JSON) with structured query capabilities.

It includes a specialized Excel decoder (`TabularExcelDecoder`) that preserves the tabular structure of Excel files when importing them into Kernel Memory.

## Features

- Store tabular data (rows from Excel, CSV, JSON) as individual records in Azure Cosmos DB.
- Perform structured queries against tabular data fields using special filter syntax (`data.FieldName`).
- Retrieve records based on vector similarity using Azure Cosmos DB's native vector search capabilities (`VectorDistance` function with Cosine distance). This allows for semantic search over the tabular data content.
- Fully implements the `IMemoryDb` interface.
- Includes an optional specialized Excel decoder (`TabularExcelDecoder`) that:
  - Preserves column-row relationships
  - Maintains data types (numbers, dates, booleans)
  - Creates one document per row for precise querying
  - Uses column headers as field names
  - Includes metadata about the source (worksheet name, row number)

## Requirements

- Azure Cosmos DB account with NoSQL API
- .NET 8.0 or later

## Configuration

To use this extension, you need to provide:

1. Azure Cosmos DB endpoint URL
2. Azure Cosmos DB API key
3. (Optional) Database name (defaults to "memory")

## Usage

### Basic Setup

```csharp
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

// Create a memory builder with Azure Cosmos DB Tabular
var memory = new KernelMemoryBuilder()
    .WithAzureOpenAITextEmbeddingGeneration(
        endpoint: "https://your-openai-endpoint.com",
        apiKey: "your-openai-api-key",
        deploymentName: "your-embedding-deployment-name")
    .WithAzureCosmosDbTabularMemory(
        endpoint: "https://your-cosmosdb-account.documents.azure.com:443/",
        apiKey: "your-cosmosdb-api-key")
    .Build();
```

### Advanced Configuration

```csharp
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular;

// Create a custom configuration
var cosmosConfig = new AzureCosmosDbTabularConfig
{
    Endpoint = "https://your-cosmosdb-account.documents.azure.com:443/",
    APIKey = "your-cosmosdb-api-key",
    DatabaseName = "your-database-name"
};

// Create a memory builder with the custom configuration
var memory = new KernelMemoryBuilder()
    .WithAzureOpenAITextEmbeddingGeneration(
        endpoint: "https://your-openai-endpoint.com",
        apiKey: "your-openai-api-key",
        deploymentName: "your-embedding-deployment-name")
    .WithAzureCosmosDbTabularMemory(cosmosConfig)
    .Build();
```

## Excel File Processing

This extension includes a specialized Excel decoder (`TabularExcelDecoder`) that preserves the tabular structure of Excel files when importing them into Kernel Memory. Unlike the standard Excel decoder that flattens data to text, this decoder:

- Preserves column-row relationships
- Maintains data types (numbers, dates, booleans)
- Creates one document per row for precise querying
- Uses column headers as field names
- Includes metadata about the source (worksheet name, row number)

### Configuring the Excel Decoder

When setting up your Kernel Memory instance, you can configure the TabularExcelDecoder:

```csharp
// Create a memory builder with Azure Cosmos DB Tabular and Excel decoder
var memory = new KernelMemoryBuilder()
    .WithAzureOpenAITextEmbeddingGeneration(
        endpoint: "https://your-openai-endpoint.com",
        apiKey: "your-openai-api-key",
        deploymentName: "your-embedding-deployment-name")
    .WithAzureCosmosDbTabularMemory(
        endpoint: "https://your-cosmosdb-account.documents.azure.com:443/",
        apiKey: "your-cosmosdb-api-key")
    .WithTabularExcelDecoder(config => {
        // Configure Excel parsing options
        config.UseFirstRowAsHeader = true;
        config.PreserveDataTypes = true;
        config.ProcessAllWorksheets = true;
        // Optionally specify which worksheets to process
        // config.WorksheetsToProcess = new List<string> { "Sheet1", "Data" };
    })
    .Build();
```

### Importing Excel Files

Import Excel files the same way you would import any document:

```csharp
// Import an Excel file
await memory.ImportDocumentAsync("servers.xlsx", documentId: "servers-inventory");
```

Each row in the Excel file will be stored as a separate document in Cosmos DB, with:
- Column headers as field names
- Cell values preserved with their original data types
- Metadata about the source (worksheet name, row number)

### Advanced Excel Processing

For complex Excel files with multiple worksheets or special formatting:

```csharp
// Configure the TabularExcelDecoder with advanced options
var memory = new KernelMemoryBuilder()
    .WithAzureOpenAITextEmbeddingGeneration(/* config */)
    .WithAzureCosmosDbTabularMemory(/* config */)
    .WithTabularExcelDecoder(config => {
        // Use a specific row as header (0-based index)
        config.UseFirstRowAsHeader = true;
        config.HeaderRowIndex = 2; // Use the 3rd row as header
        
        // Process only specific worksheets
        config.ProcessAllWorksheets = false;
        config.WorksheetsToProcess = new List<string> { "Servers", "Network" };
        
        // Data type handling
        config.PreserveDataTypes = true;
        config.DateFormat = "yyyy-MM-dd";
        config.TimeFormat = "HH:mm:ss";
        
        // Column naming
        config.NormalizeHeaderNames = true; // Convert spaces to underscores, etc.
        config.DefaultColumnPrefix = "Field"; // For columns without headers
        
        // Row/column filtering
        config.SkipEmptyRows = true;
        config.SkipHiddenRows = true;
        config.SkipHiddenColumns = true;
    })
    .Build();
```

## Storing Tabular Data

When storing tabular data manually (without using the Excel decoder), you need to include the data as key-value pairs in the memory record's payload:

```csharp
// Example: Storing a row from an Excel spreadsheet
var data = new Dictionary<string, object>
{
    { "ServerName", "SVR01" },
    { "Environment", "Production" },
    { "Location", "East US" },
    { "Status", "Running" }
};

var sourceInfo = new Dictionary<string, string>
{
    { "SheetName", "Servers" },
    { "RowNumber", "5" }
};

// Create a memory record with the tabular data
var record = new MemoryRecord
{
    Id = Guid.NewGuid().ToString(),
    Payload = new Dictionary<string, object>
    {
    { "tabular_data", JsonSerializer.Serialize(data) },
    // The `source_info` key maps to the `source` field in the stored document
    { "source_info", JsonSerializer.Serialize(sourceInfo) } 
}
};

// Add tags if needed
record.Tags.Add("type", "server");
record.Tags.Add("department", "IT");

// Store the record
await memory.SaveAsync(record);
```

## Querying Tabular Data

This extension supports querying data in multiple ways:

1.  **Vector Similarity Search**: Perform semantic searches using vector embeddings via the `GetSimilarListAsync` method. This leverages Azure Cosmos DB's native vector search.
2.  **Structured Field Queries**: Filter records based on specific field values within the tabular data using the special `data.` prefix in filter tags (e.g., `filter.Add("data.Environment", "Production")`).
3.  **Hybrid Search**: Combine vector similarity search with structured field queries and standard tag filters within the same `GetSimilarListAsync` call for powerful, targeted retrieval.

### Structured Field Queries

To query tabular data fields, use the special `data.` prefix in your filter tags:

```csharp
// Query for all servers in the Production environment
var filter = new MemoryFilter();
filter.Add("data.Environment", "Production");

// Execute the query
var results = await memory.SearchAsync(filter: filter);
```

You can combine multiple field conditions:

```csharp
// Query for Production servers in East US
var filter = new MemoryFilter();
filter.Add("data.Environment", "Production");
filter.Add("data.Location", "East US");

// Execute the query
var results = await memory.SearchAsync(filter: filter);
```

You can also combine standard tags with data field filters:

```csharp
// Query for Production servers in the IT department
var filter = new MemoryFilter();
filter.Add("data.Environment", "Production");
filter.Add("department", "IT");

// Execute the query
var results = await memory.SearchAsync(filter: filter);
```

## Implementation Details

The extension creates a database (default name "memory", configurable via `DatabaseName`) in your Azure Cosmos DB account. Each memory index is stored as a separate container within this database.

**Vector Search Implementation:** This connector utilizes Azure Cosmos DB's native vector search capabilities.
- When an index is created (`CreateIndexAsync`), a vector index policy (Flat index, Cosine distance) is automatically configured on the `/embedding` path, assuming the standard serialization of Kernel Memory's `Embedding` type.
- Similarity searches (`GetSimilarListAsync`) use the `VectorDistance` function in Cosmos DB queries comparing against the `c.embedding` field to perform efficient vector comparisons.

Memory records are stored with the following structure:
- `id`: The original `MemoryRecord.Id`, Base64 encoded for compatibility with Cosmos DB ID constraints.
- `file`: The file identifier derived from `MemoryRecord.Id`, used as the **partition key** for the container.
- `tags`: Collection of metadata tags.
- `embedding`: Vector representation of the memory content, indexed for vector search.
- `data`: Tabular data extracted from the `tabular_data` payload field, stored as key-value pairs.
- `source`: Source information extracted from the `source_info` payload field (e.g., sheet name, row number).
- `payload`: The original payload dictionary associated with the memory record (excluding `tabular_data` and `source_info` if they were processed).

## Natural Language Query Translation

This extension focuses on the storage and retrieval of tabular data using structured filters. To translate natural language queries (e.g., "list all servers in Production") into structured filters, you would typically use an LLM in a preceding pipeline step.

For example:

```csharp
// 1. User asks a natural language question
string userQuery = "List all servers in the Production environment";

// 2. Use an LLM to translate the question into structured filters
// (This would be implemented as a custom pipeline handler)
var structuredFilter = await TranslateQueryToFilterAsync(userQuery);
// Result: { "data.Environment": "Production" }

// 3. Create a memory filter from the structured filter
var filter = new MemoryFilter();
foreach (var (key, value) in structuredFilter)
{
    filter.Add(key, value);
}

// 4. Execute the query using the structured filter
var results = await memory.SearchAsync(filter: filter);
```

The translation step is not included in this extension and would need to be implemented separately.
