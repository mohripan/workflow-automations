using System.Collections.Concurrent;
using FlowForge.Contracts.Events;

namespace FlowForge.JobAutomator.Cache;

public class AutomationCache
{
    private readonly ConcurrentDictionary<Guid, AutomationSnapshot> _cache = new();

    public void Seed(IEnumerable<AutomationSnapshot> automations)
    {
        _cache.Clear();
        foreach (var a in automations)
            _cache[a.Id] = a;
    }

    public void Upsert(AutomationSnapshot snapshot)
    {
        _cache[snapshot.Id] = snapshot;
    }

    public void Remove(Guid id)
    {
        _cache.TryRemove(id, out _);
    }

    public IReadOnlyList<AutomationSnapshot> GetAll() => _cache.Values.ToList();

    public AutomationSnapshot? Get(Guid id)
    {
        _cache.TryGetValue(id, out var snapshot);
        return snapshot;
    }
}
