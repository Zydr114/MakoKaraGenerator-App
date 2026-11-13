using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

/// <summary>
/// M13 G3：SDK 2.0 冻结。作者可见表面 = <c>MakoKaraGenerator.Scripting.V2</c> 的公开类型，
/// 加上它们公开／受保护成员签名可达的项目内公开类型（如 Affine2、MaterialValue、ShadowValue）。
/// 任何类型或成员的增删改都会让本测试失败：须先升 SDK 版本（包 sdkVersion 与准入），
/// 再用 <c>MAKO_UPDATE_SDK_SURFACE=1</c> 重写快照，并在提交里说明表面变化。
/// </summary>
public sealed class SdkV2PublicSurfaceSnapshotTests
{
    private const string UpdateVariable = "MAKO_UPDATE_SDK_SURFACE";
    private static readonly NullabilityInfoContext Nullability = new();

    [Fact]
    public void SDK2公开表面与冻结快照一致()
    {
        string path = SnapshotPath();
        string actual = Render();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }
        Assert.True(File.Exists(path), "缺少 SDK 表面快照：" + path);
        string expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        string[] want = expected.Split('\n'), have = actual.Split('\n');
        var removed = want.Except(have, StringComparer.Ordinal).Take(20).ToArray();
        var added = have.Except(want, StringComparer.Ordinal).Take(20).ToArray();
        Assert.True(expected == actual, "SDK 2.0 已冻结（M13）：作者可见表面变化须升 SDK 版本，再以 " + UpdateVariable
            + "=1 重写快照。\n移除：\n  " + string.Join("\n  ", removed) + "\n新增：\n  " + string.Join("\n  ", added));
    }

    [Fact]
    public void 快照所记SDK版本与当前包准入的主次版本一致()
    {
        var version = Version.Parse(EffectPackageProfiles.V2.SdkVersion);
        Assert.Equal(Header(), File.ReadLines(SnapshotPath()).First());
        Assert.Equal((2, 0), (version.Major, version.Minor));
    }

    private static string SnapshotPath([CallerFilePath] string source = "")
        => Path.Combine(Path.GetDirectoryName(source)!, "Snapshots", "sdk-v2-public-surface.txt");

    private static string Header()
    {
        var version = Version.Parse(EffectPackageProfiles.V2.SdkVersion);
        return string.Create(CultureInfo.InvariantCulture, $"# SDK {version.Major}.{version.Minor} author surface");
    }

    private static string Render()
    {
        var assembly = typeof(TextOutput).Assembly;
        var pending = new Queue<Type>(assembly.GetExportedTypes().Where(static type => type.Namespace == "MakoKaraGenerator.Scripting.V2"));
        var seen = new HashSet<Type>();
        var blocks = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var type))
        {
            if (!seen.Add(type)) continue;
            var references = new List<Type>();
            var lines = Describe(type, references);
            blocks[TypeName(type)] = lines;
            foreach (var reference in references.SelectMany(Expand))
                if (reference.Assembly.GetName().Name?.StartsWith("MakoKaraGenerator.", StringComparison.Ordinal) == true
                    && reference.IsVisible && !reference.IsGenericParameter && !seen.Contains(reference))
                    pending.Enqueue(reference.IsGenericType ? reference.GetGenericTypeDefinition() : reference);
        }
        var builder = new StringBuilder().Append(Header()).Append('\n');
        foreach (var (_, lines) in blocks)
            foreach (string line in lines) builder.Append(line).Append('\n');
        return builder.ToString();
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.HasElementType) { foreach (var item in Expand(type.GetElementType()!)) yield return item; yield break; }
        yield return type;
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                foreach (var item in Expand(argument)) yield return item;
    }

    private static List<string> Describe(Type type, List<Type> references)
    {
        string kind = type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct"
            : typeof(Delegate).IsAssignableFrom(type) ? "delegate" : "class";
        string modifiers = type.IsAbstract && type.IsSealed ? "static " : type.IsInterface ? "" : type.IsAbstract ? "abstract "
            : type.IsSealed && !type.IsValueType ? "sealed " : "";
        var head = new StringBuilder("type ").Append(modifiers).Append(kind).Append(' ').Append(TypeName(type));
        var bases = new List<Type>();
        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum)) bases.Add(baseType);
        bases.AddRange(type.GetInterfaces().Where(static item => item.IsVisible).OrderBy(TypeName, StringComparer.Ordinal));
        if (bases.Count > 0) head.Append(" : ").Append(string.Join(", ", bases.Select(TypeName)));
        references.AddRange(bases);
        var members = new List<string>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        if (type.IsEnum)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                members.Add("  value " + field.Name + " = " + Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture));
        }
        else
        {
            foreach (var constructor in type.GetConstructors(flags).Where(static item => Visible(item)))
                members.Add("  ctor " + Access(constructor) + "(" + Parameters(constructor, references) + ")");
            foreach (var method in type.GetMethods(flags).Where(static item => Visible(item) && (!item.IsSpecialName || item.Name.StartsWith("op_", StringComparison.Ordinal))))
            {
                references.Add(method.ReturnType);
                string generic = method.IsGenericMethodDefinition ? "<" + string.Join(", ", method.GetGenericArguments().Select(static item => item.Name)) + ">" : "";
                members.Add("  method " + Access(method) + Modifiers(method) + Annotated(method.ReturnType, Nullability.Create(method.ReturnParameter))
                    + " " + method.Name + generic + "(" + Parameters(method, references) + ")");
            }
            foreach (var property in type.GetProperties(flags).Where(static item => Visible(item.GetMethod) || Visible(item.SetMethod)))
            {
                references.Add(property.PropertyType);
                string indexer = property.GetIndexParameters() is { Length: > 0 } index
                    ? "[" + string.Join(", ", index.Select(item => Annotated(item.ParameterType, Nullability.Create(item)))) + "]" : "";
                references.AddRange(property.GetIndexParameters().Select(static item => item.ParameterType));
                var accessors = new List<string>();
                if (Visible(property.GetMethod)) accessors.Add(Access(property.GetMethod!) + "get;");
                if (Visible(property.SetMethod))
                    accessors.Add(Access(property.SetMethod!) + (property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) ? "init;" : "set;"));
                var accessor = property.GetMethod ?? property.SetMethod!;
                members.Add("  property " + Modifiers(accessor) + Annotated(property.PropertyType, Nullability.Create(property)) + " "
                    + property.Name + indexer + " { " + string.Join(' ', accessors) + " }");
            }
            foreach (var field in type.GetFields(flags).Where(static item => item.IsPublic || item.IsFamily || item.IsFamilyOrAssembly))
            {
                references.Add(field.FieldType);
                string value = field.IsLiteral ? " = " + Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture) : "";
                members.Add("  field " + Access(field) + (field.IsLiteral ? "const " : field.IsStatic ? "static " : "")
                    + (field.IsInitOnly ? "readonly " : "") + Annotated(field.FieldType, Nullability.Create(field)) + " " + field.Name + value);
            }
            foreach (var evt in type.GetEvents(flags).Where(static item => Visible(item.AddMethod)))
            {
                references.Add(evt.EventHandlerType!);
                members.Add("  event " + TypeName(evt.EventHandlerType!) + " " + evt.Name);
            }
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(static item => item.IsNestedPublic || item.IsNestedFamily || item.IsNestedFamORAssem))
                references.Add(nested);
        }
        members.Sort(StringComparer.Ordinal);
        return [head.ToString(), .. members];
    }

    private static bool Visible(MethodBase? method) => method is not null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);
    private static string Access(MethodBase method) => method.IsPublic ? "" : "protected ";
    private static string Access(FieldInfo field) => field.IsPublic ? "" : "protected ";
    private static string Modifiers(MethodInfo method) => method.IsStatic ? "static " : method.IsAbstract ? "abstract "
        : method.IsVirtual && !method.IsFinal && method.DeclaringType?.IsInterface != true ? "virtual " : "";

    private static string Parameters(MethodBase method, List<Type> references) => string.Join(", ", method.GetParameters().Select(parameter =>
    {
        references.Add(parameter.ParameterType);
        var type = parameter.ParameterType;
        string prefix = type.IsByRef ? parameter.IsIn ? "in " : parameter.IsOut ? "out " : "ref " : "";
        if (parameter.IsDefined(typeof(ParamArrayAttribute))) prefix += "params ";
        string text = prefix + Annotated(type.IsByRef ? type.GetElementType()! : type, Nullability.Create(parameter)) + " " + parameter.Name;
        if (parameter.HasDefaultValue)
            text += " = " + (parameter.RawDefaultValue is null ? "null" : Convert.ToString(parameter.RawDefaultValue, CultureInfo.InvariantCulture));
        return text;
    }));

    private static string Annotated(Type type, NullabilityInfo info)
        => TypeName(type) + (!type.IsValueType && info.ReadState == NullabilityState.Nullable ? "?" : "");

    private static string TypeName(Type type)
    {
        if (type.IsGenericParameter) return type.Name;
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsByRef) return TypeName(type.GetElementType()!) + "&";
        if (Nullable.GetUnderlyingType(type) is { } underlying) return TypeName(underlying) + "?";
        string name = type.IsNested ? TypeName(type.DeclaringType!) + "." + type.Name : type.Namespace + "." + type.Name;
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0) name = name[..tick];
        if (!type.IsGenericType) return name;
        var arguments = type.GetGenericArguments();
        if (type.IsNested && type.DeclaringType!.IsGenericType) arguments = arguments[type.DeclaringType.GetGenericArguments().Length..];
        return arguments.Length == 0 ? name : name + "<" + string.Join(", ", arguments.Select(TypeName)) + ">";
    }
}
