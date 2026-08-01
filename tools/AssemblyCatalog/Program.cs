using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: AssemblyCatalog <assembly> [regex]");
    return 2;
}

var filter = new Regex(args.Length > 1 ? args[1] : ".*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
var typeOnly = args.Any(a => a == "--types");
var namesOnly = args.Any(a => a == "--names");
var membersOnly = args.Any(a => a == "--members");
var matchingOnly = args.Any(a => a == "--matching");
using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var reader = pe.GetMetadataReader();
var signatureProvider = new SignatureNameProvider();

foreach (var handle in reader.TypeDefinitions)
{
    var type = reader.GetTypeDefinition(handle);
    var ns = reader.GetString(type.Namespace);
    var name = reader.GetString(type.Name);
    var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    if (namesOnly)
    {
        if (filter.IsMatch(fullName)) Console.WriteLine(fullName);
        continue;
    }
    var members = new List<string>();

    foreach (var fieldHandle in type.GetFields())
    {
        var field = reader.GetFieldDefinition(fieldHandle);
        var memberName = reader.GetString(field.Name);
        if (!memberName.StartsWith("Native", StringComparison.Ordinal))
            members.Add($"  F{(field.Attributes.HasFlag(FieldAttributes.Static) ? 'S' : 'I')} {field.DecodeSignature(signatureProvider, null)} {memberName}");
    }

    foreach (var propertyHandle in type.GetProperties())
    {
        var property = reader.GetPropertyDefinition(propertyHandle);
        var accessors = property.GetAccessors();
        var accessor = !accessors.Getter.IsNil ? accessors.Getter : accessors.Setter;
        var isStatic = !accessor.IsNil && reader.GetMethodDefinition(accessor).Attributes.HasFlag(MethodAttributes.Static);
        members.Add($"  P{(isStatic ? 'S' : 'I')} {property.DecodeSignature(signatureProvider, null).ReturnType} {reader.GetString(property.Name)}");
    }

    foreach (var methodHandle in type.GetMethods())
    {
        var method = reader.GetMethodDefinition(methodHandle);
        var memberName = reader.GetString(method.Name);
        if (memberName is not ".cctor" and not ".ctor" && !memberName.StartsWith("get_", StringComparison.Ordinal) && !memberName.StartsWith("set_", StringComparison.Ordinal))
        {
            var signature = method.DecodeSignature(signatureProvider, null);
            members.Add($"  M{(method.Attributes.HasFlag(MethodAttributes.Static) ? 'S' : 'I')} {signature.ReturnType} {memberName}({string.Join(", ", signature.ParameterTypes)})");
        }
    }

    if ((!membersOnly && filter.IsMatch(fullName)) || (!typeOnly && members.Any(filter.IsMatch)))
    {
        Console.WriteLine("TYPE " + fullName);
        foreach (var member in matchingOnly ? members.Where(member => filter.IsMatch(member)) : members)
            Console.WriteLine(member);
    }
}

return 0;

sealed class SignatureNameProvider : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
    public string GetByReferenceType(string elementType) => "ref " + elementType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(", ", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromSpecification(MetadataReader metadataReader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => metadataReader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetTypeFromDefinition(MetadataReader metadataReader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = metadataReader.GetTypeDefinition(handle);
        return Qualify(metadataReader.GetString(type.Namespace), metadataReader.GetString(type.Name));
    }

    public string GetTypeFromReference(MetadataReader metadataReader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = metadataReader.GetTypeReference(handle);
        return Qualify(metadataReader.GetString(type.Namespace), metadataReader.GetString(type.Name));
    }

    private static string Qualify(string ns, string name) => string.IsNullOrEmpty(ns) ? name : ns + "." + name;
}
