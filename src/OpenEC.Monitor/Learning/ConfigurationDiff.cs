using System.Globalization;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;

namespace OpenEC.Monitor.Learning;

/// <summary>Compares a declared ENI against what the bus actually showed. This is the diagnostic
/// the commercial tools do not advertise: "your ENI no longer matches the machine."</summary>
public static class ConfigurationDiff
{
    /// <summary>Variables a master synthesises into the ENI's process image that have no wire
    /// representation, so a learned configuration can never contain them.
    ///
    /// TwinCAT's "Add WC state bit(s)" adds `WcState` and `InputToggle`, both computed by the
    /// master, and the whole `InfoData` group (State, AdsAddr, AoE NetId, Channels, DC shift times,
    /// ObjectId) is master-side bookkeeping. Matching `InfoData` as a path segment rather than by
    /// leaf name is deliberate: it is exact, and it cannot accidentally swallow a real variable.
    ///
    /// Deliberately NOT excluded: `TxPdoState`, `DcInputShift` and `DcOutputShift`. Those are
    /// genuine PDO entries on many drives, and excluding them would hide a real remapping — the
    /// one failure that would make this whole comparison worthless.</summary>
    private static bool IsMasterSynthesised(EniVariable variable)
    {
        if (variable.Name.Contains(".InfoData.", StringComparison.Ordinal)) return true;
        var leaf = variable.Name.AsSpan()[(variable.Name.LastIndexOf('.') + 1)..];
        return leaf.Equals("WcState", StringComparison.Ordinal)
            || leaf.Equals("InputToggle", StringComparison.Ordinal);
    }

    public static IReadOnlyList<MonitorEvent.ConfigMismatch> Compare(
        EniConfiguration declared, EniConfiguration learned) =>
        Compare(declared, learned, DateTimeOffset.UnixEpoch);

    public static IReadOnlyList<MonitorEvent.ConfigMismatch> Compare(
        EniConfiguration declared, EniConfiguration learned, DateTimeOffset timestamp)
    {
        var mismatches = new List<MonitorEvent.ConfigMismatch>();
        var learnedSlaves = learned.Slaves.ToDictionary(s => s.PhysAddr);

        foreach (var slave in declared.Slaves)
        {
            if (!learnedSlaves.TryGetValue(slave.PhysAddr, out var observed))
            {
                mismatches.Add(new MonitorEvent.ConfigMismatch(timestamp,
                    ConfigMismatchKind.SlaveMissing, slave.PhysAddr,
                    Identity(slave), "not seen on the bus"));
                continue;
            }
            // Identity is only comparable when the wire actually revealed it; a zero means "not
            // observed" (startup checking disabled), which is a completeness gap, not a mismatch.
            if (observed.VendorId != 0 && observed.ProductCode != 0
                && (observed.VendorId != slave.VendorId || observed.ProductCode != slave.ProductCode))
            {
                mismatches.Add(new MonitorEvent.ConfigMismatch(timestamp,
                    ConfigMismatchKind.Identity, slave.PhysAddr,
                    Identity(slave), Identity(observed)));
            }
        }

        foreach (var slave in learned.Slaves.Where(s => declared.Slaves.All(d => d.PhysAddr != s.PhysAddr)))
        {
            mismatches.Add(new MonitorEvent.ConfigMismatch(timestamp,
                ConfigMismatchKind.SlaveUnexpected, slave.PhysAddr,
                "not in the ENI", Identity(slave)));
        }

        // Keyed on placement, not name: a learned name is synthesised ("Slave 1002 (EL1008)…")
        // while a declared ENI carries the master's own labels ("Term 2 (EL1008)…"), so the two
        // sides never share a name even when the wire matches the declaration exactly. What a
        // process-image cross-check actually answers is "is there a variable of this size, in this
        // direction, at this offset?" — placement is the only thing both sides genuinely share.
        var learnedPlacements = new HashSet<(int BitOffs, int BitSize, bool IsInput)>(
            learned.Variables.Select(v => (v.BitOffs, v.BitSize, v.IsInput)));
        // Same-shape fallback for the "moved" case: a variable absent at its declared placement whose
        // size and direction match exactly ONE learned variable can be reported at that variable's
        // offset, because "where did it go" then has a single answer.
        //
        // Only when the group has one member. An earlier version grouped by (BitSize, IsInput) and
        // reported `g.First().BitOffs` — an arbitrary representative — so on any bus carrying more
        // than one entry of the same size and direction (nearly all of them) every genuine "PDO
        // remapped at runtime" finding named a wrong offset: a variable displaced to bit 500 was
        // reported as "observed @bit 0" while bit 0 held a different entry on a different slave.
        // Nothing had looked for where the variable actually went. Where the answer is ambiguous the
        // honest report is the declared placement's absence, in the same words as the no-counterpart
        // branch — the alternative is a specific, confidently wrong number.
        var unambiguousByShape = learned.Variables
            .GroupBy(v => (v.BitSize, v.IsInput))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().BitOffs);
        foreach (var variable in declared.Variables.Where(v => !IsMasterSynthesised(v)))
        {
            if (learnedPlacements.Contains((variable.BitOffs, variable.BitSize, variable.IsInput)))
                continue;

            var observed = unambiguousByShape
                .TryGetValue((variable.BitSize, variable.IsInput), out var offset)
                    ? $"@bit {offset}"
                    : "not in the learned image";
            mismatches.Add(new MonitorEvent.ConfigMismatch(timestamp,
                ConfigMismatchKind.ProcessImage, null,
                $"{variable.Name} @bit {variable.BitOffs}", observed));
        }

        return mismatches;
    }

    private static string Identity(EniSlave slave) => string.Create(CultureInfo.InvariantCulture,
        $"{slave.Name} (0x{slave.VendorId:X4}:0x{slave.ProductCode:X8})");
}
