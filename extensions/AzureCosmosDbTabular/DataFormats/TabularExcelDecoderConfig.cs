// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Microsoft.KernelMemory.MemoryDb.AzureCosmosDbTabular.DataFormats;

/// <summary>
/// Configuration options for the TabularExcelDecoder.
/// </summary>
public class TabularExcelDecoderConfig
{
    /// <summary>
    /// Gets or sets a value indicating whether to use the first row as header.
    /// </summary>
    public bool UseFirstRowAsHeader { get; set; } = true;

    /// <summary>
    /// Gets or sets the header row index (0-based).
    /// </summary>
    public int HeaderRowIndex { get; set; } = 0;

    /// <summary>
    /// Gets or sets a value indicating whether to process all worksheets.
    /// </summary>
    public bool ProcessAllWorksheets { get; set; } = true;

    /// <summary>
    /// Gets or sets the list of worksheets to process.
    /// Only used when ProcessAllWorksheets is false.
    /// </summary>
    public List<string> WorksheetsToProcess { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether to preserve data types.
    /// </summary>
    public bool PreserveDataTypes { get; set; } = true;

    /// <summary>
    /// Gets or sets the value to use for blank cells.
    /// </summary>
    public string BlankCellValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the date format to use when converting dates to strings.
    /// </summary>
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>
    /// Gets or sets the format provider to use when formatting dates.
    /// </summary>
    public IFormatProvider DateFormatProvider { get; set; } = CultureInfo.InvariantCulture;

    /// <summary>
    /// Gets or sets the time format to use when converting times to strings.
    /// Use standard TimeSpan format specifiers (e.g., "c", "g", "G") or custom formats (e.g., @"hh\:mm\:ss").
    /// See: https://learn.microsoft.com/en-us/dotnet/standard/base-types/standard-timespan-format-strings
    /// </summary>
    public string TimeFormat { get; set; } = "c"; // Use constant format specifier "c" as a robust default

    /// <summary>
    /// Gets or sets the format provider to use when formatting times.
    /// </summary>
    public IFormatProvider TimeFormatProvider { get; set; } = CultureInfo.InvariantCulture;

    /// <summary>
    /// Gets or sets a value indicating whether to normalize header names.
    /// </summary>
    public bool NormalizeHeaderNames { get; set; } = true;

    /// <summary>
    /// Gets or sets the prefix to use for columns without headers.
    /// </summary>
    public string DefaultColumnPrefix { get; set; } = "Column";

    /// <summary>
    /// Gets or sets a value indicating whether to include row numbers in the output.
    /// </summary>
    public bool IncludeRowNumbers { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to include worksheet names in the output.
    /// </summary>
    public bool IncludeWorksheetNames { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to skip empty rows.
    /// </summary>
    public bool SkipEmptyRows { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to skip hidden rows.
    /// </summary>
    public bool SkipHiddenRows { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to skip hidden columns.
    /// </summary>
    public bool SkipHiddenColumns { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to include formulas in the output.
    /// </summary>
    public bool IncludeFormulas { get; set; } = false;
}
