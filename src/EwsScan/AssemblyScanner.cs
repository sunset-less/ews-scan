using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace EwsScan;

internal sealed class MemberUse
{
    public int CallSites { get; set; }
    public SortedSet<string> Callers { get; } = new(StringComparer.Ordinal);
}

/// <summary>What one assembly uses from the EWS Managed API.</summary>
internal sealed class AssemblyUsage
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Library { get; init; }

    /// <summary>Methods and fields, as "ExchangeService.FindItems".</summary>
    public SortedDictionary<string, MemberUse> Members { get; } = new(StringComparer.Ordinal);

    public SortedSet<string> Types { get; } = new(StringComparer.Ordinal);

    /// <summary>Enum values passed as literals, as "WellKnownFolderName.PublicFoldersRoot".</summary>
    public SortedDictionary<string, MemberUse> Constants { get; } = new(StringComparer.Ordinal);
}

/// <summary>Reads the IL of an assembly and collects its calls into the EWS Managed API.</summary>
internal sealed partial class AssemblyScanner(Catalog catalog)
{
    private const string EwsNamespace = "Microsoft.Exchange.WebServices";
    private const string DataNamespace = EwsNamespace + ".Data";
    private static readonly byte[] MicrosoftKeyToken = [0x31, 0xbf, 0x38, 0x56, 0xad, 0x36, 0x4e, 0x35];
    private static readonly byte[] SunsetlessKeyToken = [0x1b, 0x8c, 0x25, 0xc6, 0xfc, 0x94, 0xdc, 0xf1];

    private static readonly OpCode?[] OneByte = new OpCode?[256];
    private static readonly OpCode?[] TwoByte = new OpCode?[256];

    private readonly string[] trackedEnums = catalog.TrackedEnums.ToArray();

    static AssemblyScanner()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode op)
            {
                (op.Size == 1 ? OneByte : TwoByte)[op.Value & 0xFF] = op;
            }
        }
    }

    /// <summary>Returns null for a file that is not a .NET assembly, does not use the EWS Managed API, or is that library itself.</summary>
    public AssemblyUsage? Scan(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return null;
            }
            var reader = pe.GetMetadataReader();
            return reader.IsAssembly ? Scan(path, pe, reader) : null;
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private AssemblyUsage? Scan(string path, PEReader pe, MetadataReader reader)
    {
        if (reader.TypeDefinitions.Select(reader.GetTypeDefinition).Any(type =>
                reader.StringComparer.Equals(type.Name, "ExchangeService") && reader.StringComparer.Equals(type.Namespace, DataNamespace)))
        {
            return null;
        }

        var types = new SortedSet<string>(StringComparer.Ordinal);
        AssemblyReferenceHandle libraryHandle = default;
        foreach (var handle in reader.TypeReferences)
        {
            var type = reader.GetTypeReference(handle);
            if (!IsEws(reader.GetString(type.Namespace)))
            {
                continue;
            }
            types.Add(reader.GetString(type.Name));
            if (type.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                libraryHandle = (AssemblyReferenceHandle)type.ResolutionScope;
            }
        }
        if (types.Count == 0 || libraryHandle.IsNil)
        {
            return null;
        }

        var library = reader.GetAssemblyReference(libraryHandle);
        var libraryName = reader.GetString(library.Name);
        var token = reader.GetBlobBytes(library.PublicKeyOrToken);
        var fromSunsetless = token.AsSpan().SequenceEqual(SunsetlessKeyToken);
        var usage = new AssemblyUsage
        {
            Path = path,
            Name = reader.GetString(reader.GetAssemblyDefinition().Name),
            Library = $"{libraryName} {library.Version}{(fromSunsetless ? " (Sunsetless EWS)" : "")}",
        };
        usage.Types.UnionWith(types);

        var walk = new Walk(reader, usage, EnumNames(System.IO.Path.GetDirectoryName(path)!, libraryName, Numbering(libraryName, token)), trackedEnums);
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }
            var caller = CallerName(
                TypeNames.Instance.GetTypeFromDefinition(reader, method.GetDeclaringType(), 0),
                reader.GetString(method.Name));
            try
            {
                walk.Method(pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader(), caller);
            }
            catch (BadImageFormatException)
            {
            }
        }
        return usage;
    }

    /// <summary>Which enum numbering the library uses, by its public key token: "microsoft", "sunsetless" or "other".</summary>
    internal static string Numbering(string libraryName, byte[] publicKeyToken)
    {
        if (publicKeyToken.AsSpan().SequenceEqual(MicrosoftKeyToken))
        {
            return "microsoft";
        }

        // Sunsetless.Ews numbers like Microsoft's package and adds the names the public source has; the .NET Standard port keeps the port's numbers.
        return publicKeyToken.AsSpan().SequenceEqual(SunsetlessKeyToken) && libraryName == EwsNamespace ? "sunsetless" : "other";
    }

    private static bool IsEws(string @namespace) =>
        @namespace == EwsNamespace || @namespace.StartsWith(EwsNamespace + ".", StringComparison.Ordinal);

    /// <summary>"Sync.Mailbox/&lt;RunAsync&gt;d__4" and "MoveNext" give "Sync.Mailbox.RunAsync".</summary>
    internal static string CallerName(string type, string method)
    {
        var parts = type.Split('/');
        var owner = string.Join('.', parts.TakeWhile(part => !part.StartsWith('<')));
        var source = GeneratedName().Match(method);
        if (!source.Success)
        {
            source = parts.Select(part => GeneratedName().Match(part)).LastOrDefault(match => match.Success) ?? Match.Empty;
        }
        return $"{(owner.Length > 0 ? owner : parts[0])}.{(source.Success ? source.Groups[1].Value : method)}";
    }

    // The compiler names lambdas, local functions and state machines after their method: <Run>b__0, <Run>d__3.
    [GeneratedRegex(@"^<+([^<>]+)>")]
    private static partial Regex GeneratedName();

    /// <summary>The names of the tracked enum values, read from the EWS library next to the assembly when it is there.</summary>
    private Dictionary<string, Dictionary<int, string>> EnumNames(string folder, string libraryName, string numbering)
    {
        var names = new Dictionary<string, Dictionary<int, string>>();
        var file = System.IO.Path.Combine(folder, libraryName + ".dll");
        if (File.Exists(file))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                var reader = pe.GetMetadataReader();
                foreach (var type in reader.TypeDefinitions.Select(reader.GetTypeDefinition))
                {
                    var name = reader.GetString(type.Name);
                    if (!trackedEnums.Contains(name) || reader.GetString(type.Namespace) != DataNamespace)
                    {
                        continue;
                    }
                    var values = names[name] = [];
                    foreach (var field in type.GetFields().Select(reader.GetFieldDefinition))
                    {
                        var constant = field.GetDefaultValue();
                        if (!constant.IsNil && reader.GetConstant(constant) is { TypeCode: ConstantTypeCode.Int32 } value)
                        {
                            values[reader.GetBlobReader(value.Value).ReadInt32()] = reader.GetString(field.Name);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                names.Clear();
            }
        }

        // Microsoft's package, Sunsetless.Ews and the builds made from the public source number WellKnownFolderName differently.
        foreach (var (name, variants) in catalog.EnumFallback)
        {
            if (!names.ContainsKey(name))
            {
                var list = variants.TryGetValue(numbering, out var own) ? own : variants["other"];
                names[name] = list.Select((value, index) => (value, index)).ToDictionary(pair => pair.index, pair => pair.value);
            }
        }
        return names;
    }

    private sealed record Target(string? Member, int Parameters, bool Instance, bool Returns, string?[] Enums, bool PassesArgumentThrough);

    /// <summary>Follows the evaluation stack through each method far enough to see literal enum arguments.</summary>
    private sealed class Walk(MetadataReader reader, AssemblyUsage usage, Dictionary<string, Dictionary<int, string>> enumNames, string[] trackedEnums)
    {
        private static readonly Target Unknown = new(null, 0, false, false, [], false);

        private readonly Dictionary<EntityHandle, Target> targets = [];
        private readonly List<int?> stack = [];
        private readonly Dictionary<int, int?> locals = [];

        public void Method(BlobReader il, string caller)
        {
            stack.Clear();
            locals.Clear();
            while (il.RemainingBytes > 0)
            {
                int first = il.ReadByte();
                if ((first == 0xFE ? TwoByte[il.ReadByte()] : OneByte[first]) is not { } op)
                {
                    return;
                }
                var operand = Operand(ref il, op);
                var name = op.Name!;

                if (name.StartsWith("ldc.i4", StringComparison.Ordinal))
                {
                    stack.Add(name switch { "ldc.i4" or "ldc.i4.s" => operand, "ldc.i4.m1" => -1, _ => name[^1] - '0' });
                }
                else if (name.StartsWith("ldloc", StringComparison.Ordinal) && !name.StartsWith("ldloca", StringComparison.Ordinal))
                {
                    stack.Add(locals.GetValueOrDefault(LocalIndex(name, operand)));
                }
                else if (name.StartsWith("stloc", StringComparison.Ordinal))
                {
                    locals[LocalIndex(name, operand)] = Pop(1)[0];
                }
                else if (op == OpCodes.Dup)
                {
                    stack.Add(stack.Count > 0 ? stack[^1] : null);
                }
                else if (op.OperandType == OperandType.InlineMethod)
                {
                    Call(op, MetadataTokens.EntityHandle(operand), caller);
                }
                else if (op == OpCodes.Calli)
                {
                    var signature = reader.GetStandaloneSignature((StandaloneSignatureHandle)MetadataTokens.EntityHandle(operand))
                        .DecodeMethodSignature(TypeNames.Instance, null);
                    Pop(signature.ParameterTypes.Length + 1 + (signature.Header.IsInstance ? 1 : 0));
                    Push(signature.ReturnType != "Void" ? 1 : 0);
                }
                else
                {
                    if (op.OperandType == OperandType.InlineField && FieldMember(MetadataTokens.EntityHandle(operand)) is { } field)
                    {
                        Record(usage.Members, field, caller);
                    }
                    Pop(Pops(op.StackBehaviourPop));
                    Push(Pushes(op.StackBehaviourPush));
                }

                if (op.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw)
                {
                    stack.Clear();
                }
            }
        }

        private void Call(OpCode op, EntityHandle handle, string caller)
        {
            var target = Resolve(handle);
            if (target.Member is { } member)
            {
                Record(usage.Members, member, caller);
            }
            if (op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn || op == OpCodes.Jmp)
            {
                Pop(op == OpCodes.Ldvirtftn ? 1 : 0);
                Push(op == OpCodes.Jmp ? 0 : 1);
                return;
            }

            var creates = op == OpCodes.Newobj;
            var arguments = Pop(target.Parameters + (target.Instance && !creates ? 1 : 0));
            var first = arguments.Length - target.Parameters;
            for (var index = 0; index < target.Parameters; index++)
            {
                if (target.Enums[index] is { } enumName
                    && arguments[first + index] is { } value
                    && enumNames.GetValueOrDefault(enumName)?.GetValueOrDefault(value) is { } valueName)
                {
                    Record(usage.Constants, $"{enumName}.{valueName}", caller);
                }
            }
            if (creates || target.Returns)
            {
                stack.Add(target.PassesArgumentThrough && arguments.Length > 0 ? arguments[^1] : null);
            }
        }

        private Target Resolve(EntityHandle handle)
        {
            if (targets.TryGetValue(handle, out var known))
            {
                return known;
            }
            Target target;
            switch (handle.Kind)
            {
                case HandleKind.MethodSpecification:
                    target = Resolve(reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
                    break;
                case HandleKind.MemberReference:
                    var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
                    target = reference.GetKind() == MemberReferenceKind.Method
                        ? Build(OwnerName(reference.Parent), reader.GetString(reference.Name), reference.DecodeMethodSignature(TypeNames.Instance, null))
                        : Unknown;
                    break;
                case HandleKind.MethodDefinition:
                    var definition = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    target = Build(null, reader.GetString(definition.Name), definition.DecodeSignature(TypeNames.Instance, null));
                    break;
                default:
                    target = Unknown;
                    break;
            }
            return targets[handle] = target;
        }

        private Target Build(string? owner, string name, MethodSignature<string> signature)
        {
            var enums = signature.ParameterTypes.Select(TrackedEnum).ToArray();
            // new Nullable<T>(value) keeps the value: optional enum parameters are passed this way.
            var nullable = owner == "System.Nullable`1" && name == ".ctor";
            return new Target(
                owner is not null && IsEws(NamespaceOf(owner)) ? $"{SimpleName(owner)}.{name}" : null,
                signature.ParameterTypes.Length,
                signature.Header.IsInstance,
                signature.ReturnType != "Void",
                enums,
                nullable);
        }

        private string? TrackedEnum(string parameterType)
        {
            foreach (var name in trackedEnums)
            {
                var full = $"{DataNamespace}.{name}";
                if (parameterType == full || parameterType == $"System.Nullable`1<{full}>")
                {
                    return name;
                }
            }
            return null;
        }

        private string? FieldMember(EntityHandle handle)
        {
            if (handle.Kind != HandleKind.MemberReference)
            {
                return null;
            }
            var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
            return OwnerName(reference.Parent) is { } owner && IsEws(NamespaceOf(owner))
                ? $"{SimpleName(owner)}.{reader.GetString(reference.Name)}"
                : null;
        }

        /// <summary>The declaring type without generic arguments, or null when the member belongs to this assembly.</summary>
        private string? OwnerName(EntityHandle parent)
        {
            var name = parent.Kind switch
            {
                HandleKind.TypeReference => TypeNames.Instance.GetTypeFromReference(reader, (TypeReferenceHandle)parent, 0),
                HandleKind.TypeSpecification => TypeNames.Instance.GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)parent, 0),
                _ => null,
            };
            var generic = name?.IndexOf('<') ?? -1;
            return generic < 0 ? name : name![..generic];
        }

        private static string NamespaceOf(string type) => type.LastIndexOf('.') is var dot and >= 0 ? type[..dot] : "";

        private static string SimpleName(string type) => type[(type.LastIndexOf('.') + 1)..];

        private static void Record(SortedDictionary<string, MemberUse> uses, string key, string caller)
        {
            if (!uses.TryGetValue(key, out var use))
            {
                uses[key] = use = new MemberUse();
            }
            use.CallSites++;
            use.Callers.Add(caller);
        }

        private static int LocalIndex(string name, int operand) => char.IsDigit(name[^1]) ? name[^1] - '0' : operand;

        private int?[] Pop(int count)
        {
            var values = new int?[count];
            for (var index = count - 1; index >= 0 && stack.Count > 0; index--)
            {
                values[index] = stack[^1];
                stack.RemoveAt(stack.Count - 1);
            }
            return values;
        }

        private void Push(int count)
        {
            for (var index = 0; index < count; index++)
            {
                stack.Add(null);
            }
        }

        private static int Operand(ref BlobReader il, OpCode op)
        {
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineBrTarget:
                    return il.ReadSByte();
                case OperandType.ShortInlineVar:
                    return il.ReadByte();
                case OperandType.InlineVar:
                    return il.ReadUInt16();
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    il.ReadInt64();
                    return 0;
                case OperandType.InlineSwitch:
                    il.Offset += 4 * (int)il.ReadUInt32();
                    return 0;
                default:
                    return il.ReadInt32();
            }
        }

        private static int Pops(StackBehaviour behaviour) => behaviour switch
        {
            StackBehaviour.Pop0 or StackBehaviour.Varpop => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
                or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            _ => 3,
        };

        private static int Pushes(StackBehaviour behaviour) => behaviour switch
        {
            StackBehaviour.Push0 or StackBehaviour.Varpush => 0,
            StackBehaviour.Push1_push1 => 2,
            _ => 1,
        };
    }
}
