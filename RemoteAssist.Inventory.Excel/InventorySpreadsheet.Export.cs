using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace RemoteAssist.Inventory.Excel;

public static partial class InventorySpreadsheet
{
    private static readonly string[] RegistryHeaders =
    [
        "ID объекта", "Корпус", "Этаж", "Кабинет", "Место", "Тип", "Модель", "Инвентарный номер",
        "Серийный номер", "Имя ПК", "MAC", "Состояние", "Описание", "Примечания", "Картриджи", "Фото", "В архиве"
    ];
    private static readonly double[] RegistryWidths = [35, 10, 8, 14, 10, 19, 29, 22, 22, 20, 21, 16, 38, 38, 30, 8, 12];

    public static void ExportRegistry(string path, IEnumerable<AssetDto> filteredAssets, InventorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(filteredAssets);
        ArgumentNullException.ThrowIfNull(snapshot);
        var rooms = snapshot.Rooms.ToDictionary(x => x.Id);
        var photos = snapshot.Photos.Where(x => x.OwnerType == "asset").GroupBy(x => x.OwnerId).ToDictionary(x => x.Key, x => x.Count());
        var cartridges = snapshot.Cartridges.Where(x => x.PrinterId is not null).GroupBy(x => x.PrinterId!)
            .ToDictionary(x => x.Key, x => string.Join("; ", x.Select(c => $"{c.Slot}: {c.Number} ({c.Model}, {c.Color})")));
        var rows = filteredAssets.Select(asset =>
        {
            var room = asset.RoomId is null ? null : rooms.GetValueOrDefault(asset.RoomId);
            return AssetCells(asset, room?.Building ?? "", room?.Floor, room?.Number ?? "", photos.GetValueOrDefault(asset.Id), cartridges.GetValueOrDefault(asset.Id, ""));
        }).ToList();
        WriteWorkbook(path, "Реестр", RegistryHeaders, RegistryWidths, rows, []);
    }

    /// <summary>Uses only audit snapshot fields. Subsequent asset, room, photo, or cartridge edits cannot change the report.</summary>
    public static void ExportAudit(string path, AuditDto audit, InventorySnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(audit);
        var headers = RegistryHeaders.Concat(new[]
        {
            "Ожидаемый кабинет", "Фактический кабинет", "Результат проверки", "Комментарий проверки", "Дата проверки", "Проверяющий"
        }).ToArray();
        var widths = RegistryWidths.Concat(new double[] { 20, 20, 30, 38, 22, 24 }).ToArray();
        var rows = audit.Items.Select(item =>
        {
            var expectedRoom = item.AddedAfterStart ? null : item.ExpectedRoomSnapshot;
            // A room's number does not define its floor. Legacy audits without the room snapshot keep the floor blank.
            var registry = AssetCells(item.AssetSnapshot, item.AddedAfterStart ? "" : expectedRoom?.Building ?? audit.Building,
                expectedRoom?.Floor, item.AddedAfterStart ? "" : expectedRoom?.Number ?? item.ExpectedRoomNumber,
                item.PhotoCount, item.InstalledCartridges);
            if (item.AddedAfterStart) registry[4] = ""; // No expected workstation existed at the start of the audit.
            return registry.Concat(new object?[] { item.ExpectedRoomNumber, item.ActualRoomNumber, item.Result, item.Comment, item.CheckedUtc?.LocalDateTime, item.CheckedBy }).ToArray();
        }).ToList();
        WriteWorkbook(path, "Инвентаризация", headers, widths, rows,
        [
            $"Инвентаризация: {audit.Name}",
            $"Дата: {audit.Date:dd.MM.yyyy}. Корпус: {audit.Building}. " + (audit.CompletedUtc.HasValue
                ? $"Завершена: {audit.CompletedUtc.Value.LocalDateTime:dd.MM.yyyy HH:mm}."
                : "Проверка не завершена. Промежуточные результаты.")
        ]);
    }

    private static object?[] AssetCells(AssetDto asset, string building, int? floor, string room, int photoCount, string cartridges) =>
    [
        asset.Id, building, floor, room, asset.Seat, asset.Type, asset.Model, asset.InventoryNumber,
        asset.SerialNumber, asset.Hostname, asset.Mac, asset.Status, asset.Description, asset.Notes,
        cartridges, photoCount, asset.Archived ? "Да" : "Нет"
    ];

    private static void WriteWorkbook(string path, string sheetName, string[] headers, double[] widths,
        IReadOnlyCollection<object?[]> rows, IReadOnlyList<string> metadata)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new Workbook();
        var styles = workbook.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = CreateStyles();
        styles.Stylesheet.Save();
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        int headerRow = metadata.Count == 0 ? 1 : metadata.Count + 2;
        int lastRow = Math.Max(headerRow, headerRow + rows.Count);
        var view = new SheetView { WorkbookViewId = 0U, ShowGridLines = false };
        view.Append(new Pane { VerticalSplit = headerRow, TopLeftCell = $"A{headerRow + 1}", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen });
        view.Append(new Selection { Pane = PaneValues.BottomLeft, ActiveCell = $"A{headerRow + 1}", SequenceOfReferences = new ListValue<StringValue> { InnerText = $"A{headerRow + 1}" } });
        var columns = new Columns();
        for (int i = 0; i < widths.Length; i++) columns.Append(new Column { Min = (uint)(i + 1), Max = (uint)(i + 1), Width = widths[i], CustomWidth = true });
        worksheet.Worksheet = new Worksheet(new SheetProperties(new PageSetupProperties { FitToPage = false }),
            new SheetDimension { Reference = $"A1:{ColumnName(headers.Length)}{lastRow}" },
            new SheetViews(view), new SheetFormatProperties { DefaultRowHeight = 22 }, columns, data);
        for (int i = 0; i < metadata.Count; i++)
        {
            var row = new Row { RowIndex = (uint)i + 1, Height = 30, CustomHeight = true };
            row.Append(TextCell($"A{i + 1}", metadata[i], 4));
            data.Append(row);
        }
        var header = new Row { RowIndex = (uint)headerRow, Height = 42, CustomHeight = true };
        for (int i = 0; i < headers.Length; i++) header.Append(TextCell(ColumnName(i + 1) + headerRow, headers[i], 1));
        data.Append(header);
        var number = headerRow;
        foreach (var values in rows)
        {
            number++;
            var row = new Row { RowIndex = (uint)number };
            // Leave height automatic, so lengthy descriptions remain readable in Excel.
            for (int i = 0; i < headers.Length; i++)
            {
                var reference = ColumnName(i + 1) + number;
                var value = i < values.Length ? values[i] : null;
                uint style = number % 2 == 0 ? 2U : 3U;
                if (value is DateTime date)
                    row.Append(new Cell { CellReference = reference, DataType = CellValues.Number, CellValue = new CellValue(date.ToOADate().ToString(CultureInfo.InvariantCulture)), StyleIndex = 5 });
                else if (value is int count)
                    row.Append(new Cell { CellReference = reference, DataType = CellValues.Number, CellValue = new CellValue(count.ToString(CultureInfo.InvariantCulture)), StyleIndex = style });
                else row.Append(TextCell(reference, value?.ToString() ?? "", style));
            }
            data.Append(row);
        }
        worksheet.Worksheet.Append(new AutoFilter { Reference = $"A{headerRow}:{ColumnName(headers.Length)}{lastRow}" });
        if (metadata.Count > 0)
        {
            var merges = new MergeCells();
            for (int i = 1; i <= metadata.Count; i++) merges.Append(new MergeCell { Reference = $"A{i}:M{i}" });
            worksheet.Worksheet.Append(merges);
        }
        worksheet.Worksheet.Append(new PrintOptions { HorizontalCentered = false });
        worksheet.Worksheet.Append(new PageMargins { Left = .25, Right = .25, Top = .45, Bottom = .45, Header = .2, Footer = .2 });
        // Do not shrink 17–23 columns to one page. Print at readable size, repeat identifiers on horizontal pages.
        worksheet.Worksheet.Append(new PageSetup { PaperSize = 8U, Orientation = OrientationValues.Landscape, Scale = 90U });
        worksheet.Worksheet.Append(new HeaderFooter(new OddFooter { Text = "&L" + sheetName + "&RСтраница &P из &N" }));
        var sheets = workbook.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1U, Name = sheetName });
        var quoted = "'" + sheetName.Replace("'", "''") + "'";
        workbook.Workbook.Append(new DefinedNames(
            new DefinedName { Name = "_xlnm.Print_Titles", LocalSheetId = 0U, Text = $"{quoted}!${headerRow}:${headerRow},{quoted}!$A:$D" },
            new DefinedName { Name = "_xlnm.Print_Area", LocalSheetId = 0U, Text = $"{quoted}!$A$1:${ColumnName(headers.Length)}${lastRow}" }));
        worksheet.Worksheet.Save();
        workbook.Workbook.Save();
    }

    private static Cell TextCell(string reference, string value, uint style) => new()
    {
        CellReference = reference, DataType = CellValues.InlineString, StyleIndex = style,
        InlineString = new InlineString(new Text(XmlSafe(value)) { Space = SpaceProcessingModeValues.Preserve })
    };
    private static string XmlSafe(string text)
    {
        // Worksheet text has a 32,767 UTF-16 unit limit. Reject instead of silently truncating inventory notes.
        if (text.Length > 32_767) throw new InvalidDataException("Значение превышает ограничение Excel: 32 767 символов в ячейке.");
        return string.Concat(text.EnumerateRunes().Where(r => r.Value is 9 or 10 or 13 || r.Value >= 32 && r.Value != 0xFFFE && r.Value != 0xFFFF).Select(r => r.ToString()));
    }
    private static Stylesheet CreateStyles()
    {
        var fonts = new Fonts(
            new Font(new FontSize { Val = 11 }, new Color { Rgb = "FF1E293B" }, new FontName { Val = "Calibri" }),
            new Font(new Bold(), new FontSize { Val = 11 }, new Color { Rgb = "FFFFFFFF" }, new FontName { Val = "Calibri" }));
        fonts.Count = (uint)fonts.ChildElements.Count;
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FF214B70" }, new BackgroundColor { Indexed = 64U }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFEFF6FC" }, new BackgroundColor { Indexed = 64U }) { PatternType = PatternValues.Solid }));
        fills.Count = (uint)fills.ChildElements.Count;
        var borders = new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1 };
        var cellFormats = new CellFormats(
            new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 0 },
            Format(1, 2), Format(0, 0), Format(0, 3), Format(0, 0), Format(0, 0, 164));
        cellFormats.Count = (uint)cellFormats.ChildElements.Count;
        return new Stylesheet(new NumberingFormats(new NumberingFormat { NumberFormatId = 164U, FormatCode = "dd.mm.yyyy hh:mm" }) { Count = 1 },
            fonts, fills, borders, new CellStyleFormats(new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 0 }) { Count = 1 },
            cellFormats, new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 });
    }
    private static CellFormat Format(uint font, uint fill, uint number = 0) => new(new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Top })
    {
        FontId = font, FillId = fill, BorderId = 0, NumberFormatId = number, ApplyFont = true, ApplyFill = true, ApplyAlignment = true,
        ApplyNumberFormat = number != 0, FormatId = 0
    };
}
