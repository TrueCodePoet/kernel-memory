// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.DataFormats;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.Pipeline;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular.DataFormats;

/// <summary>
/// Decoder for Excel files that preserves tabular structure.
/// </summary>
[Experimental("KMEXP00")]
public sealed class TabularExcelDecoder : IContentDecoder
{
    private readonly TabularExcelDecoderConfig _config;
    private readonly ILogger<TabularExcelDecoder> _log;
    private static readonly Regex s_invalidCharsRegex = new(@"[^\w\d]", RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="TabularExcelDecoder"/> class.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public TabularExcelDecoder(TabularExcelDecoderConfig? config = null, ILoggerFactory? loggerFactory = null)
    {
        this._config = config ?? new TabularExcelDecoderConfig();
        this._log = (loggerFactory ?? DefaultLogger.Factory).CreateLogger<TabularExcelDecoder>();
    }

    /// <inheritdoc />
    public bool SupportsMimeType(string mimeType)
    {
        return mimeType != null && mimeType.StartsWith(MimeTypes.MsExcelX, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<FileContent> DecodeAsync(string filename, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(filename);
        return this.DecodeAsync(stream, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FileContent> DecodeAsync(BinaryData data, CancellationToken cancellationToken = default)
    {
        using var stream = data.ToStream();
        return this.DecodeAsync(stream, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FileContent> DecodeAsync(Stream data, CancellationToken cancellationToken = default)
    {
        this._log.LogDebug("Extracting tabular data from MS Excel file");

        var result = new FileContent(MimeTypes.PlainText);
        using var workbook = new XLWorkbook(data);

        var chunkNumber = 0;
        foreach (var worksheet in workbook.Worksheets)
        {
            // Skip worksheet if not in the list of worksheets to process
            if (!this._config.ProcessAllWorksheets &&
                !this._config.WorksheetsToProcess.Contains(worksheet.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var worksheetName = worksheet.Name;
            this._log.LogDebug("Processing worksheet: {WorksheetName}", worksheetName);

            var rangeUsed = worksheet.RangeUsed();
            if (rangeUsed == null)
            {
                this._log.LogDebug("Worksheet {WorksheetName} is empty", worksheetName);
                continue;
            }

            // Get headers from the specified row
            var headers = new List<string>();
            var headerRow = rangeUsed.Row(this._config.HeaderRowIndex + 1);

            if (this._config.UseFirstRowAsHeader && headerRow != null)
            {
                foreach (var cell in headerRow.CellsUsed())
                {
                    var headerText = cell.Value.ToString() ?? string.Empty;

                    // Normalize header name if configured
                    if (this._config.NormalizeHeaderNames)
                    {
                        headerText = NormalizeHeaderName(headerText);
                    }

                    // If header is empty, use default column prefix with column number
                    if (string.IsNullOrWhiteSpace(headerText))
                    {
                        headerText = $"{this._config.DefaultColumnPrefix}{cell.Address.ColumnNumber}";
                    }

                    headers.Add(headerText);
                }
            }

            // Process data rows
            var rowsUsed = rangeUsed.RowsUsed();
            if (rowsUsed == null)
            {
                continue;
            }

            // Skip the header row if using first row as header
            var startRow = this._config.UseFirstRowAsHeader ? this._config.HeaderRowIndex + 1 : 0;

            foreach (var row in rowsUsed.Skip(startRow))
            {
                // Skip row if configured to skip empty or hidden rows
                if ((this._config.SkipEmptyRows && !row.CellsUsed().Any()) ||
                    (this._config.SkipHiddenRows && worksheet.Row(row.RowNumber()).IsHidden))
                {
                    continue;
                }

                var rowNumber = row.RowNumber();
                var cells = row.Cells().ToList();

                // Create a dictionary to hold the row data
                var rowData = new Dictionary<string, object>();

                // Add metadata if configured
                if (this._config.IncludeWorksheetNames)
                {
                    rowData["_worksheet"] = worksheetName;
                }

                if (this._config.IncludeRowNumbers)
                {
                    rowData["_rowNumber"] = rowNumber;
                }

                // Process each cell in the row
                for (var i = 0; i < cells.Count; i++)
                {
                    var cell = cells[i];

                    // Skip hidden columns if configured
                    if (this._config.SkipHiddenColumns && worksheet.Column(cell.Address.ColumnNumber).IsHidden)
                    {
                        continue;
                    }

                    // Get the column name (from headers or generate one)
                    string columnName;
                    if (this._config.UseFirstRowAsHeader && i < headers.Count)
                    {
                        columnName = headers[i];
                    }
                    else
                    {
                        columnName = $"{this._config.DefaultColumnPrefix}{cell.Address.ColumnNumber}";
                    }

                    // Extract the cell value based on its type
                    object cellValue = this.ExtractCellValue(cell);

                    // Add to row data
                    rowData[columnName] = cellValue;
                }

                // Create a chunk for this row
                chunkNumber++;
                var metadata = new Dictionary<string, string>
                {
                    ["worksheetName"] = worksheetName,
                    ["rowNumber"] = rowNumber.ToString(),
                    ["tabularData"] = JsonSerializer.Serialize(rowData)
                };

                // Create a text representation for the chunk content
                var sb = new StringBuilder();
                sb.AppendLine($"Worksheet: {worksheetName}, Row: {rowNumber}");
                foreach (var kvp in rowData)
                {
                    sb.AppendLine($"{kvp.Key}: {kvp.Value}");
                }

                result.Sections.Add(new Chunk(sb.ToString(), chunkNumber, metadata));
            }
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Extracts the value from a cell based on its type.
    /// </summary>
    /// <param name="cell">The cell to extract the value from.</param>
    /// <returns>The extracted value.</returns>
    private object ExtractCellValue(IXLCell cell)
    {
        if (cell == null || cell.Value.IsBlank)
        {
            return this._config.BlankCellValue;
        }

        if (this._config.PreserveDataTypes)
        {
            if (cell.Value.IsBoolean)
            {
                return cell.Value.GetBoolean();
            }
            else if (cell.Value.IsDateTime)
            {
                var dateTime = cell.Value.GetDateTime();
                return dateTime.ToString(this._config.DateFormat, this._config.DateFormatProvider);
            }
            else if (cell.Value.IsTimeSpan)
            {
                var timeSpan = cell.Value.GetTimeSpan();
                return timeSpan.ToString(this._config.TimeFormat, this._config.TimeFormatProvider);
            }
            else if (cell.Value.IsNumber)
            {
                return cell.Value.GetNumber();
            }
            else if (cell.Value.IsText)
            {
                return cell.Value.GetText();
            }
            else if (cell.Value.IsError)
            {
                return cell.Value.GetError().ToString();
            }
            else
            {
                return cell.Value.ToString() ?? string.Empty;
            }
        }
        else
        {
            // Convert everything to string
            return cell.Value.ToString() ?? string.Empty;
        }
    }

    /// <summary>
    /// Normalizes a header name by replacing invalid characters with underscores.
    /// </summary>
    /// <param name="headerName">The header name to normalize.</param>
    /// <returns>The normalized header name.</returns>
    private static string NormalizeHeaderName(string headerName)
    {
        if (string.IsNullOrEmpty(headerName))
        {
            return headerName;
        }

        // Replace invalid characters with underscores
        var normalized = s_invalidCharsRegex.Replace(headerName, "_");

        // Remove consecutive underscores
        while (normalized.Contains("__"))
        {
            normalized = normalized.Replace("__", "_");
        }

        // Trim underscores from start and end
        normalized = normalized.Trim('_');

        return normalized;
    }
}
