using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace RemoteAssist.Inventory.Excel;

public static partial class InventorySpreadsheet
{
    private const int MaxRows = 100_000;
    private const long MaxFileBytes = 100 * 1024 * 1024;

    public static SpreadsheetPreview ReadPreview(string path, InventorySnapshot snapshot, SpreadsheetImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new();
        if (options.FirstDataRow < 1) throw new ArgumentException("Номер первой строки должен быть больше нуля.");
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException("Размер XLSX превышает 100 МБ.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var fileHash = Convert.ToHexString(SHA256.HashData(file));
        file.Position = 0;
        var preview = new SpreadsheetPreview
        {
            FileName = Path.GetFileName(path), FileHash = fileHash,
            SourceId = string.IsNullOrWhiteSpace(options.SourceId) ? Path.GetFileNameWithoutExtension(path) : options.SourceId.Trim()
        };
        var columns = new[] { options.BuildingColumn, options.RoomColumn, options.HostnameColumn, options.TypeColumn,
            options.ModelColumn, options.InventoryNumberColumn, options.DescriptionColumn, options.NotesColumn, options.MacColumn };
        foreach (var column in columns.Concat(string.IsNullOrWhiteSpace(options.AssetIdColumn) ? [] : new[] { options.AssetIdColumn }))
            if (!Regex.IsMatch(column.Trim(), "^[A-Za-z]{1,3}$", RegexOptions.CultureInvariant))
                throw new ArgumentException($"Неверный столбец «{column}». Используйте буквы Excel, например A или AB.");

        using var doc = SpreadsheetDocument.Open(file, false, new OpenSettings { MaxCharactersInPart = 50_000_000 });
        var workbook = doc.WorkbookPart ?? throw new InvalidDataException("В файле отсутствует книга Excel.");
        var strings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];
        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            var sheetName = sheet.Name?.Value ?? "Лист";
            preview.SheetNames.Add(sheetName);
            if (options.SheetName is not null && options.SheetName != sheetName) continue;
            if (sheet.Id is null || workbook.GetPartById(sheet.Id!) is not WorksheetPart worksheet) continue;
            var rawRows = worksheet.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>() ?? [];
            var fallbackRow = 0;
            foreach (var rawRow in rawRows)
            {
                fallbackRow++;
                int number = checked((int)(rawRow.RowIndex?.Value ?? (uint)fallbackRow));
                if (number < options.FirstDataRow) continue;
                var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var formulas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var errors = new List<string>();
                var fallbackColumn = 0;
                foreach (var cell in rawRow.Elements<Cell>())
                {
                    fallbackColumn++;
                    var column = Regex.Match(cell.CellReference?.Value ?? "", "^[A-Za-z]+").Value.ToUpperInvariant();
                    if (column.Length == 0) column = ColumnName(fallbackColumn);
                    cells[column] = ReadCell(cell, strings, workbook.WorkbookStylesPart?.Stylesheet);
                    if (cell.CellFormula is not null) formulas[column] = cell.CellFormula.Text;
                    if (cell.DataType?.Value == CellValues.Error) errors.Add($"{column}{number}: {cells[column]}");
                }
                if (!cells.Values.Any(x => !string.IsNullOrWhiteSpace(x))) continue;
                if (preview.Rows.Count >= MaxRows) throw new InvalidDataException("В файле больше 100 000 непустых строк.");
                string At(string? column) => column is null ? "" : cells.GetValueOrDefault(column.Trim().ToUpperInvariant(), "").Trim();
                var row = new SpreadsheetPreviewRow
                {
                    Sheet = sheetName, RowNumber = number, RawCells = cells, RawFormulas = formulas,
                    Building = At(options.BuildingColumn), Room = At(options.RoomColumn), Hostname = At(options.HostnameColumn),
                    Type = At(options.TypeColumn), Model = At(options.ModelColumn), InventoryNumber = At(options.InventoryNumberColumn),
                    Description = At(options.DescriptionColumn), Notes = At(options.NotesColumn), Mac = At(options.MacColumn)
                };
                row.RowSignature = Signature(row);
                row.ImportKey = Hash(JsonSerializer.Serialize(new[] { preview.SourceId, sheetName, number.ToString(CultureInfo.InvariantCulture) }));
                if (errors.Count > 0) row.Issues.Add("В исходной строке ошибки Excel: " + string.Join(", ", errors));
                if (formulas.Count > 0) row.Issues.Add("Формулы прочитаны по сохранённым значениям Excel.");
                ValidateRow(row, snapshot.Rooms);
                ProposeMatch(row, At(options.AssetIdColumn), preview, snapshot.Assets);
                preview.Rows.Add(row);
            }
        }
        if (options.SheetName is not null && !preview.SheetNames.Contains(options.SheetName))
            throw new InvalidDataException($"Лист «{options.SheetName}» не найден.");
        foreach (var group in preview.Rows.Where(x => x.InventoryNumber.Length > 0).GroupBy(x => Normalize(x.InventoryNumber)).Where(x => x.Count() > 1))
            foreach (var row in group) row.Issues.Add("Повторяется инвентарный номер: компоненты комплекта остаются отдельными объектами.");
        // Duplicate source rows must never silently target the same shared object.
        foreach (var group in preview.Rows.Where(x => x.TargetAssetId is not null && x.Action == SpreadsheetRowAction.Update).GroupBy(x => x.TargetAssetId).Where(x => x.Count() > 1))
            foreach (var row in group)
            {
                row.Action = SpreadsheetRowAction.Skip;
                row.Issues.Add("Несколько строк соответствуют одному объекту. Выберите соответствие вручную.");
            }
        preview.PreviouslyImported = snapshot.ImportedFileHashes.Contains(fileHash, StringComparer.OrdinalIgnoreCase);
        if (preview.PreviouslyImported)
            foreach (var row in preview.Rows)
            {
                row.Action = SpreadsheetRowAction.Skip;
                row.Issues.Add("Этот файл уже импортирован.");
            }
        return preview;
    }

    public static ImportCommand BuildImportCommand(SpreadsheetPreview preview, InventorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(snapshot);
        var command = new ImportCommand { FileHash = preview.FileHash, FileName = preview.FileName };
        if (preview.PreviouslyImported) return command;
        var rooms = snapshot.Rooms.ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in preview.Rows.Where(x => x.Action != SpreadsheetRowAction.Skip))
        {
            if (string.IsNullOrWhiteSpace(row.Type)) throw new InvalidDataException($"Строка {row.RowNumber}: укажите тип оборудования или пропустите строку.");
            if (string.IsNullOrWhiteSpace(row.Building) != string.IsNullOrWhiteSpace(row.Room))
                throw new InvalidDataException($"Строка {row.RowNumber}: укажите и корпус, и кабинет, либо оставьте оба поля пустыми.");
            AssetDto asset;
            if (row.Action == SpreadsheetRowAction.Update)
            {
                var existing = snapshot.Assets.SingleOrDefault(x => x.Id == row.TargetAssetId)
                    ?? throw new InvalidDataException($"Строка {row.RowNumber}: выберите существующий объект для обновления.");
                asset = JsonSerializer.Deserialize<AssetDto>(JsonSerializer.Serialize(existing))!;
                // Keep the preview's version even if a refresh happened while the editor was open.
                asset.Version = row.TargetVersion;
            }
            else asset = new AssetDto();
            if (!used.Add(asset.Id)) throw new InvalidDataException("Две строки обновляют один объект. Исправьте соответствия перед импортом.");
            if (row.Action == SpreadsheetRowAction.Add || row.ApplyLocationChange)
            {
                var key = RoomKey(row.Building, row.Room);
                var matches = rooms.Where(x => RoomKey(x.Building, x.Number) == key).ToList();
                if (matches.Count > 1) throw new InvalidDataException($"Кабинет {row.Room}: несколько точных совпадений. Исправьте номер или корпус.");
                RoomDto? room = matches.SingleOrDefault();
                if (room is null && !string.IsNullOrWhiteSpace(row.Room))
                {
                    room = new RoomDto
                    {
                        Building = row.Building.Trim(), Number = row.Room.Trim(), Floor = InferFloor(row.Room),
                        Name = "", MapKey = null
                    };
                    rooms.Add(room);
                    command.Rooms.Add(room);
                }
                if (asset.RoomId != room?.Id) { asset.X = null; asset.Y = null; asset.Seat = ""; }
                asset.RoomId = room?.Id;
            }
            asset.Type = row.Type.Trim(); asset.Model = row.Model.Trim(); asset.InventoryNumber = row.InventoryNumber.Trim();
            asset.Hostname = row.Hostname.Trim(); asset.Mac = row.Mac.Trim(); asset.Description = row.Description.Trim(); asset.Notes = row.Notes.Trim();
            asset.Source = new ImportSourceDto
            {
                FileName = preview.FileName, FileHash = preview.FileHash, SourceId = preview.SourceId,
                Sheet = row.Sheet, RowNumber = row.RowNumber, RowSignature = row.RowSignature, ImportKey = row.ImportKey,
                RawCells = new(row.RawCells), RawFormulas = new(row.RawFormulas)
            };
            command.Assets.Add(asset);
        }
        return command;
    }

    private static void ValidateRow(SpreadsheetPreviewRow row, IReadOnlyCollection<RoomDto> rooms)
    {
        if (row.Type.Length == 0) { row.Issues.Add("Не указан тип оборудования. Строка пропущена до исправления."); row.Action = SpreadsheetRowAction.Skip; }
        if (row.Model.Length == 0) row.Issues.Add("Не указана модель.");
        if (row.InventoryNumber.Length == 0) row.Issues.Add("Не указан инвентарный номер.");
        if (row.Room.Length == 0 || row.Building.Length == 0) row.Issues.Add("Не указан корпус или кабинет. Проверьте размещение.");
        var exact = rooms.Where(x => RoomKey(x.Building, x.Number) == RoomKey(row.Building, row.Room)).ToList();
        if (row.Room.Length > 0 && exact.Count == 0) row.Issues.Add("Кабинет отсутствует на карте: будет создан отдельный кабинет.");
        if (exact.Count > 1) { row.Issues.Add("В реестре несколько кабинетов с таким номером. Требуется исправление."); row.Action = SpreadsheetRowAction.Skip; }
        if (Regex.IsMatch(row.Room, "[A-Za-z]")) row.Issues.Add("В номере кабинета латинские буквы. Проверьте суффикс: латиница и кириллица не объединяются.");
        if (row.Mac.Length > 0 && !Regex.IsMatch(Regex.Replace(row.Mac, "[-:.\\s]", ""), "^[0-9a-fA-F]{12}$")) row.Issues.Add("Проверьте формат MAC-адреса.");
    }

    private static void ProposeMatch(SpreadsheetPreviewRow row, string explicitId, SpreadsheetPreview preview, IReadOnlyCollection<AssetDto> existing)
    {
        var scope = existing.Where(x => x.Source?.SourceId == preview.SourceId && x.Source?.Sheet == row.Sheet).ToList();
        if (explicitId.Length > 0)
        {
            var byId = existing.SingleOrDefault(x => x.Id == explicitId);
            if (byId is not null) { Target(row, byId, SpreadsheetRowAction.Update); return; }
            row.Issues.Add("Указанный ID объекта не найден. Проверьте соответствие."); row.Action = SpreadsheetRowAction.Skip; return;
        }
        var exact = scope.Where(x => x.Source!.FileHash == preview.FileHash && x.Source.RowNumber == row.RowNumber).ToList();
        if (exact.Count == 1) { Target(row, exact[0], SpreadsheetRowAction.Skip); return; }
        var signatures = scope.Where(x => x.Source!.RowSignature == row.RowSignature).ToList();
        if (signatures.Count == 1) { Target(row, signatures[0], SpreadsheetRowAction.Skip); return; }
        if (signatures.Count > 1)
        {
            row.Action = SpreadsheetRowAction.Skip;
            row.Issues.Add("Несколько объектов имеют такую исходную строку. Выберите соответствие вручную."); return;
        }
        // A row number, shared kit inventory number, or a PC name shared by its monitor is not an identity.
        var candidates = scope.Where(x => SameDevice(row, x)).ToList();
        if (candidates.Count == 1)
        {
            Target(row, candidates[0], SpreadsheetRowAction.Update);
            row.Issues.Add("Предложено обновление существующего объекта. Размещение сохраняется, пока не выбрано его изменение.");
        }
        else if (scope.Count > 0)
        {
            row.Action = SpreadsheetRowAction.Skip;
            row.Issues.Add(candidates.Count > 1
                ? "Несколько возможных соответствий. Выберите объект или действие «Добавить»."
                : "Изменённый файл: соответствие не установлено. Выберите объект или действие «Добавить».");
        }
    }

    private static void Target(SpreadsheetPreviewRow row, AssetDto asset, SpreadsheetRowAction action)
    {
        row.TargetAssetId = asset.Id; row.TargetVersion = asset.Version;
        if (row.Type.Length > 0) row.Action = action;
    }
    private static bool SameDevice(SpreadsheetPreviewRow row, AssetDto asset)
    {
        if (Normalize(row.Type) != Normalize(asset.Type)) return false;
        if (row.Mac.Length > 0 && NormalizeMac(row.Mac) == NormalizeMac(asset.Mac)) return true;
        if (row.Model.Length == 0 || Normalize(row.Model) != Normalize(asset.Model)) return false;
        if (row.Hostname.Length > 0 && Normalize(row.Hostname) == Normalize(asset.Hostname)) return true;
        return row.InventoryNumber.Length > 0 && Normalize(row.InventoryNumber) == Normalize(asset.InventoryNumber);
    }
    private static string NormalizeMac(string value) => Regex.Replace(value, "[-:.\\s]", "").ToUpperInvariant();
    private static string Signature(SpreadsheetPreviewRow row) => Hash(JsonSerializer.Serialize(new[]
    {
        row.Building,row.Room,row.Hostname,row.Type,row.Model,row.InventoryNumber,row.Description,row.Notes,row.Mac
    }.Select(Normalize)));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Normalize(string value) => Regex.Replace(value.Trim().Normalize(), "\\s+", " ").ToUpperInvariant();
    public static string RoomKey(string building, string number)
    {
        var b = Normalize(building); var n = Normalize(number);
        // Only an exact, case-insensitive building suffix is removed. 307А and 307A remain different rooms.
        if (b.Length > 0 && n.EndsWith(b, StringComparison.Ordinal) && n.Length > b.Length) n = n[..^b.Length].TrimEnd();
        return b + "|" + n;
    }
    private static int InferFloor(string room) => room.Trim().FirstOrDefault() is >= '1' and <= '9' ? room.Trim()[0] - '0' : 0;
    private static string ReadCell(Cell cell, string[] strings, Stylesheet? styles)
    {
        var raw = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? "";
        if (cell.DataType?.Value == CellValues.SharedString)
            return int.TryParse(raw, out int i) && i >= 0 && i < strings.Length ? strings[i] : raw;
        if (cell.DataType?.Value == CellValues.InlineString) return cell.InlineString?.InnerText ?? raw;
        if (cell.DataType?.Value == CellValues.Boolean) return raw == "1" ? "TRUE" : "FALSE";
        if (cell.DataType is not null && cell.DataType.Value != CellValues.Number) return raw;
        // Keep identifiers as text and honor a simple numeric zero mask (e.g. 000000).
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            string? format = null;
            if (cell.StyleIndex is not null && styles?.CellFormats is not null)
            {
                var cf = styles.CellFormats.Elements<CellFormat>().ElementAtOrDefault((int)cell.StyleIndex.Value);
                var id = cf?.NumberFormatId?.Value;
                format = styles.NumberingFormats?.Elements<NumberingFormat>().FirstOrDefault(x => x.NumberFormatId?.Value == id)?.FormatCode?.Value;
            }
            if (format is not null && Regex.IsMatch(format, "^0{2,}$") && decimal.Truncate(number) == number)
                return number.ToString(format, CultureInfo.InvariantCulture);
            return number.ToString("0.############################", CultureInfo.InvariantCulture);
        }
        return raw;
    }
    private static string ColumnName(int index)
    {
        var name = "";
        while (index > 0) { index--; name = (char)('A' + index % 26) + name; index /= 26; }
        return name;
    }
}
