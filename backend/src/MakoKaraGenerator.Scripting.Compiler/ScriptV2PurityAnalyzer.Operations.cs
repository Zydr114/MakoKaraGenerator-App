using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace MakoKaraGenerator.Scripting.Compilation;

public static partial class ScriptV2PurityAnalyzer
{
    private sealed partial class Analysis
    {
        private void CheckMethod(IMethodSymbol method, bool evaluating, ImmutableArray<int> ownedParameters, Location callLocation)
        {
            _token.ThrowIfCancellationRequested();
            method = method.ReducedFrom ?? method;
            string key = method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "|" + evaluating + "|" + string.Join(',', ownedParameters);
            if (!_visitedMethods.Add(key)) return;
            if (method.ContainingType is { } owner && owner.Locations.Any(static value => value.IsInSource)) CheckStaticState(owner);
            if (method.IsAsync || method.IsExtern || method.GetDllImportData() is not null)
                Report(UnsupportedLanguage, SourceLocation(method), method.ToDisplayString());
            foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
            {
                SyntaxNode declaration = reference.GetSyntax(_token);
                if (declaration.DescendantTokens().Any(static value => value.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.UnsafeKeyword)))
                    Report(UnsupportedLanguage, declaration.GetLocation(), "unsafe");
                SemanticModel model = GetSemanticModel(declaration.SyntaxTree);
                IOperation? operation = model.GetOperation(declaration, _token);
                if (operation is null)
                {
                    SyntaxNode? body = declaration switch
                    {
                        BaseMethodDeclarationSyntax value => (SyntaxNode?)value.Body ?? value.ExpressionBody?.Expression,
                        AccessorDeclarationSyntax value => (SyntaxNode?)value.Body ?? value.ExpressionBody?.Expression,
                        PropertyDeclarationSyntax value => value.ExpressionBody?.Expression,
                        _ => null,
                    };
                    if (body is not null) operation = model.GetOperation(body, _token);
                }
                if (operation is not null) new Walker(this, method, evaluating, ownedParameters).Visit(operation);
                else if (!method.IsImplicitlyDeclared && declaration is not AccessorDeclarationSyntax { Body: null, ExpressionBody: null }
                    && declaration is not RecordDeclarationSyntax && declaration is not ParameterSyntax)
                    Report(Unsupported, callLocation, method.ToDisplayString());
            }
        }

        private sealed class Walker : OperationWalker
        {
            private readonly Analysis _analysis;
            private readonly IMethodSymbol? _method;
            private readonly bool _evaluating;
            private readonly HashSet<ISymbol> _owned = new(SymbolEqualityComparer.Default);
            private readonly HashSet<ISymbol> _handleAliases = new(SymbolEqualityComparer.Default);
            private readonly HashSet<ISymbol> _knownInterfaceValues = new(SymbolEqualityComparer.Default);
            private readonly Dictionary<ISymbol, IOperation> _delegates = new(SymbolEqualityComparer.Default);

            internal Walker(Analysis analysis, IMethodSymbol? method, bool evaluating, ImmutableArray<int> ownedParameters)
            {
                _analysis = analysis; _method = method; _evaluating = evaluating;
                if (method is not null)
                    foreach (int index in ownedParameters) _owned.Add(method.Parameters[index]);
            }

            public override void Visit(IOperation? operation)
            {
                _analysis._token.ThrowIfCancellationRequested();
                base.Visit(operation);
            }

            public override void VisitDefaultValue(IDefaultValueOperation operation)
            {
                if (IsStageHandle(operation.Type)) Report(Capability, operation, "宿主阶段句柄 default " + operation.Type!.Name);
                base.VisitDefaultValue(operation);
            }

            public override void VisitVariableDeclarator(IVariableDeclaratorOperation operation)
            {
                // A managed reference can alias an instance field or array element;
                // treating it as an ordinary local would admit cross-frame writes.
                if (operation.Symbol.RefKind != RefKind.None)
                    Report(Mutation, operation, "ref 局部变量 " + operation.Symbol.Name);
                IOperation? value = operation.Initializer?.Value;
                if (value is not null)
                {
                    if (Owned(value)) _owned.Add(operation.Symbol);
                    if (ContainsHandle(value)) _handleAliases.Add(operation.Symbol);
                    if (KnownMetadataInput(value)) _knownInterfaceValues.Add(operation.Symbol);
                    if (IsDelegate(value)) _delegates[operation.Symbol] = value;
                }
                base.VisitVariableDeclarator(operation);
            }

            public override void VisitSimpleAssignment(ISimpleAssignmentOperation operation)
            {
                CheckWrite(operation.Target, operation.Value);
                if (operation.Target is ILocalReferenceOperation local)
                {
                    if (Owned(operation.Value)) _owned.Add(local.Local); else _owned.Remove(local.Local);
                    if (ContainsHandle(operation.Value)) _handleAliases.Add(local.Local); else _handleAliases.Remove(local.Local);
                    if (KnownMetadataInput(operation.Value)) _knownInterfaceValues.Add(local.Local); else _knownInterfaceValues.Remove(local.Local);
                    if (IsDelegate(operation.Value)) _delegates[local.Local] = operation.Value; else _delegates.Remove(local.Local);
                }
                base.VisitSimpleAssignment(operation);
            }

            public override void VisitCompoundAssignment(ICompoundAssignmentOperation operation)
            {
                CheckWrite(operation.Target, operation.Value);
                if (operation.OperatorMethod is { } method) CheckCall(method, null, [], operation);
                base.VisitCompoundAssignment(operation);
            }

            public override void VisitIncrementOrDecrement(IIncrementOrDecrementOperation operation)
            {
                CheckWrite(operation.Target, null);
                if (operation.OperatorMethod is { } method) CheckCall(method, null, [], operation);
                base.VisitIncrementOrDecrement(operation);
            }

            public override void VisitDeconstructionAssignment(IDeconstructionAssignmentOperation operation)
            {
                CheckWrite(operation.Target, operation.Value);
                base.VisitDeconstructionAssignment(operation);
            }

            private void CheckWrite(IOperation target, IOperation? value)
            {
                target = Unwrap(target);
                bool allowed = target switch
                {
                    ILocalReferenceOperation local => Same(local.Local.ContainingSymbol, _method),
                    IParameterReferenceOperation parameter => parameter.Parameter.RefKind == RefKind.None && Same(parameter.Parameter.ContainingSymbol, _method),
                    IDiscardOperation => true,
                    ITupleOperation tuple => tuple.Elements.All(WriteAllowed),
                    IArrayElementReferenceOperation array => Owned(array.ArrayReference),
                    IFieldReferenceOperation field => !field.Field.IsStatic && (Owned(field.Instance) || ConstructorWrites(field.Field)),
                    IPropertyReferenceOperation property => !property.Property.IsStatic && (Owned(property.Instance) || NewInitializer(property)),
                    _ => false,
                };
                if (!allowed) Report(Mutation, target, target.Syntax.ToString());
                if (target is IPropertyReferenceOperation { Property.SetMethod: { } setter } assignedProperty)
                    CheckCall(setter, assignedProperty.Instance, assignedProperty.Arguments, target);
                if (value is not null && ContainsHandle(value) && target is not (ILocalReferenceOperation or IParameterReferenceOperation or IDiscardOperation))
                    Report(HandleEscape, value, value.Syntax.ToString());
            }

            private bool WriteAllowed(IOperation operation)
            {
                if (operation is ILocalReferenceOperation local) return Same(local.Local.ContainingSymbol, _method);
                if (operation is IDiscardOperation) return true;
                return operation is ITupleOperation tuple && tuple.Elements.All(WriteAllowed);
            }

            private bool ConstructorWrites(IFieldSymbol field)
                => _method?.MethodKind == MethodKind.Constructor && field.IsReadOnly && Same(field.ContainingType, _method.ContainingType);

            private static bool NewInitializer(IOperation operation)
            {
                for (IOperation? current = operation.Parent; current is not null && current is not IBlockOperation; current = current.Parent)
                    if (current is IObjectOrCollectionInitializerOperation && current.Parent is IObjectCreationOperation or IWithOperation) return true;
                return false;
            }

            public override void VisitInvocation(IInvocationOperation operation)
            {
                IMethodSymbol method = operation.TargetMethod;
                if (_evaluating && method.ContainingNamespace.ToDisplayString() == "System.Linq") Report(Linq, operation, method.Name);
                if (method.MethodKind == MethodKind.DelegateInvoke)
                {
                    if (!VisitKnownDelegate(operation.Instance)) Report(Unsupported, operation, "无法静态绑定的委托 " + method.ToDisplayString());
                }
                else CheckCall(method, operation.Instance, operation.Arguments, operation);
                foreach (IArgumentOperation argument in operation.Arguments)
                {
                    if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out
                        && !(Unwrap(argument.Value) is ILocalReferenceOperation local && Same(local.Local.ContainingSymbol, _method)))
                        Report(Mutation, argument, "ref/out " + argument.Syntax);
                    if (ContainsHandle(argument.Value) && !SourceMethod(method) && !IsSdkCall(method))
                        Report(HandleEscape, argument, argument.Syntax.ToString());
                    CheckMetadataArgument(method, argument);
                }
                base.VisitInvocation(operation);
            }

            private void CheckMetadataArgument(IMethodSymbol method, IArgumentOperation argument)
            {
                if (SourceMethod(method) || IsSdkCall(method)) return;
                if (argument.Parameter?.Name is "comparer" or "comparison" && argument.Value.ConstantValue is { HasValue: true, Value: null }
                    && method.Name is ("Sort" or "BinarySearch" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or "Order" or "OrderDescending"))
                    Report(Unsupported, argument, "默认排序 comparer 不能确定文化与回调");
                if (argument.Value.ConstantValue.HasValue && argument.Value.ConstantValue.Value is null) return;
                if (argument.Parameter?.Type.TypeKind == TypeKind.Interface && !KnownMetadataInput(argument.Value))
                    Report(Unsupported, argument, "无法静态证明的 metadata 接口回调 " + argument.Syntax);
                if (argument.Value.Type?.TypeKind == TypeKind.Delegate && !VisitKnownDelegate(argument.Value))
                    Report(Unsupported, argument, "无法静态绑定的 metadata 委托回调 " + argument.Syntax);
            }

            private bool KnownMetadataInput(IOperation operation)
            {
                operation = Unwrap(operation);
                if (operation.Type is IArrayTypeSymbol) return true;
                if (operation is ILocalReferenceOperation local && _knownInterfaceValues.Contains(local.Local)) return true;
                if (operation is IInvocationOperation invocation && invocation.TargetMethod.ContainingNamespace.ToDisplayString()
                    is "System.Linq" or "System.Collections.Immutable" or "System.Collections.Frozen") return true;
                if (operation is IPropertyReferenceOperation { Property.GetMethod: { } getter } && IsSdkCall(getter)) return true;
                return operation.Type is { TypeKind: not TypeKind.Interface } type && !SourceMethod(type)
                    && (IsSdkValue(type) || type.ContainingNamespace.ToDisplayString()
                        is "System.Collections.Generic" or "System.Collections.Immutable" or "System.Collections.Frozen" or "System");
            }

            private void CheckCall(IMethodSymbol method, IOperation? instance, ImmutableArray<IArgumentOperation> arguments, IOperation operation)
            {
                if (Forbidden(method)) { Report(Capability, operation, method.ToDisplayString()); return; }
                if (SourceMethod(method))
                {
                    if ((method.IsVirtual || method.IsAbstract) && !method.IsSealed && !method.ContainingType.IsSealed)
                    { Report(Unsupported, operation, "开放虚分派 " + method.ToDisplayString()); return; }
                    int offset = method.ReducedFrom is null ? 0 : 1;
                    ImmutableArray<int> owned = [.. arguments.Where(argument => argument.Parameter is not null && Owned(argument.Value))
                        .Select(argument => argument.Parameter!.Ordinal + offset), .. offset != 0 && Owned(instance) ? new[] { 0 } : []];
                    _analysis.CheckMethod(method, _evaluating, owned, operation.Syntax.GetLocation());
                    return;
                }
                if (IsSdkCall(method))
                {
                    if (IsStageHandle(method.ContainingType) && method.MethodKind == MethodKind.Constructor)
                        Report(Capability, operation, "宿主阶段句柄构造 " + method.ContainingType.Name);
                    if (method.ContainingType.Name is ("TextOutput" or "VisualOutput" or "ParticleFrame") && method.Name is ("Complete" or "Invalidate" or "Dispose"))
                        Report(Capability, operation, "宿主输出生命周期 " + method.Name);
                    return;
                }
                if (MutableCollection(method.ContainingType))
                {
                    if (method.Name is ("Sort" or "BinarySearch")
                        && !arguments.Any(static argument => argument.Parameter?.Name is "comparer" or "comparison"))
                        Report(Unsupported, operation, "需要显式确定性 comparer 的集合排序 " + method.Name);
                    if (method.MethodKind == MethodKind.Constructor || Owned(instance)) return;
                    Report(Mutation, operation, "非局部集合 " + method.ToDisplayString()); return;
                }
                if (method.ContainingType.SpecialType == SpecialType.System_Array && method.Name is ("Copy" or "Clear" or "Fill" or "Reverse"))
                {
                    IArgumentOperation? target = method.Name == "Copy"
                        ? arguments.FirstOrDefault(static argument => argument.Parameter?.Name.Contains("destination", StringComparison.OrdinalIgnoreCase) == true)
                        : arguments.FirstOrDefault();
                    if (target is null || !Owned(target.Value)) Report(Mutation, operation, "非局部数组 " + method.Name);
                }
                if (!AllowedBcl(method, instance, arguments)) Report(Unsupported, operation, method.ToDisplayString());
            }

            public override void VisitPropertyReference(IPropertyReferenceOperation operation)
            {
                IPropertySymbol property = operation.Property;
                bool writeOnly = operation.Parent is ISimpleAssignmentOperation assignment && ReferenceEquals(assignment.Target, operation);
                if (!writeOnly && property.GetMethod is { } getter) CheckCall(getter, operation.Instance, operation.Arguments, operation);
                base.VisitPropertyReference(operation);
            }

            public override void VisitFieldReference(IFieldReferenceOperation operation)
            {
                IFieldSymbol field = operation.Field;
                if (ForbiddenField(field)) Report(Capability, operation, field.ToDisplayString());
                else if (field.IsStatic && !field.IsConst && (!field.IsReadOnly || !_analysis.Readonly(field.Type, new(SymbolEqualityComparer.Default), out _)))
                    Report(StaticState, operation, field.ToDisplayString());
                else if (field.IsStatic && SourceMethod(field.ContainingType)) _analysis.CheckStaticState(field.ContainingType);
                base.VisitFieldReference(operation);
            }

            public override void VisitObjectCreation(IObjectCreationOperation operation)
            {
                if (_evaluating && operation.Type?.IsReferenceType == true) Report(Allocation, operation, operation.Type.ToDisplayString());
                if (_analysis.IsPrepared(operation.Type)) _analysis.CheckPrepared(operation.Type!, operation.Syntax.GetLocation());
                if (operation.Constructor is { } constructor) CheckCall(constructor, null, operation.Arguments, operation);
                foreach (IArgumentOperation argument in operation.Arguments)
                {
                    if (ContainsHandle(argument.Value)) Report(HandleEscape, argument, argument.Syntax.ToString());
                    if (operation.Constructor is { } calledConstructor) CheckMetadataArgument(calledConstructor, argument);
                }
                base.VisitObjectCreation(operation);
            }

            public override void VisitArrayCreation(IArrayCreationOperation operation)
            {
                if (_evaluating) Report(Allocation, operation, operation.Type?.ToDisplayString() ?? "array");
                if (operation.Initializer?.ElementValues.Any(ContainsHandle) == true) Report(HandleEscape, operation, operation.Syntax.ToString());
                base.VisitArrayCreation(operation);
            }

            public override void VisitCollectionExpression(ICollectionExpressionOperation operation)
            {
                if (_evaluating) Report(Allocation, operation, operation.Type?.ToDisplayString() ?? "collection");
                if (operation.Elements.Any(ContainsHandle)) Report(HandleEscape, operation, operation.Syntax.ToString());
                base.VisitCollectionExpression(operation);
            }

            public override void VisitReturn(IReturnOperation operation)
            {
                if (operation.ReturnedValue is { } value)
                {
                    if (ContainsHandle(value)) Report(HandleEscape, value, value.Syntax.ToString());
                    if (_method?.Name == "Declare" || _analysis.IsPrepared(value.Type))
                        _analysis.CheckPrepared(Unwrap(value).Type ?? value.Type!, value.Syntax.GetLocation());
                }
                base.VisitReturn(operation);
            }

            public override void VisitAnonymousFunction(IAnonymousFunctionOperation operation)
            {
                if (operation.Symbol.IsAsync) Report(UnsupportedLanguage, operation, "async lambda");
                CheckCapture(operation);
                new Walker(_analysis, operation.Symbol, _evaluating, []).Visit(operation.Body);
            }

            public override void VisitLocalFunction(ILocalFunctionOperation operation)
            {
                CheckCapture(operation);
                _analysis.CheckMethod(operation.Symbol, _evaluating, [], operation.Syntax.GetLocation());
            }

            private void CheckCapture(IOperation operation)
            {
                foreach (IOperation child in operation.Descendants())
                    if (child is ILocalReferenceOperation or IParameterReferenceOperation && ContainsHandle(child))
                        Report(HandleEscape, child, "闭包捕获 " + child.Syntax);
            }

            public override void VisitAwait(IAwaitOperation operation) { Report(UnsupportedLanguage, operation, "await"); base.VisitAwait(operation); }
            public override void VisitDynamicInvocation(IDynamicInvocationOperation operation) { Report(UnsupportedLanguage, operation, "dynamic"); base.VisitDynamicInvocation(operation); }
            public override void VisitDynamicMemberReference(IDynamicMemberReferenceOperation operation) { Report(UnsupportedLanguage, operation, "dynamic"); base.VisitDynamicMemberReference(operation); }
            public override void VisitAddressOf(IAddressOfOperation operation) { Report(UnsupportedLanguage, operation, "address-of"); base.VisitAddressOf(operation); }
            public override void VisitLock(ILockOperation operation) { Report(Capability, operation, "lock"); base.VisitLock(operation); }
            public override void VisitEventAssignment(IEventAssignmentOperation operation) { Report(Mutation, operation, "event subscription"); base.VisitEventAssignment(operation); }
            public override void VisitTypeOf(ITypeOfOperation operation) { Report(Capability, operation, "typeof/reflection"); base.VisitTypeOf(operation); }
            public override void VisitBinaryOperator(IBinaryOperation operation)
            {
                if (operation.OperatorMethod is { } method) CheckCall(method, null, [], operation);
                if (operation.Type?.SpecialType == SpecialType.System_String
                    && (operation.LeftOperand.Type?.SpecialType is not (SpecialType.System_String or SpecialType.System_Char)
                        || operation.RightOperand.Type?.SpecialType is not (SpecialType.System_String or SpecialType.System_Char)))
                    Report(Unsupported, operation, "隐式文化格式化的字符串拼接");
                base.VisitBinaryOperator(operation);
            }
            public override void VisitUnaryOperator(IUnaryOperation operation)
            {
                if (operation.OperatorMethod is { } method) CheckCall(method, null, [], operation);
                base.VisitUnaryOperator(operation);
            }
            public override void VisitConversion(IConversionOperation operation)
            {
                if (operation.OperatorMethod is { } method) CheckCall(method, null, [], operation);
                if (operation.Type?.TypeKind == TypeKind.Dynamic) Report(UnsupportedLanguage, operation, "dynamic conversion");
                base.VisitConversion(operation);
            }
            public override void VisitInterpolatedString(IInterpolatedStringOperation operation)
            {
                if (operation.Parts.Any(static part => part is IInterpolationOperation interpolation
                    && interpolation.Expression.Type?.SpecialType is not (SpecialType.System_String or SpecialType.System_Char)))
                    Report(Unsupported, operation, "隐式文化格式化的插值字符串");
                base.VisitInterpolatedString(operation);
            }
            public override void VisitMethodReference(IMethodReferenceOperation operation)
            {
                CheckCall(operation.Method, operation.Instance, [], operation);
                if (operation.Instance is { } receiver && ContainsHandle(receiver)) Report(HandleEscape, operation, "句柄方法委托 " + operation.Syntax);
                base.VisitMethodReference(operation);
            }

            private bool VisitKnownDelegate(IOperation? instance)
            {
                if (instance is null) return false;
                instance = Unwrap(instance);
                if (instance is ILocalReferenceOperation local && _delegates.TryGetValue(local.Local, out IOperation? value)) return VisitKnownDelegate(value);
                if (instance is IDelegateCreationOperation creation) return VisitKnownDelegate(creation.Target);
                if (instance is IAnonymousFunctionOperation lambda) { VisitAnonymousFunction(lambda); return true; }
                if (instance is IMethodReferenceOperation method)
                {
                    CheckCall(method.Method, method.Instance, [], method); return true;
                }
                return false;
            }

            private bool Owned(IOperation? operation)
            {
                if (operation is null) return false;
                operation = Unwrap(operation);
                return operation switch
                {
                    IObjectCreationOperation or IArrayCreationOperation or ICollectionExpressionOperation or IWithOperation => true,
                    ILocalReferenceOperation local => _owned.Contains(local.Local),
                    IParameterReferenceOperation parameter => _owned.Contains(parameter.Parameter),
                    IInvocationOperation invocation => invocation.TargetMethod.ContainingNamespace.ToDisplayString() is "System.Linq" or "System.Collections.Immutable" or "System.Collections.Frozen"
                        && invocation.TargetMethod.Name is "ToArray" or "ToList" or "ToDictionary" or "ToHashSet" or "ToImmutable" or "ToBuilder" or "CreateBuilder",
                    _ => false,
                };
            }

            private static IOperation Unwrap(IOperation operation)
            {
                while (operation is IConversionOperation conversion) operation = conversion.Operand;
                return operation;
            }

            private bool ContainsHandle(IOperation operation)
            {
                if (IsStageHandle(operation.Type)) return true;
                operation = Unwrap(operation);
                return IsStageHandle(operation.Type) || operation.Type is INamedTypeSymbol named && named.TypeArguments.Any(IsStageHandle)
                    || operation is ILocalReferenceOperation local && _handleAliases.Contains(local.Local)
                    || operation is ITupleOperation tuple && tuple.Elements.Any(ContainsHandle);
            }
            private static bool IsDelegate(IOperation operation) => Unwrap(operation) is IDelegateCreationOperation or IAnonymousFunctionOperation or IMethodReferenceOperation;
            private static bool SourceMethod(ISymbol symbol) => symbol.Locations.Any(static value => value.IsInSource);
            private void Report(DiagnosticDescriptor descriptor, IOperation operation, string detail)
                => _analysis.Report(descriptor, operation.Syntax.GetLocation(), detail);
        }
    }
}
