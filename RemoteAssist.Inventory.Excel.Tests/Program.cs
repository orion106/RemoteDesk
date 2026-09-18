using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using RemoteAssist.Inventory;
using RemoteAssist.Inventory.Excel;

var root = Path.Combine(Path.GetTempPath(), "RemoteAssist-Excel-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
void Throws(Action action, string message)
{
    try { action(); } catch (InvalidDataException) { checks++; return; }
    throw new Exception(message);
}
var snapshot = new InventorySnapshot
{
    Rooms = [new RoomDto { Id = "r201", Building = "Д", Number = "201Д", Floor = 2 }, new RoomDto { Id = "r307", Building = "Д", Number = "307аД", Floor = 3 }]
};
var source = Path.Combine(root, "inventory.xlsx");
WriteSource(source,
[
    ["Д", "201", "PC01", "Компьютер", "Model A", "00017", "CPU", "Initial note", "00:01:02:03:04:05", "DERIVED", "#VALUE!"],
    ["Д", "201", "PC01", "Монитор", "Panel", "00017", "Display", "", "", "IGNORE", ""],
    ["Д", "307a", "", "Принтер", "Laser", "007", "", "", "", "", ""],
    ["Д", "201", "", "", "", "00099", "", "", "", "", ""]
]);
var preview = InventorySpreadsheet.ReadPreview(source, snapshot);
Check(preview.Rows.Count == 4, "First row must not be interpreted as a header.");
Check(preview.Rows[0].InventoryNumber == "00017", "Text inventory identifiers retain leading zeros.");
Check(preview.Rows[0].RawCells["J"] == "DERIVED" && preview.Rows[0].RawCells["K"] == "#VALUE!", "All source columns must be preserved.");
Check(preview.Rows.Take(2).All(x => x.IssuesText.Contains("комплекта")), "Shared inventory numbers are flagged, not merged.");
Check(preview.Rows[3].Action == SpreadsheetRowAction.Skip, "Partial inventory-only row must remain for review and default to skipped.");
Check(preview.Rows[2].IssuesText.Contains("латинские"), "Latin room suffix must be visible as ambiguous.");
Check(InventorySpreadsheet.RoomKey("Д", "307аД") != InventorySpreadsheet.RoomKey("Д", "307a"), "Cyrillic and Latin suffixes must not merge.");
Check(InventorySpreadsheet.RoomKey("Д", "201") == InventorySpreadsheet.RoomKey("Д", "201Д"), "Known building suffix should match floor map.");
Check(InventorySpreadsheet.RoomKey("Д", "201") != InventorySpreadsheet.RoomKey("Д", "202"), "Different rooms remain different.");
var command = InventorySpreadsheet.BuildImportCommand(preview, snapshot);
Check(command.Assets.Count == 3, "Each complete input row produces a separate asset.");
Check(command.Assets[0].Id != command.Assets[1].Id, "Kit members have separate IDs.");
Check(command.Assets[0].RoomId == "r201", "Map room is matched precisely.");
Check(command.Rooms.Count == 1 && command.Rooms[0].Number == "307a" && command.Rooms[0].MapKey is null, "Unknown rooms get independent manual rooms.");
Check(command.Assets.All(x => x.X is null && x.Y is null), "New assets begin unplaced.");
Check(command.Assets[0].Source?.RowNumber == 1 && command.Assets[0].Source?.RawCells["K"] == "#VALUE!", "Source provenance remains complete.");
foreach (var asset in command.Assets) asset.Version = 3;
snapshot.Assets = command.Assets;
snapshot.Rooms.AddRange(command.Rooms);
var repeat = InventorySpreadsheet.ReadPreview(source, snapshot);
Check(repeat.Rows.All(x => x.Action == SpreadsheetRowAction.Skip), "Exact same source never adds duplicates.");
snapshot.ImportedFileHashes.Add(preview.FileHash);
snapshot.Assets = [];
var known = InventorySpreadsheet.ReadPreview(source, snapshot);
Check(known.PreviouslyImported && known.Rows.All(x => x.Action == SpreadsheetRowAction.Skip), "Import ledger recognizes old files even after assets were changed.");
snapshot.ImportedFileHashes.Clear();
snapshot.Assets = command.Assets;
snapshot.Assets[0].RoomId = "r307";
snapshot.Assets[0].Seat = "12";
snapshot.Assets[0].X = .3;
snapshot.Assets[0].Y = .4;
snapshot.Assets[0].SerialNumber = "SN0008";
WriteSource(source,
[
    ["Д", "201", "PC01", "Монитор", "Panel", "00017", "Display", "", ""],
    ["Д", "201", "PC01", "Компьютер", "Model A", "00017", "CPU", "Changed note", "00:01:02:03:04:05"],
    ["Д", "201", "", "Монитор", "DifferentPanel", "00017", "", "", ""]
]);
var changed = InventorySpreadsheet.ReadPreview(source, snapshot);
Check(changed.Rows[0].Action == SpreadsheetRowAction.Skip, "Unchanged source row remains skipped after sort.");
Check(changed.Rows[1].Action == SpreadsheetRowAction.Update && changed.Rows[1].TargetAssetId == snapshot.Assets[0].Id, "Changed row gets cautious stable identity match.");
Check(changed.Rows[2].Action == SpreadsheetRowAction.Skip && changed.Rows[2].TargetAssetId is null, "Inventory number alone cannot match equipment.");
var update = InventorySpreadsheet.BuildImportCommand(changed, snapshot).Assets.Single();
Check(update.Notes == "Changed note" && update.RoomId == "r307" && update.Seat == "12" && update.X == .3 && update.SerialNumber == "SN0008", "Reimport changes source fields but retains manual placement and serial.");
changed.Rows[1].ApplyLocationChange = true;
update = InventorySpreadsheet.BuildImportCommand(changed, snapshot).Assets.Single();
Check(update.RoomId == "r201" && update.X is null && update.Seat == "", "Explicit movement clears stale placement.");
snapshot.Assets[0].Version = 99;
Check(InventorySpreadsheet.BuildImportCommand(changed, snapshot).Assets.Single().Version == 3, "Preview version must not silently refresh after concurrent edit.");
changed.Rows[0].Action = SpreadsheetRowAction.Update;
changed.Rows[0].TargetAssetId = changed.Rows[1].TargetAssetId;
Throws(() => InventorySpreadsheet.BuildImportCommand(changed, snapshot), "Duplicate target IDs must fail before sending.");

var leading = Path.Combine(root, "leading.xlsx");
WriteSource(leading, [["Д", "201", "", "Монитор", "Panel", "12", "", "", ""]], numericInventory: true);
Check(InventorySpreadsheet.ReadPreview(leading, snapshot).Rows[0].InventoryNumber == "000012", "Numeric identifier must honor zero-pad display mask.");
var mapped = Path.Combine(root, "mapped.xlsx");
WriteSource(mapped,
[
    ["Корпус", "Кабинет", "Тип", "Имя ПК", "Модель", "Номер", "Описание", "Примечание", "MAC"],
    ["Д", "201", "Монитор", "PC2", "Panel2", "09", "", "", ""]
]);
var mapping = InventorySpreadsheet.ReadPreview(mapped, snapshot, new SpreadsheetImportOptions { FirstDataRow = 2, TypeColumn = "C", HostnameColumn = "D", SourceId = "custom-source" });
Check(mapping.Rows.Count == 1 && mapping.Rows[0].Type == "Монитор" && mapping.Rows[0].Hostname == "PC2" && mapping.SourceId == "custom-source", "Column mapping and explicit header skip work.");

var registry = Path.Combine(root, "registry.xlsx");
snapshot.Photos = [new PhotoDto { OwnerId = snapshot.Assets[0].Id, OwnerType = "asset" }];
snapshot.Cartridges = [new CartridgeDto { PrinterId = snapshot.Assets[0].Id, Number = "0003", Model = "85A", Slot = "Black", Color = "Чёрный" }];
InventorySpreadsheet.ExportRegistry(registry, snapshot.Assets.Take(1), snapshot);
using (var doc = SpreadsheetDocument.Open(registry, false))
{
    var errors = new OpenXmlValidator().Validate(doc).ToList();
    Check(errors.Count == 0, "Registry export validates: " + string.Join("; ", errors.Select(x => x.Description)));
    var ws = doc.WorkbookPart!.WorksheetParts.Single().Worksheet!;
    Check(ws.Descendants<Row>().Count() == 2, "Registry exports only filtered assets.");
    var inventory = ws.Descendants<Cell>().Single(x => x.CellReference == "H2");
    Check(inventory.DataType?.Value == CellValues.InlineString && inventory.InnerText == "00017", "Export identifiers are text.");
    Check(ws.Descendants<AutoFilter>().Any() && ws.Descendants<Pane>().Single().State?.Value == PaneStateValues.Frozen, "Export has filters and frozen headers.");
    Check(ws.Descendants<Cell>().Single(x => x.CellReference == "P2").InnerText == "1", "Photo count is exported.");
    Check(ws.Descendants<Cell>().Single(x => x.CellReference == "O2").InnerText.Contains("0003"), "Installed cartridge number is exported.");
    Check(doc.WorkbookPart.Workbook!.Descendants<DefinedName>().Count() == 2, "Print titles and print area exist.");
}
var audit = new AuditDto
{
    Name = "Проверка", Building = "Д", CompletedUtc = DateTimeOffset.UtcNow,
    Items = [new AuditItemDto
    {
        AssetId = "gone", AssetSnapshot = new AssetDto { Id = "gone", Type = "Принтер", Model = "Frozen model", InventoryNumber = "00009" },
        ExpectedRoomId = "r201", ExpectedRoomNumber = "201Д", ActualRoomNumber = "307аД", Result = "Обнаружено в другом кабинете", CheckedBy = "Inspector",
        CheckedUtc = DateTimeOffset.UtcNow, PhotoCount = 7, InstalledCartridges = "Frozen cartridge 007"
    }, new AuditItemDto
    {
        AssetId = "discovered", AssetSnapshot = new AssetDto { Id = "discovered", Type = "Монитор", Model = "Newly found" },
        AddedAfterStart = true, ExpectedRoomId = null, ExpectedRoomNumber = "Не учтено на начало проверки", ActualRoomNumber = "201Д", Result = "Найдено"
    }, new AuditItemDto
    {
        AssetId = "named", AssetSnapshot = new AssetDto { Id = "named", Type = "Проектор" },
        ExpectedRoomId = "rhall", ExpectedRoomNumber = "Актовый зал (М)",
        ExpectedRoomSnapshot = new RoomDto { Id = "rhall", Building = "М", Number = "Актовый зал", Floor = 5 }
    }, new AuditItemDto
    {
        AssetId = "mismatch", AssetSnapshot = new AssetDto { Id = "mismatch", Type = "Принтер" },
        ExpectedRoomId = "r999", ExpectedRoomNumber = "999 (Л)",
        ExpectedRoomSnapshot = new RoomDto { Id = "r999", Building = "Л", Number = "999", Floor = 2 }
    }]
};
var auditPath = Path.Combine(root, "audit.xlsx");
InventorySpreadsheet.ExportAudit(auditPath, audit, new InventorySnapshot
{
    Rooms = [new RoomDto { Id = "rhall", Number = "Переназначен", Building = "Изменён", Floor = 8 }]
});
using (var doc = SpreadsheetDocument.Open(auditPath, false))
{
    var errors = new OpenXmlValidator().Validate(doc).ToList();
    Check(errors.Count == 0, "Audit export validates: " + string.Join("; ", errors.Select(x => x.Description)));
    var text = doc.WorkbookPart!.WorksheetParts.Single().Worksheet!.InnerText;
    Check(text.Contains("Frozen model") && text.Contains("Frozen cartridge 007") && text.Contains("Inspector") && text.Contains("307аД"), "Audit export is entirely based on frozen audit snapshot.");
    string At(string reference) => doc.WorkbookPart.WorksheetParts.Single().Worksheet!.Descendants<Cell>().Single(x => x.CellReference == reference).InnerText;
    Check(At("C5") == "", "Legacy audit without captured room must not infer floor from its number.");
    Check(new[] { "B6", "C6", "D6", "E6" }.All(x => At(x) == "") && At("R6") == "Не учтено на начало проверки", "Newly discovered equipment has no invented expected location and retains the explanatory caption.");
    Check(At("B7") == "М" && At("C7") == "5" && At("D7") == "Актовый зал", "Named room uses its captured building, floor and number despite live room changes.");
    Check(At("C8") == "2" && At("D8") == "999", "Numbering and floor mismatch uses the captured floor, never a guessed digit.");
}
// The user file is optional test input and is never copied into project or release artifacts.
if (args.Length > 0)
{
    var real = InventorySpreadsheet.ReadPreview(args[0], new InventorySnapshot());
    Check(real.Rows[0].RowNumber == 1 && real.Rows[0].Type.Length > 0, "User workbook first row is equipment.");
    Check(real.Rows.Any(x => x.Type.Length == 0 && x.InventoryNumber.Length > 0), "User workbook incomplete inventory-only row retained.");
    Check(real.Rows.Any(x => x.RawCells.ContainsKey("J") && x.RawCells.ContainsKey("K")), "User workbook J/K preserved.");
    Check(real.Rows.GroupBy(x => x.InventoryNumber).Any(g => g.Key.Length > 0 && g.Count() > 1), "User workbook kit inventory numbers preserved.");
    Check(real.Rows.Count == 798, "User workbook: expected all 798 non-empty rows.");
    Check(real.Rows.Count(x => x.Type.Length > 0) == 792, "User workbook: expected 792 type-filled rows.");
    var realCommand = InventorySpreadsheet.BuildImportCommand(real, new InventorySnapshot());
    Check(realCommand.Assets.Count == 792, "User workbook: all complete rows can be committed after preview without lost kit members.");
    Console.WriteLine($"User workbook validated: {real.Rows.Count} rows, {real.AddCount} proposals, {real.WarningCount} rows flagged for review.");
}
Console.WriteLine($"PASS: {checks} spreadsheet checks. Test outputs: {root}");

static void WriteSource(string path, string[][] rows, bool numericInventory = false)
{
    using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
    var wb = doc.AddWorkbookPart(); wb.Workbook = new Workbook();
    if (numericInventory)
    {
        var styles = wb.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = new Stylesheet(new NumberingFormats(new NumberingFormat { NumberFormatId = 164, FormatCode = "000000" }),
            new CellFormats(new CellFormat { NumberFormatId = 164, ApplyNumberFormat = true }));
        styles.Stylesheet.Save();
    }
    var part = wb.AddNewPart<WorksheetPart>(); var data = new SheetData(); part.Worksheet = new Worksheet(data);
    for (int i = 0; i < rows.Length; i++)
    {
        var row = new Row { RowIndex = (uint)i + 1 };
        for (int j = 0; j < rows[i].Length; j++)
        {
            var reference = ((char)('A' + j)).ToString() + (i + 1);
            if (numericInventory && j == 5) row.Append(new Cell { CellReference = reference, DataType = CellValues.Number, StyleIndex = 0, CellValue = new CellValue(rows[i][j]) });
            else row.Append(new Cell { CellReference = reference, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(rows[i][j])) });
        }
        data.Append(row);
    }
    wb.Workbook.Append(new Sheets(new Sheet { Id = wb.GetIdOfPart(part), SheetId = 1, Name = "Лист1" }));
    part.Worksheet.Save(); wb.Workbook.Save();
}
