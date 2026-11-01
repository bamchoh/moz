using Microsoft.Extensions.Logging;
using Moz.Devices;
using Opc.Ua;
using Opc.Ua.Server;
using Opc.Ua.Server.Fluent;

namespace Moz.OpcUa;

/// <summary>
/// Node manager that mirrors the device's typed variables as UA nodes:
/// one root folder per device, one node per variable (scalar, or a
/// OneDimension array for count &gt; 1; <c>ns=&lt;manager&gt;;s=&lt;name&gt;</c>).
/// Node values are refreshed on device Watch so reads and subscriptions
/// always see the device; writes go through <see cref="UaValueCodec"/>.
/// The namespace URI is registered at CONSTRUCTION time — required for the
/// request routing table to map our namespace to this manager (spike finding).
/// </summary>
internal sealed class MozNodeManager(IServerInternal server, ApplicationConfiguration configuration,
    Device device, VariableTable variables, ILogger logger)
    : AsyncCustomNodeManager(server, configuration, NamespaceUri)
{
    public const string NamespaceUri = "urn:moz:ua:ns";

    /// <summary>Read + write of value, status and source timestamp (all fit the AccessLevel byte).</summary>
    private const byte FullAccess = AccessLevels.CurrentReadOrWrite
        | AccessLevels.StatusWrite
        | AccessLevels.TimestampWrite;

    private readonly Device _device = device;
    private readonly VariableTable _variables = variables;
    private readonly ILogger _logger = logger;
    private readonly Dictionary<string, BaseDataVariableState> _nodes = new(StringComparer.Ordinal);

    /// <summary>The namespace index this manager's nodes live under.</summary>
    public ushort NodeNamespaceIndex => (ushort)NamespaceIndex;

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
        // struct leaves ("motor.speed", "motors[1].on") hang under one
        // FolderState per dotted prefix; NodeId = full leaf name, BrowseName = last segment
        var folders = new Dictionary<string, FolderState>(StringComparer.Ordinal);
        foreach (var v in _variables.Variables)
        {
            int dot = v.Name.LastIndexOf('.');
            var parent = dot < 0 ? folder : FolderFor(v.Name[..dot], folder, folders);
            var leafName = dot < 0 ? v.Name : v.Name[(dot + 1)..];
            var node = new BaseDataVariableState(null)
            {
                NodeId = new NodeId(v.Name, (ushort)NamespaceIndex),
                BrowseName = new QualifiedName(leafName, NamespaceIndex),
                DisplayName = new LocalizedText(leafName),
                ReferenceTypeId = new NodeId(35), // Organizes
                TypeDefinitionId = new NodeId(63), // BaseDataVariableType (i=63)
                DataType = UaValueCodec.DataType(v.Type),
                ValueRank = UaValueCodec.ValueRank(v),
                ArrayDimensions = UaValueCodec.ArrayDimensions(v),
                // Many UA clients (asyncua, UAExpert) send a status/timestamp
                // with value writes; the simulator accepts and ignores them.
                AccessLevel = FullAccess,
                UserAccessLevel = FullAccess,
                AccessLevelEx = FullAccess,
                OnSimpleWriteValue = OnWriteVariable,
                // grant write regardless of session user (karakuri parity)
                OnReadUserAccessLevel = OnEffectiveUserAccessLevel,
                Value = UaValueCodec.Read(_variables, v),
            };
            parent.AddChild(node);
            _nodes[v.Name] = node;

            // device change → node value → subscriptions
            _device.Watch(v.Table, v.Offset, v.Cells, _ => UpdateNode(node, v));
        }

        // make the folder discoverable under Objects: Objects --Organizes--> folder
        // (forward reference on the Objects side, inverse on ours)
        folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
        externalReferences.TryAdd(ObjectIds.ObjectsFolder,
        [
            new ReferenceNode
            {
                ReferenceTypeId = ReferenceTypeIds.Organizes,
                IsInverse = false,
                TargetId = folder.NodeId,
            },
        ]);

        // index the whole subtree (children were added after the folder)
        await AddPredefinedNodeAsync(SystemContext, folder, cancellationToken);

        _logger.LogInformation(
            "opcua address space: {Count} node(s) nsIndex={Ns} for device {Device}",
            _nodes.Count, NamespaceIndex, _device.Name);
    }

    /// <summary>Folder for a dotted prefix ("motors[1]" → folder "[1]" under folder "motors"), created on demand.</summary>
    private FolderState FolderFor(string prefix, FolderState root, Dictionary<string, FolderState> folders)
    {
        if (folders.TryGetValue(prefix, out var existing))
        {
            return existing;
        }
        // split point: the last '.' or, for an array element ("motors[1]"), the '['
        int split = prefix.EndsWith(']') ? prefix.LastIndexOf('[') : prefix.LastIndexOf('.');
        var parent = split <= 0 ? root : FolderFor(prefix[..split], root, folders);
        var name = split <= 0 ? prefix : prefix.EndsWith(']') ? prefix[split..] : prefix[(split + 1)..];
        var created = new FolderState(null)
        {
            NodeId = new NodeId(prefix, (ushort)NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            ReferenceTypeId = new NodeId(35), // Organizes
            TypeDefinitionId = new NodeId(61), // FolderType
        };
        parent.AddChild(created);
        folders[prefix] = created;
        return created;
    }

    /// <summary>
    /// Always grant CurrentReadOrWrite to the caller: moz is a simulator with
    /// no access control (karakuri parity). This overrides any deny the
    /// session user context would apply to anonymous writes.
    /// </summary>
    private static ServiceResult OnEffectiveUserAccessLevel(
        ISystemContext context, NodeState node, ref byte userAccessLevel)
    {
        userAccessLevel = FullAccess;
        return ServiceResult.Good;
    }

    /// <summary>Device write → typed value → node Value + change masks (fires subscriptions).</summary>
    private void UpdateNode(BaseDataVariableState node, Variable v)
    {
        try
        {
            node.Value = UaValueCodec.Read(_variables, v);
            node.UpdateChangeMasks(NodeStateChangeMasks.Value);
            node.ClearChangeMasks(SystemContext, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "opcua node update failed for {Variable}", v.Name);
        }
    }

    /// <summary>
    /// The single write hook: resolves the variable by NodeId identifier
    /// (= the leaf variable name) and routes through VariableTable so moz's
    /// type/range/readonly rules surface as proper OPC UA status codes:
    /// wrong Variant type → BadTypeMismatch, off-range → BadOutOfRange,
    /// read-only table → BadNotWritable.
    /// </summary>
    private ServiceResult OnWriteVariable(
        ISystemContext context, NodeState node, ref Variant value)
    {
        var name = node.NodeId.TryGetValue(out string? id) ? id : "";
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
            UaValueCodec.Write(_variables, v, value);
            return ServiceResult.Good;
        }
        catch (FormatException ex)
        {
            return ServiceResult.Create(StatusCodes.BadTypeMismatch, ex.Message);
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
}
