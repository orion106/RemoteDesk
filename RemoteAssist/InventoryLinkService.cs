namespace RemoteAssist;

public sealed record InventoryComputerLink(string InstanceId, string LocalComputerId, string AssetId);

public sealed record InventoryPlacementProjection(
    string AssetId, string RoomDisplay, string Seat, bool Archived = false, string Uuid = "", string Serial = "");

/// <summary>Local associations only; inventory metadata never grants trust to a remote endpoint.</summary>
public sealed class InventoryLinkService : IDisposable
{
    public const string RecordKind = "inventory-link";
    private readonly LabStore _store;
    private readonly LabRuntime _runtime;
    private readonly object _sync = new();
    private List<InventoryComputerLink> _links;
    private Dictionary<string, InventoryPlacementProjection> _placements = new(StringComparer.Ordinal);
    private string _instanceId = "";
    private bool _applying, _disposed;

    public InventoryLinkService(LabStore store, LabRuntime runtime)
    {
        _store = store; _runtime = runtime;
        _links = store.Load<InventoryComputerLink>(RecordKind);
        if (_links.Any(x => string.IsNullOrWhiteSpace(x.LocalComputerId) || string.IsNullOrWhiteSpace(x.InstanceId) || string.IsNullOrWhiteSpace(x.AssetId)) ||
            _links.GroupBy(x => x.LocalComputerId).Any(x => x.Count() > 1) ||
            _links.GroupBy(x => (x.InstanceId, x.AssetId)).Any(x => x.Count() > 1))
            throw new InvalidDataException("В локальных связях инвентаризации обнаружены повторяющиеся или пустые идентификаторы.");
        runtime.Changed += RuntimeChanged;
        Reproject();
    }

    public string CurrentInstanceId { get { lock (_sync) return _instanceId; } }
    public IReadOnlyList<InventoryComputerLink> Links { get { lock (_sync) return _links.ToArray(); } }
    public event Action? Changed;

    public void ApplySnapshot(string instanceId, IEnumerable<InventoryPlacementProjection> placements)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Не задан идентификатор сервера инвентаризации.", nameof(instanceId));
        var snapshot = placements.ToDictionary(x => x.AssetId, StringComparer.Ordinal);
        if (snapshot.Keys.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Снимок инвентаризации содержит пустой идентификатор оборудования.");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _instanceId = instanceId; _placements = snapshot; Reproject();
        }
        Changed?.Invoke();
    }

    public void ClearSnapshot()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _instanceId = ""; _placements.Clear();
        }
        Changed?.Invoke();
    }

    public InventoryComputerLink? GetLink(string localComputerId)
    {
        lock (_sync) return _links.FirstOrDefault(x => x.LocalComputerId == localComputerId);
    }

    public string? GetLocalComputerId(string assetId)
    {
        lock (_sync) return _links.FirstOrDefault(x => x.InstanceId == _instanceId && x.AssetId == assetId)?.LocalComputerId;
    }

    public string GetStatus(string localComputerId)
    {
        lock (_sync)
        {
            var link = _links.FirstOrDefault(x => x.LocalComputerId == localComputerId);
            if (link is null) return "";
            if (_instanceId.Length == 0) return "Размещение связано с инвентаризацией. Ожидается загрузка данных сервера.";
            if (link.InstanceId != _instanceId) return "Связь относится к другому серверу инвентаризации. Сохранено последнее размещение.";
            if (!_placements.TryGetValue(link.AssetId, out var placement)) return "Объект не найден на сервере. Проверьте связь; последнее размещение сохранено.";
            return placement.Archived ? "Объект инвентаризации архивирован. Локальная карточка ПК сохранена." : "Аудитория и место задаются в общей инвентаризации.";
        }
    }

    public bool CanOpenInventory(string localComputerId)
    {
        lock (_sync)
        {
            var link = _links.FirstOrDefault(x => x.LocalComputerId == localComputerId);
            return link is not null && link.InstanceId == _instanceId && _placements.ContainsKey(link.AssetId);
        }
    }

    public IReadOnlyList<InventoryPlacementProjection> SuggestedAssets(string localComputerId)
    {
        lock (_sync)
        {
            var computer = _runtime.FindComputer(localComputerId);
            if (computer is null) return [];
            return _placements.Values.Where(x => !x.Archived &&
                MachineIdentity.SameHardware(computer, new LabComputer { Uuid = x.Uuid, Serial = x.Serial })).ToArray();
        }
    }

    public void Link(string localComputerId, string assetId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runtime.FindComputer(localComputerId) is null) throw new InvalidOperationException("Локальная карточка компьютера не найдена.");
            if (_instanceId.Length == 0 || !_placements.TryGetValue(assetId, out var placement)) throw new InvalidOperationException("Сначала загрузите этот объект с сервера инвентаризации.");
            if (placement.Archived) throw new InvalidOperationException("Нельзя создавать связь с архивированным объектом.");
            var existing = _links.FirstOrDefault(x => x.LocalComputerId == localComputerId);
            if (existing is not null && (existing.InstanceId != _instanceId || existing.AssetId != assetId))
                throw new InvalidOperationException("Этот ПК уже связан с другим объектом. Сначала снимите прежнюю связь.");
            if (_links.Any(x => x.InstanceId == _instanceId && x.AssetId == assetId && x.LocalComputerId != localComputerId))
                throw new InvalidOperationException("Этот объект уже связан с другой локальной карточкой ПК. Проверьте дубли компьютеров.");
            if (existing is null)
            {
                var link = new InventoryComputerLink(_instanceId, localComputerId, assetId);
                _store.Save(RecordKind, localComputerId, link); _links.Add(link);
            }
            Reproject();
        }
        Changed?.Invoke();
    }

    public void Unlink(string localComputerId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _store.Delete(RecordKind, localComputerId);
            _links.RemoveAll(x => x.LocalComputerId == localComputerId);
        }
        Changed?.Invoke();
    }

    public void PreserveLinkedPlacement(LabComputer draft)
    {
        lock (_sync)
        {
            if (!_links.Any(x => x.LocalComputerId == draft.Id)) return;
            var latest = _runtime.FindComputer(draft.Id) ?? throw new InvalidOperationException("Связанная локальная карточка больше не существует.");
            draft.Room = latest.Room; draft.Seat = latest.Seat; draft.LocalPlacement = true;
        }
    }

    public void SaveComputer(LabComputer draft)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var saved = draft.Copy(); PreserveLinkedPlacement(saved); _runtime.SaveComputer(saved);
        }
    }

    public void MergeComputers(string primaryId, string secondaryId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var first = _links.FirstOrDefault(x => x.LocalComputerId == primaryId);
            var second = _links.FirstOrDefault(x => x.LocalComputerId == secondaryId);
            if (first is not null && second is not null && (first.InstanceId != second.InstanceId || first.AssetId != second.AssetId))
                throw new InvalidOperationException("Карточки связаны с разными объектами инвентаризации. Исправьте связи перед объединением.");
            var replacement = _links.Where(x => x.LocalComputerId != primaryId && x.LocalComputerId != secondaryId).ToList();
            var kept = first ?? second;
            if (kept is not null) replacement.Add(kept with { LocalComputerId = primaryId });
            _applying = true;
            try
            {
                _runtime.MergeComputers(primaryId, secondaryId, replacement, kept?.LocalComputerId);
                _links = replacement;
            }
            finally { _applying = false; }
            Reproject();
        }
        Changed?.Invoke();
    }

    private void RuntimeChanged()
    {
        lock (_sync)
        {
            if (_disposed || _applying) return;
            Reproject();
        }
        Changed?.Invoke();
    }

    private void Reproject()
    {
        lock (_sync)
        {
            if (_disposed || _applying) return;
            _applying = true;
            try
            {
                var placements = _links.ToDictionary(x => x.LocalComputerId,
                    x => x.InstanceId == _instanceId ? _placements.GetValueOrDefault(x.AssetId) : null);
                _runtime.ApplyInventoryPlacements(placements);
            }
            finally { _applying = false; }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _runtime.Changed -= RuntimeChanged;
        }
    }
}
