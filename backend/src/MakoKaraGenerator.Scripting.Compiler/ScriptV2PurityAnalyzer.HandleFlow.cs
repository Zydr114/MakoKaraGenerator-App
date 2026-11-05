using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace MakoKaraGenerator.Scripting.Compilation;

public static partial class ScriptV2PurityAnalyzer
{
    private sealed partial class Analysis
    {
        private sealed partial class Walker
        {
            // Only handle aliases flow here. Ownership, metadata-interface proof
            // and delegate binding keep their existing independent rules.
            private int _mergeHandleAssignments;

            private HashSet<ISymbol> HandleState() => new(_handleAliases, SymbolEqualityComparer.Default);

            private void RestoreHandleState(HashSet<ISymbol> state)
            {
                _handleAliases.Clear();
                _handleAliases.UnionWith(state);
            }

            private void SetHandleAlias(ISymbol symbol, bool containsHandle)
            {
                if (containsHandle) _handleAliases.Add(symbol);
                else if (_mergeHandleAssignments == 0) _handleAliases.Remove(symbol);
            }

            private void AssignHandleAlias(IOperation target, bool containsHandle)
            {
                target = Unwrap(target);
                if (target is ILocalReferenceOperation local) SetHandleAlias(local.Local, containsHandle);
                else if (target is IParameterReferenceOperation parameter) SetHandleAlias(parameter.Parameter, containsHandle);
            }

            public override void VisitConditional(IConditionalOperation operation)
            {
                Visit(operation.Condition);
                HashSet<ISymbol> entry = HandleState();
                Visit(operation.WhenTrue);
                HashSet<ISymbol> result = HandleState();
                RestoreHandleState(entry);
                Visit(operation.WhenFalse);
                _handleAliases.UnionWith(result);
            }

            private bool VisitShortCircuitHandleFlow(IBinaryOperation operation)
            {
                if (operation.OperatorKind is not (BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)) return false;
                Visit(operation.LeftOperand);
                HashSet<ISymbol> skipped = HandleState();
                Visit(operation.RightOperand);
                _handleAliases.UnionWith(skipped);
                return true;
            }

            public override void VisitCoalesce(ICoalesceOperation operation)
            {
                Visit(operation.Value);
                HashSet<ISymbol> nonNull = HandleState();
                Visit(operation.WhenNull);
                _handleAliases.UnionWith(nonNull);
            }

            public override void VisitCoalesceAssignment(ICoalesceAssignmentOperation operation)
            {
                CheckWrite(operation.Target, operation.Value);
                Visit(operation.Target);
                HashSet<ISymbol> nonNull = HandleState();
                Visit(operation.Value);
                AssignHandleAlias(operation.Target, ContainsHandle(operation.Value));
                _handleAliases.UnionWith(nonNull);
            }

            public override void VisitConditionalAccess(IConditionalAccessOperation operation)
            {
                Visit(operation.Operation);
                HashSet<ISymbol> nullReceiver = HandleState();
                Visit(operation.WhenNotNull);
                _handleAliases.UnionWith(nullReceiver);
            }

            private void BindRootPatternAliases(IPatternOperation pattern, bool containsHandle)
            {
                // Subpatterns use the projected member/element type, not the
                // parent's taint: Catalog stays detached, while Media/Indexes
                // cannot lose their stage identity through an object pattern.
                containsHandle |= ContainsStageHandleType(pattern.InputType);
                switch (pattern)
                {
                    case IDeclarationPatternOperation declaration when declaration.DeclaredSymbol is { } declarationSymbol:
                        SetHandleAlias(declarationSymbol, containsHandle);
                        break;
                    case IRecursivePatternOperation recursive:
                        if (recursive.DeclaredSymbol is { } recursiveSymbol) SetHandleAlias(recursiveSymbol, containsHandle);
                        foreach (IPropertySubpatternOperation property in recursive.PropertySubpatterns)
                            BindRootPatternAliases(property.Pattern, ContainsHandle(property.Member));
                        foreach (IPatternOperation element in recursive.DeconstructionSubpatterns)
                            BindRootPatternAliases(element, ContainsStageHandleType(element.InputType));
                        break;
                    case IListPatternOperation list:
                        if (list.DeclaredSymbol is { } listSymbol) SetHandleAlias(listSymbol, containsHandle);
                        foreach (IPatternOperation element in list.Patterns)
                            BindRootPatternAliases(element, ContainsStageHandleType(element.InputType));
                        break;
                    case IBinaryPatternOperation binary:
                        BindRootPatternAliases(binary.LeftPattern, containsHandle);
                        BindRootPatternAliases(binary.RightPattern, containsHandle);
                        break;
                    case INegatedPatternOperation negated:
                        BindRootPatternAliases(negated.Pattern, containsHandle);
                        break;
                }
            }

            public override void VisitIsPattern(IIsPatternOperation operation)
            {
                BindRootPatternAliases(operation.Pattern, ContainsHandle(operation.Value));
                base.VisitIsPattern(operation);
            }

            private bool SwitchResultContainsHandle(ISwitchExpressionOperation operation)
            {
                bool inputContainsHandle = ContainsHandle(operation.Value);
                foreach (ISwitchExpressionArmOperation arm in operation.Arms)
                {
                    BindRootPatternAliases(arm.Pattern, inputContainsHandle);
                    if (ContainsHandle(arm.Value)) return true;
                }
                return false;
            }

            public override void VisitSwitchExpression(ISwitchExpressionOperation operation)
            {
                Visit(operation.Value);
                bool inputContainsHandle = ContainsHandle(operation.Value);
                HashSet<ISymbol> entry = HandleState();
                HashSet<ISymbol> result = new(SymbolEqualityComparer.Default);
                foreach (ISwitchExpressionArmOperation arm in operation.Arms)
                {
                    RestoreHandleState(entry);
                    BindRootPatternAliases(arm.Pattern, inputContainsHandle);
                    Visit(arm);
                    result.UnionWith(_handleAliases);
                }
                RestoreHandleState(result);
            }

            public override void VisitSwitch(ISwitchOperation operation)
            {
                Visit(operation.Value);
                bool inputContainsHandle = ContainsHandle(operation.Value);
                HashSet<ISymbol> entry = HandleState();
                HashSet<ISymbol> result = new(SymbolEqualityComparer.Default);
                bool hasDefault = false;
                foreach (ISwitchCaseOperation item in operation.Cases)
                {
                    RestoreHandleState(entry);
                    foreach (ICaseClauseOperation clause in item.Clauses)
                    {
                        if (clause is IDefaultCaseClauseOperation) hasDefault = true;
                        if (clause is IPatternCaseClauseOperation pattern) BindRootPatternAliases(pattern.Pattern, inputContainsHandle);
                    }
                    Visit(item);
                    result.UnionWith(_handleAliases);
                }
                if (!hasDefault) result.UnionWith(entry);
                RestoreHandleState(result);
            }

            // Loops may use a previous iteration's alias before the next
            // assignment. Monotone iteration reaches a finite symbol-set fixpoint.
            private void VisitLoopHandleFlow(Action visit)
            {
                _mergeHandleAssignments++;
                try
                {
                    HashSet<ISymbol> before;
                    do { before = HandleState(); visit(); }
                    while (!before.SetEquals(_handleAliases));
                }
                finally { _mergeHandleAssignments--; }
            }

            public override void VisitWhileLoop(IWhileLoopOperation operation)
                => VisitLoopHandleFlow(() => base.VisitWhileLoop(operation));

            public override void VisitForLoop(IForLoopOperation operation)
                => VisitLoopHandleFlow(() => base.VisitForLoop(operation));

            public override void VisitForEachLoop(IForEachLoopOperation operation)
                => VisitLoopHandleFlow(() => base.VisitForEachLoop(operation));

            public override void VisitTry(ITryOperation operation)
            {
                // An exception can leave the body before its last assignment.
                // Keep any possible alias throughout body/catches. Finally runs
                // after this merge and can perform an unconditional safe write.
                _mergeHandleAssignments++;
                try
                {
                    Visit(operation.Body);
                    HashSet<ISymbol> exceptional = HandleState();
                    HashSet<ISymbol> result = HandleState();
                    foreach (ICatchClauseOperation clause in operation.Catches)
                    {
                        RestoreHandleState(exceptional);
                        Visit(clause);
                        result.UnionWith(_handleAliases);
                    }
                    RestoreHandleState(result);
                }
                finally { _mergeHandleAssignments--; }
                Visit(operation.Finally);
            }

            public override void VisitBranch(IBranchOperation operation)
            {
                if (operation.BranchKind == BranchKind.GoTo)
                    Report(UnsupportedLanguage, operation, "goto 需要未支持的非结构化控制流证明");
                base.VisitBranch(operation);
            }
        }
    }
}
