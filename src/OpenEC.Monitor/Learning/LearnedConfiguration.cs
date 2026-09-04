using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor.Learning;

/// <summary>A learned bus configuration. Wraps <see cref="EniConfiguration"/> rather than
/// replacing it, so every existing consumer — ProcessVariableMap, WkcTracker, BusModel —
/// works against learned and declared configurations identically.</summary>
public sealed record LearnedConfiguration(
    EniConfiguration Configuration,
    LearningCompleteness Completeness,
    IReadOnlyDictionary<ushort, FactProvenance> Provenance,
    int Revision);
