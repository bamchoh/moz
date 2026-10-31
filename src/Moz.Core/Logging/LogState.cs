using System.Collections;

namespace Moz.Logging;

/// <summary>
/// Base for structured log-state classes shared by CommLog and DeviceLog:
/// implements the key/value list MEL expects (JSON formatters emit the pairs)
/// while ToString() is the humane single line for custom text formatters.
/// </summary>
internal abstract class LogState : IReadOnlyList<KeyValuePair<string, object?>>
{
    protected abstract KeyValuePair<string, object?>[] Items { get; }

    public int Count => Items.Length;
    public KeyValuePair<string, object?> this[int index] => Items[index];
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, object?>>)Items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)Items).GetEnumerator();
}