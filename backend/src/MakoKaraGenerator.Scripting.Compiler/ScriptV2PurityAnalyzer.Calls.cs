using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace MakoKaraGenerator.Scripting.Compilation;

public static partial class ScriptV2PurityAnalyzer
{
    private sealed partial class Analysis
    {
        private static bool IsSdkCall(IMethodSymbol method)
            => IsTrustedSdk(method.ContainingType) && (IsStageHandle(method.ContainingType) || IsSdkValue(method.ContainingType));

        private static bool Forbidden(IMethodSymbol method)
        {
            string ns = method.ContainingNamespace.ToDisplayString(), name = method.ContainingType.Name;
            // BCL strings, synthesized records and default comparers may use process-randomized hash salts.
            if (method.Name == "GetHashCode") return true;
            if (method.IsExtern || method.GetDllImportData() is not null) return true;
            if (ns.StartsWith("System.IO", StringComparison.Ordinal) || ns.StartsWith("System.Net", StringComparison.Ordinal)
                || ns.StartsWith("System.Threading", StringComparison.Ordinal) || ns.StartsWith("System.Reflection", StringComparison.Ordinal)
                || ns.StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal) || ns.StartsWith("System.Diagnostics", StringComparison.Ordinal)
                || ns.StartsWith("Microsoft.Win32", StringComparison.Ordinal)) return true;
            return ns == "System" && (name is "Environment" or "Random" or "Console" or "AppContext" or "GC" or "Activator" or "Type"
                or "OperatingSystem" or "TimeProvider" or "Delegate" or "MulticastDelegate"
                || name is ("DateTime" or "DateTimeOffset") && method.Name is ("get_Now" or "get_UtcNow" or "get_Today")
                || name == "Guid" && method.Name == "NewGuid" || method.ContainingType.SpecialType == SpecialType.System_Object && method.Name is ("GetType" or "GetHashCode"))
                || ns.StartsWith("System.Globalization", StringComparison.Ordinal) && method.Name.Contains("Current", StringComparison.Ordinal);
        }

        private static bool ForbiddenField(IFieldSymbol field)
            => field.ContainingType.ContainingNamespace.ToDisplayString().StartsWith("System.Reflection", StringComparison.Ordinal)
                || field.ContainingType.Name is "IntPtr" or "UIntPtr" or "RuntimeTypeHandle" or "RuntimeMethodHandle"
                || field.ContainingType.Name == "StringComparison" && field.Name.Contains("CurrentCulture", StringComparison.Ordinal);

        private static bool MutableCollection(INamedTypeSymbol type)
        {
            string ns = type.ContainingNamespace.ToDisplayString();
            return ns == "System.Collections.Generic" && type.Name is "List" or "Dictionary" or "HashSet" or "Queue" or "Stack" or "SortedDictionary" or "SortedSet"
                || ns == "System.Collections.Immutable" && type.Name == "Builder";
        }

        private static bool AllowedBcl(IMethodSymbol method, IOperation? instance, ImmutableArray<IArgumentOperation> arguments)
        {
            INamedTypeSymbol type = method.ContainingType;
            string ns = type.ContainingNamespace.ToDisplayString(), name = type.Name;
            if (ns == "System.Linq") return name == "Enumerable"
                && (!method.Name.StartsWith("Order", StringComparison.Ordinal) && !method.Name.StartsWith("ThenBy", StringComparison.Ordinal)
                    || method.Parameters.Any(static parameter => parameter.Name == "comparer"));
            if (ns == "System.Collections.Immutable" || ns == "System.Collections.Frozen")
                return method.Name is not ("Sort" or "BinarySearch")
                    || method.Parameters.Any(static parameter => parameter.Name is "comparer" or "comparison");
            if (ns == "System.Numerics" && IsBclValue(type)) return true;
            if (ns == "System.Collections.Generic" && name is "KeyValuePair" or "EqualityComparer" or "Comparer")
                return method.Name is not "get_Default" || name == "EqualityComparer";
            if (ns != "System") return false;
            if (name is "Math" or "MathF" or "Tuple" or "ValueTuple" or "Nullable" or "Index" or "Range" or "TimeSpan") return true;
            if (name is "DateTime" or "DateTimeOffset") return method.Name is not "ToLocalTime" && method.Name is not "ToUniversalTime";
            if (name == "Guid") return method.Name is not "NewGuid";
            if (name == "StringComparer") return method.Name is "get_Ordinal" or "get_OrdinalIgnoreCase" or "Equals" or "Compare";
            if (name is "ArgumentException" or "ArgumentNullException" or "ArgumentOutOfRangeException" or "InvalidOperationException"
                or "NotSupportedException" or "KeyNotFoundException" or "Exception" or "ObjectDisposedException") return true;
            if (type.SpecialType == SpecialType.System_Object) return method.MethodKind == MethodKind.Constructor || method.Name == "ReferenceEquals";
            if (name == "Array") return method.Name is "Empty" or "Copy" or "Reverse" or "Clear" or "Fill" or "IndexOf" or "LastIndexOf";
            if (type.SpecialType == SpecialType.System_String)
            {
                if (method.MethodKind == MethodKind.PropertyGet || method.MethodKind == MethodKind.Constructor) return true;
                if (method.Name is "GetHashCode" or "ToLower" or "ToUpper" or "CompareTo" or "Format") return false;
                if (method.Name is "Compare" or "StartsWith" or "EndsWith" or "IndexOf" or "LastIndexOf" or "Contains")
                    return method.Parameters.Any(static parameter => parameter.Type.Name == "StringComparison")
                        || method.Parameters.Any(static parameter => parameter.Type.SpecialType == SpecialType.System_Char);
                return method.Name is "Concat" or "Join" or "Equals" or "IsNullOrEmpty" or "IsNullOrWhiteSpace" or "Substring"
                    or "ToLowerInvariant" or "ToUpperInvariant" or "Replace" or "Trim" or "TrimStart" or "TrimEnd" or "Split"
                    or "ToCharArray" or "Insert" or "Remove" or "PadLeft" or "PadRight" or "CopyTo" or "GetEnumerator";
            }
            if (type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum)
                return method.Name is not "ToString" and not "Parse" and not "TryParse" and not "GetHashCode";
            _ = instance; _ = arguments;
            return false;
        }
    }
}
