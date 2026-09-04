namespace OpenEC.Monitor.Eni;

/// <summary>Partitions an ENI's process variables by owning slave, matched by name
/// prefix ("SlaveName." …). Longest slave name wins; identical names resolve to the
/// lowest PhysAddr. Pure and immutable — a heuristic over TwinCAT-style ENI naming,
/// safe because unmatched variables stay reachable through <see cref="Unmatched"/>.</summary>
public sealed record ProcessVariableAssignment(
    IReadOnlyDictionary<ushort, IReadOnlyList<EniVariable>> BySlave,
    IReadOnlyList<EniVariable> Unmatched)
{
    public static ProcessVariableAssignment Build(EniConfiguration eni)
    {
        var candidates = eni.Slaves
            .OrderByDescending(s => s.Name.Length).ThenBy(s => s.PhysAddr).ToList();
        var bySlave = new Dictionary<ushort, List<EniVariable>>();
        foreach (var s in eni.Slaves) bySlave.TryAdd(s.PhysAddr, []);
        var unmatched = new List<EniVariable>();
        foreach (var v in eni.Variables)
        {
            var owner = candidates.FirstOrDefault(
                s => v.Name.StartsWith(s.Name + ".", StringComparison.Ordinal));
            if (owner is null) unmatched.Add(v);
            else bySlave[owner.PhysAddr].Add(v);
        }
        return new ProcessVariableAssignment(
            bySlave.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<EniVariable>)kv.Value),
            unmatched);
    }
}
