namespace Moz.Devices;

/// <summary>One field of a user-defined struct: a scalar type name or another struct name.</summary>
public sealed record StructField(string Name, string Type, int Count = 1, int Length = 0);

/// <summary>A user-defined struct: fields laid out sequentially in declaration order.</summary>
public sealed record StructDef(string Name, IReadOnlyList<StructField> Fields);

/// <summary>
/// Expands a variable declaration into leaf <see cref="Variable"/>s. Scalars
/// pass through; a struct becomes one leaf per field, named
/// <c>name.field</c> (nested: <c>name.inner.field</c>, arrays of structs:
/// <c>name[i].field</c>, 0-based), laid out sequentially on the word table.
/// A bool field takes one word (<see cref="VariableType.BoolWord"/>); a field
/// array stays one leaf with its count. The VariableTable never sees structs.
/// Unknown types and cycles throw ArgumentException — config validation
/// reports them to the user first; this is the programming-bug backstop.
/// </summary>
public static class VariableLayout
{
    public static IReadOnlyList<Variable> Expand(string name, TableKind table, int offset,
        string type, int count, int length, IReadOnlyDictionary<string, StructDef> structs)
    {
        var leaves = new List<Variable>();
        if (VariableTypeNames.TryParse(type, out var scalar))
        {
            leaves.Add(new Variable(name, table, offset, scalar, count, length));
            return leaves;
        }
        var def = Resolve(type, structs);
        int stride = Words(type, 0, structs);
        for (int i = 0; i < count; i++)
        {
            ExpandStruct(count == 1 ? name : $"{name}[{i}]", table, offset + i * stride, def, structs, leaves, []);
        }
        return leaves;
    }

    /// <summary>Words per element of a scalar (string: ceil(length/2), bool: 1) or struct type.</summary>
    public static int Words(string type, int length, IReadOnlyDictionary<string, StructDef> structs) =>
        Words(type, length, structs, []);

    private static int Words(string type, int length, IReadOnlyDictionary<string, StructDef> structs, HashSet<string> visiting)
    {
        if (VariableTypeNames.TryParse(type, out var scalar))
        {
            return scalar switch
            {
                VariableType.Bool => 1,
                VariableType.String => (length + 1) / 2,
                _ => Variable.WordSize(scalar),
            };
        }
        var def = Resolve(type, structs);
        Enter(type, visiting);
        int words = 0;
        foreach (var f in def.Fields)
        {
            words += Words(f.Type, f.Length, structs, visiting) * f.Count;
        }
        visiting.Remove(type);
        return words;
    }

    private static void ExpandStruct(string prefix, TableKind table, int offset, StructDef def,
        IReadOnlyDictionary<string, StructDef> structs, List<Variable> leaves, HashSet<string> visiting)
    {
        Enter(def.Name, visiting);
        foreach (var f in def.Fields)
        {
            string leafName = prefix + "." + f.Name;
            if (VariableTypeNames.TryParse(f.Type, out var scalar))
            {
                var leafType = scalar == VariableType.Bool ? VariableType.BoolWord : scalar;
                var leaf = new Variable(leafName, table, offset, leafType, f.Count, f.Length);
                leaves.Add(leaf);
                offset += leaf.Cells;
                continue;
            }
            var inner = Resolve(f.Type, structs);
            int stride = Words(f.Type, 0, structs);
            for (int i = 0; i < f.Count; i++)
            {
                ExpandStruct(f.Count == 1 ? leafName : $"{leafName}[{i}]", table, offset + i * stride,
                    inner, structs, leaves, visiting);
            }
            offset += stride * f.Count;
        }
        visiting.Remove(def.Name);
    }

    private static StructDef Resolve(string type, IReadOnlyDictionary<string, StructDef> structs) =>
        structs.TryGetValue(type, out var def)
            ? def
            : throw new ArgumentException($"unknown type \"{type}\" (not a scalar type or a declared struct)", nameof(type));

    private static void Enter(string type, HashSet<string> visiting)
    {
        if (!visiting.Add(type))
        {
            throw new ArgumentException($"struct cycle through \"{type}\"", nameof(type));
        }
    }
}
