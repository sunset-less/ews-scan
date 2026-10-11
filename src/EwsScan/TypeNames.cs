using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace EwsScan;

/// <summary>Turns the types in a metadata signature into names such as "System.Nullable`1&lt;Namespace.Type&gt;".</summary>
internal sealed class TypeNames : ISignatureTypeProvider<string, object?>
{
    public static readonly TypeNames Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

    private const int MaxNesting = 64;

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Definition(reader, handle, 0);

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Reference(reader, handle, 0);

    private static string Definition(MetadataReader reader, TypeDefinitionHandle handle, int depth)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        return declaring.IsNil
            ? Qualified(reader.GetString(type.Namespace), name)
            : $"{Definition(reader, declaring, Deeper(depth))}/{name}";
    }

    private static string Reference(MetadataReader reader, TypeReferenceHandle handle, int depth)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? $"{Reference(reader, (TypeReferenceHandle)type.ResolutionScope, Deeper(depth))}/{name}"
            : Qualified(reader.GetString(type.Namespace), name);
    }

    private static int Deeper(int depth) =>
        depth < MaxNesting ? depth + 1 : throw new BadImageFormatException("The chain of nested types is too deep or forms a loop.");

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        $"{genericType}<{string.Join(",", typeArguments)}>";

    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => elementType;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

    private static string Qualified(string @namespace, string name) => @namespace.Length == 0 ? name : $"{@namespace}.{name}";
}
