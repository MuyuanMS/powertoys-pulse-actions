namespace Pulse.Host;

/// <summary>Separate the model JSON, Host projections, and escaped CLI transport budgets.</summary>
public static class ResultLimits
{
    public const int MaximumModelBytes = 320 * 1024;
    // Compatibility projections duplicate only subsets of the canonical result. IDs and Host
    // diagnostics require a separate allowance; they must not invalidate valid model output.
    public const int MaximumNormalizedResultBytes = 2 * MaximumModelBytes + 64 * 1024;
    // A JSON string may encode one model byte as six ASCII characters (for example \u003C).
    // Reserve bounded outer-event metadata in addition to that worst-case escaped payload.
    public const int MaximumCliEventCharacters = 6 * MaximumModelBytes + 16 * 1024;
    public const int MaximumFinalTextCharacters = MaximumModelBytes + 1024;
    public const int RawDiagnosticCharacters = 32768;
    // Full v3 reports retain all findings. Byte/resource limits remain explicit, while Native
    // Messaging consumers page the stored report instead of reducing it to the first N items.
    public const int MaximumV3ModelBytes = 8 * 1024 * 1024;
    public const int MaximumV3NormalizedResultBytes = 2 * MaximumV3ModelBytes + 512 * 1024;
    public const int MaximumV3CliEventCharacters = 6 * MaximumV3ModelBytes + 16 * 1024;
    public const int MaximumV3FinalTextCharacters = MaximumV3ModelBytes + 1024;
    public const long MaximumV3LogBytes = 64L * 1024 * 1024;
    public const long MaximumLegacyLogBytes = 8L * 1024 * 1024;
}
