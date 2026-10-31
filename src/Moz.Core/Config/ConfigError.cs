namespace Moz.Config;

/// <summary>
/// One config problem with its TOML location (file path, not TOML line —
/// the strictness pass works on the deserialized model). All problems are
/// collected and reported together instead of failing on the first.
/// </summary>
public sealed record ConfigError(string File, string Message)
{
    public override string ToString() => $"{File}: {Message}";
}

/// <summary>Outcome of <see cref="ConfigLoader.Load"/>: all-at-once error reporting.</summary>
public sealed record ConfigResult(string File, MozConfig? Config, IReadOnlyList<ConfigError> Errors)
{
    public bool Ok => Config is not null && Errors.Count == 0;
}