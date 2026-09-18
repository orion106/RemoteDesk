using Microsoft.VisualBasic.FileIO;
using System.Text;

namespace RemoteAssist;

public static class InventoryImport
{
    public static IReadOnlyList<LabComputer> Csv(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text;
        using (var reader = new StreamReader(path, new UTF8Encoding(false, true), true))
            try { text = reader.ReadToEnd(); }
            catch (DecoderFallbackException) { text = File.ReadAllText(path, Encoding.GetEncoding(1251)); }
        var firstLine = text.Split('\n')[0];
        var delimiter = new[] { ';', ',', '\t' }.OrderByDescending(c => firstLine.Count(x => x == c)).First();
        using var parser = new TextFieldParser(new StringReader(text)) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(delimiter.ToString()); var headers = parser.ReadFields() ?? throw new InvalidDataException("CSV пуст.");
        string Key(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        int Column(params string[] names) => Array.FindIndex(headers, h => names.Any(n => Key(h) == Key(n)));
        var nameCol = Column("Name", "Имя", "Название", "Computer name", "Компьютер - Имя");
        if (nameCol < 0) throw new InvalidDataException("В CSV нужна колонка «Имя» / Name. Дополнительные колонки: UUID, Серийный номер, Расположение, IP, MAC, ОС.");
        var uuidCol = Column("UUID", "Hardware UUID"); var serialCol = Column("Serial", "Serial number", "Серийный номер");
        var idCol = Column("ID", "Идентификатор", "Computer ID");
        var roomCol = Column("Location", "Complete name", "Расположение", "Местоположение", "Аудитория");
        var ipCol = Column("IP", "IP address", "IP addresses", "IP-адрес", "IP-адреса", "Адрес");
        var macCol = Column("MAC", "MAC address", "MAC-адрес", "MAC-адреса");
        var osCol = Column("OS", "Operating system", "ОС", "Операционная система", "Операционные системы - Имя");
        var seatCol = Column("Seat", "Место", "Номер места"); var modelCol = Column("Model", "Модель");
        var result = new List<LabComputer>();
        while (!parser.EndOfData)
        {
            var cells = parser.ReadFields(); if (cells is null) continue;
            string Value(int index) => index >= 0 && index < cells.Length ? cells[index].Trim() : "";
            var name = Value(nameCol); if (name.Length == 0) continue;
            var pc = new LabComputer { Name = name, Uuid = Value(uuidCol), Serial = Value(serialCol), Room = Value(roomCol) is { Length: > 0 } room ? room : "Без аудитории", Seat = Value(seatCol), Model = Value(modelCol), InventoryNote = "Импорт из CSV GLPI; требуется живая проверка идентичности" };
            var osName = Value(osCol); var os = osName.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? LabOs.Windows : osName.Contains("Astra", StringComparison.OrdinalIgnoreCase) || osName.Contains("Астра", StringComparison.OrdinalIgnoreCase) ? LabOs.Astra : LabOs.Unknown;
            var addresses = Value(ipCol).Split(['\r', '\n', ',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (addresses.Length == 0) addresses = [name];
            // A hostname alone must not collapse unrelated records or different OS profiles.
            var reference = Value(idCol) is { Length: > 0 } sourceId ? "id:" + sourceId
                : MachineIdentity.Valid(pc.Uuid) ? "uuid:" + MachineIdentity.Normalize(pc.Uuid)
                : MachineIdentity.Valid(pc.Serial) ? "serial:" + MachineIdentity.Normalize(pc.Serial)
                : "unverified:" + name.ToUpperInvariant() + ":" + os + ":" + string.Join(",", addresses.Order(StringComparer.OrdinalIgnoreCase)).ToUpperInvariant();
            pc.GlpiIds.Add("csv:" + reference);
            foreach (var address in addresses) { LabWire.ValidateHost(address); pc.Systems.Add(new() { Os = os, Address = address, Description = osName }); }
            pc.MacAddresses = Value(macCol).Split(['\r', '\n', ',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            result.Add(pc);
        }
        return result;
    }
    public static LabComputer FromAd(AdComputer pc) => new()
    {
        Name = pc.Name, Room = "Без аудитории", GlpiIds = ["ad:" + pc.DistinguishedName],
        InventoryNote = "Из AD: аппаратный ID ещё не подтверждён. Выполните диагностику и сохраните UUID/серийный номер.",
        Systems = [new() { Os = LabOs.Windows, Address = pc.DnsHostName ?? pc.Name }]
    };
}
