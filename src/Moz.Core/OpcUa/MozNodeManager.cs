using Microsoft.Extensions.Logging;
using Moz.Devices;
using Opc.Ua;
using Opc.Ua.Server;
using Opc.Ua.Server.Fluent;

namespace Moz.OpcUa;

/// <summary>
/// Node manager that mirrors the device's typed variables as UA nodes:
/// one root folder per device, one scalar node per variable
/// (<c>ns=&lt;manager&gt;;&lt;name&gt;</c>), values live-read from the device
/// (OnSimpleReadValue) and updated on device Watch so subscriptions fire.
/// The namespace URI is registered at CONSTRUCTION time — required for the
/// request routing table to map our namespace to this manager (spike finding).
/// </summary>
internal sealed class MozNodeManager(IServerInternal server, ApplicationConfiguration configuration,
    Device device, VariableTable variables, ILogger logger)
    : AsyncCustomNodeManager(server, configuration, NamespaceUri)
{
    public const string NamespaceUri = "urn:moz:ua:ns";

    private readonly Device _device = device;
    private readonly VariableTable _variables = variables;
    private readonly ILogger _logger = logger;
    private readonly Dictionary<string, BaseDataVariableState> _nodes = new(StringComparer.Ordinal);

    /// <summary>The namespace index this manager's nodes live under.</summary>
    public ushort NodeNamespaceIndex => (ushort)NamespaceIndex;

    public override async ValueTask WriteAsync(
        OperationContext context,
        ArrayOf<WriteValue> nodesToWrite,
        IList<ServiceResult> errors,
        CancellationToken cancellationToken = default)
    {
        await base.WriteAsync(context, nodesToWrite, errors, cancellationToken);
    }

    public override async ValueTask CreateAddressSpaceAsync(
        IDictionary<NodeId, IList<IReference>> externalReferences,
        CancellationToken cancellationToken = default)
    {
        var folder = new FolderState(null)
        {
            NodeId = new NodeId("device", (ushort)NamespaceIndex),
            BrowseName = new QualifiedName(_device.Name, NamespaceIndex),
            DisplayName = new LocalizedText(_device.Name),
            ReferenceTypeId = new NodeId(35), // Organizes
            TypeDefinitionId = new NodeId(61), // FolderType
        };
        foreach (var v in _variables.Variables)
        {
            var node = new BaseDataVariableState(null)
            {
                NodeId = new NodeId(v.Name, (ushort)NamespaceIndex),
                BrowseName = new QualifiedName(v.Name, NamespaceIndex),
                DisplayName = new LocalizedText(v.Name),
                ReferenceTypeId = new NodeId(35), // Organizes
                TypeDefinitionId = new NodeId(63), // BaseDataVariableType (i=63)
                DataType = ToUaDataType(v.Type),
                ValueRank = ValueRanks.Scalar,
                // StatusWrite/TimestampWrite live in the EXTENDED bits of
                // AccessLevelEx (the served AccessLevel byte masks them out).
                // Many UA clients (asyncua, UAExpert) send timestamps with
                // value writes; the simulator ignores them (karakuri parity).
                AccessLevelEx = (uint)(AccessLevels.CurrentReadOrWrite
                    | AccessLevels.StatusWrite
                    | AccessLevels.TimestampWrite),
                OnSimpleWriteValue = OnWriteVariable,
                // grant write regardless of session user (karakuri parity)
                OnReadUserAccessLevel = OnEffectiveUserAccessLevel,
                Value = ToVariant(ReadTyped(v)),
            };
            folder.AddChild(node);
            _nodes[v.Name] = node;

            // device change → node value → subscriptions
            _device.Watch(v.Table, v.Offset, v.Cells, _ => UpdateNode(node, v));
        }

        // index the whole subtree (children were added after the folder);
        await AddPredefinedNodeAsync(SystemContext, folder, cancellationToken);

        // make the folder discoverable under Objects
        externalReferences.TryAdd(ObjectIds.ObjectsFolder,
        [
            new ReferenceNode
            {
                ReferenceTypeId = new NodeId(35), // Organizes
                IsInverse = true,
                TargetId = folder.NodeId,
            },
        ]);

        _logger.LogInformation(
            "opcua address space: {Count} node(s) nsIndex={Ns} for device {Device}",
            _nodes.Count, NamespaceIndex, _device.Name);
    }

    /// <summary>
    /// Always grant CurrentReadOrWrite to the caller: moz is a simulator with
    /// no access control (karakuri parity). This overrides any deny the
    /// session user context would apply to anonymous writes.
    /// </summary>
    private static ServiceResult OnEffectiveUserAccessLevel(
        ISystemContext context, NodeState node, ref byte userAccessLevel)
    {
        userAccessLevel = AccessLevels.CurrentReadOrWrite;
        return ServiceResult.Good;
    }

    /// <summary>Device write → typed value → node Value + change masks (fires subscriptions).</summary>
    private void UpdateNode(BaseDataVariableState node, Variable v)
    {
        try
        {
            node.Value = ToVariant(ReadTyped(v));
            node.UpdateChangeMasks(NodeStateChangeMasks.Value);
            node.ClearChangeMasks(SystemContext, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "opcua node update failed for {Variable}", v.Name);
        }
    }

    /// <summary>Read the variable from the device (boxed).</summary>
    private object ReadTyped(Variable v) => v.Type switch
    {
        VariableType.Bool => _variables.Read<bool>(v.Name),
        VariableType.Int or VariableType.Dint => _variables.Read<int>(v.Name),
        VariableType.Uint or VariableType.Udint => _variables.Read<uint>(v.Name),
        VariableType.Real => _variables.Read<float>(v.Name),
        VariableType.Lint => _variables.Read<long>(v.Name),
        VariableType.Ulint => _variables.Read<ulong>(v.Name),
        _ => throw new ArgumentOutOfRangeException(nameof(v), v.Type, null),
    };

    /// <summary>
    /// The single write hook: resolves the variable by browse name and routes
    /// through VariableTable so moz's range/readonly rules surface as proper
    /// OPC UA status codes.
    /// </summary>
    private ServiceResult OnWriteVariable(
        ISystemContext context, NodeState node, ref Variant value)
    {
        if (node is not BaseDataVariableState state)
        {
            return new ServiceResult(StatusCodes.BadNodeIdUnknown);
        }
        var name = state.BrowseName.Name ?? "";
        Variable v;
        try
        {
            v = _variables.Find(name);
        }
        catch (KeyNotFoundException)
        {
            return new ServiceResult(StatusCodes.BadNodeIdUnknown);
        }

        try
        {
            object hostValue = v.Type switch
            {
                // moz Int/Uint are 16-bit on the wire but int/uint on the host
                VariableType.Int => checked((int)AsInt16(value)),
                VariableType.Uint => checked((int)AsUShort(value)),
                VariableType.Bool => AsBool(value),
                VariableType.Dint => AsInt(value),
                VariableType.Udint => (int)AsUInt(value),
                VariableType.Real => AsFloat(value),
                VariableType.Lint => AsLong(value),
                VariableType.Ulint => checked((long)AsULong(value)),
                _ => throw new ArgumentException($"variable \"{name}\" is not writable"),
            };
            // VariableTable.Write<T> infers T from the value: the boxed
            // object must be written through the typed overload
            switch (v.Type)
            {
                case VariableType.Bool:
                    _variables.Write(name, (bool)hostValue);
                    break;
                case VariableType.Dint:
                    _variables.Write(name, (int)hostValue);
                    break;
                case VariableType.Udint:
                    _variables.Write(name, (uint)hostValue);
                    break;
                case VariableType.Real:
                    _variables.Write(name, (float)hostValue);
                    break;
                case VariableType.Lint:
                    _variables.Write(name, (long)hostValue);
                    break;
                case VariableType.Ulint:
                    _variables.Write(name, (ulong)hostValue);
                    break;
                // moz Int/Uint are 16-bit: the host stores int/uint
                case VariableType.Int:
                    _variables.Write(name, (int)(short)hostValue);
                    break;
                case VariableType.Uint:
                    _variables.Write(name, (uint)(ushort)hostValue);
                    break;
                default:
                    break;
            }
            return ServiceResult.Good;
        }
        catch (ArgumentException ex)
        {
            return ServiceResult.Create(StatusCodes.BadOutOfRange, ex.Message);
        }
        catch (InvalidOperationException)
        {
            return ServiceResult.Create(StatusCodes.BadNotWritable, "{0} is read-only", v.Name);
        }
    }

    private static short AsInt16(Variant v) =>
        v.TryGetValue(out short s) ? s : throw new FormatException("not int16");

    private static ushort AsUShort(Variant v) =>
        v.TryGetValue(out ushort us) ? us : throw new FormatException("not uint16");

    private static bool AsBool(Variant v) =>
        v.TryGetValue(out bool b) ? b : throw new FormatException("not bool");

    private static int AsInt(Variant v) =>
        v.TryGetValue(out int i) ? i : throw new FormatException("not int32");

    private static uint AsUInt(Variant v) =>
        v.TryGetValue(out uint u) ? u : throw new FormatException("not uint32");

    private static float AsFloat(Variant v) =>
        v.TryGetValue(out float f) ? f : throw new FormatException("not float");

    private static long AsLong(Variant v) =>
        v.TryGetValue(out long l) ? l : throw new FormatException("not int64");

    private static ulong AsULong(Variant v) =>
        v.TryGetValue(out ulong ul) ? ul : throw new FormatException("not uint64");

    /// <summary> moz variable type → UA data type (Int/Uint are 16-bit on the wire).</summary>
    private static NodeId ToUaDataType(VariableType type) => type switch
    {
        VariableType.Bool => DataTypeIds.Boolean,
        VariableType.Int => DataTypeIds.Int16,
        VariableType.Uint => DataTypeIds.UInt16,
        VariableType.Dint => DataTypeIds.Int32,
        VariableType.Udint => DataTypeIds.UInt32,
        VariableType.Real => DataTypeIds.Float,
        VariableType.Lint => DataTypeIds.Int64,
        VariableType.Ulint => DataTypeIds.UInt64,
        _ => DataTypeIds.BaseDataType,
    };

    /// <summary>Boxed host value → Variant without the obsolete ctor.</summary>
    private static Variant ToVariant(object value) => value switch
    {
        bool b => new Variant(b),
        short s => new Variant(s),
        ushort us => new Variant(us),
        int i => new Variant(i),
        uint u => new Variant(u),
        float f => new Variant(f),
        long l => new Variant(l),
        ulong ul => new Variant(ul),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
