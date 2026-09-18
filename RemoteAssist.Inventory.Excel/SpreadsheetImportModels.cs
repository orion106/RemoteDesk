namespace RemoteAssist.Inventory.Excel;

public enum SpreadsheetRowAction { Add, Update, Skip }

/// <summary>Letters are editable in the preview dialog. The supplied inventory has no header row.</summary>
public sealed class SpreadsheetImportOptions
{
    public string BuildingColumn { get; set; } = "A";
    public string RoomColumn { get; set; } = "B";
    public string HostnameColumn { get; set; } = "C";
    public string TypeColumn { get; set; } = "D";
    public string ModelColumn { get; set; } = "E";
    public string InventoryNumberColumn { get; set; } = "F";
    public string DescriptionColumn { get; set; } = "G";
    public string NotesColumn { get; set; } = "H";
    public string MacColumn { get; set; } = "I";
    public string? AssetIdColumn { get; set; }
    public int FirstDataRow { get; set; } = 1;
    public string? SheetName { get; set; }
    public string? SourceId { get; set; }
}

public sealed class SpreadsheetPreview
{
    public string FileName { get; init; } = "";
    public string FileHash { get; init; } = "";
    public string SourceId { get; init; } = "";
    public bool PreviouslyImported { get; internal set; }
    public List<SpreadsheetPreviewRow> Rows { get; } = [];
    public List<string> SheetNames { get; } = [];
    public int AddCount => Rows.Count(x => x.Action == SpreadsheetRowAction.Add);
    public int UpdateCount => Rows.Count(x => x.Action == SpreadsheetRowAction.Update);
    public int SkipCount => Rows.Count(x => x.Action == SpreadsheetRowAction.Skip);
    public int WarningCount => Rows.Count(x => x.Issues.Count > 0);
}

/// <summary>Editable proposal, never a live object from the shared inventory.</summary>
public sealed class SpreadsheetPreviewRow
{
    public string Sheet { get; init; } = "";
    public int RowNumber { get; init; }
    public string Building { get; set; } = "";
    public string Room { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Type { get; set; } = "";
    public string Model { get; set; } = "";
    public string InventoryNumber { get; set; } = "";
    public string Description { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Mac { get; set; } = "";
    public string? TargetAssetId { get; set; }
    public long TargetVersion { get; set; }
    public SpreadsheetRowAction Action { get; set; } = SpreadsheetRowAction.Add;
    public Dictionary<string, string> RawCells { get; init; } = [];
    public Dictionary<string, string> RawFormulas { get; init; } = [];
    public List<string> Issues { get; } = [];
    public string IssuesText => string.Join("; ", Issues);
    /// <summary>The original source row signature is independent of edits made in the preview.</summary>
    public string RowSignature { get; internal set; } = "";
    public string ImportKey { get; internal set; } = "";
    /// <summary>Updates retain existing placement unless the user explicitly requests a move.</summary>
    public bool ApplyLocationChange { get; set; }
}
