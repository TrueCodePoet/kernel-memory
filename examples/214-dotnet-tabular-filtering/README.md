# AI-Driven Tabular Data Filtering Example

This example demonstrates how to use the AzureCosmosDbTabular extension with AI-driven filtering capabilities to enable natural language queries over tabular data.

## Features

- Import Excel files with tabular data into Kernel Memory
- Use AI to translate natural language queries into structured filters
- Perform semantic field and value matching
- Execute filtered searches with high precision

## Prerequisites

- Azure Cosmos DB account with NoSQL API
- Azure OpenAI service with embedding and chat models
- .NET 8.0 SDK

## Setup

1. Set the following environment variables:

```bash
# Azure OpenAI
export AZURE_OPENAI_ENDPOINT="https://your-openai-service.openai.azure.com/"
export AZURE_OPENAI_API_KEY="your-openai-api-key"
export AZURE_OPENAI_EMBEDDING_DEPLOYMENT="text-embedding-ada-002"
export AZURE_OPENAI_CHAT_DEPLOYMENT="gpt-4"

# Azure Cosmos DB
export AZURE_COSMOSDB_ENDPOINT="https://your-cosmosdb-account.documents.azure.com:443/"
export AZURE_COSMOSDB_API_KEY="your-cosmosdb-api-key"
```

2. Create a sample Excel file named `servers.xlsx` with the following structure:

| Server Name | Environment | Location   | Server Type | Status  | Application | Department |
| ----------- | ----------- | ---------- | ----------- | ------- | ----------- | ---------- |
| SVR001      | Production  | East US    | Web         | Running | TAMI        | IT         |
| SVR002      | Production  | East US    | Database    | Running | TAMI        | IT         |
| SVR003      | Production  | West US    | Web         | Running | CRM         | Sales      |
| SVR004      | Staging     | East US    | Web         | Running | TAMI        | IT         |
| SVR005      | Development | West US    | Database    | Stopped | CRM         | Sales      |
| SVR006      | Production  | Central US | Analytics   | Running | BI          | Finance    |
| SVR007      | Production  | East US    | Database    | Running | ERP         | Finance    |
| SVR008      | Development | East US    | Web         | Running | Portal      | HR         |
| SVR009      | Production  | West US    | Web         | Stopped | Portal      | HR         |
| SVR010      | Staging     | Central US | Database    | Running | BI          | Finance    |

## Running the Example

1. Build the project:

```bash
dotnet build
```

2. Import the Excel file:

```bash
dotnet run --import
```

3. Run the example with predefined queries:

```bash
dotnet run
```

4. Run the example with a custom query:

```bash
dotnet run "Show me all database servers in Production"
```

## How It Works

The example demonstrates a multi-stage approach to AI-driven filtering:

1. **Query Analysis**: The LLM analyzes the natural language query to identify potential filter criteria (field hints and value hints).

2. **Field Matching**: For each identified criterion, the system:
   - Retrieves available fields from the database using `GetFilterableFieldsAsync`
   - Uses the LLM to find the best matching field for each hint

3. **Value Matching**: For each matched field, the system:
   - Retrieves the most common values for that field using `GetTopFieldValuesAsync`
   - Uses the LLM to find the best matching value for each hint

4. **Filter Construction**: The system constructs a structured filter based on the matched fields and values.

5. **Execution**: The system executes the search with the constructed filter.

This approach ensures high-precision filtering by grounding the LLM's suggestions in the actual database schema and values.

## Key Components

- **TabularFilterHelper**: Provides methods for discovering available fields and their common values.
- **TabularFilteringExample**: Implements the AI-driven filtering logic using Semantic Kernel.
- **Program**: Demonstrates how to set up and use the filtering capabilities.

## Example Queries

- "Show me all production servers"
- "What database servers are running in East US?"
- "List servers related to the TAMI application"
- "Find all stopped servers in the HR department"
- "Show me web servers in staging environments"
