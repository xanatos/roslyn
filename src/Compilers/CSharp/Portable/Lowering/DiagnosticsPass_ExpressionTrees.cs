﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// This pass detects and reports diagnostics that do not affect lambda convertibility.
    /// This part of the partial class focuses on features that cannot be used in expression trees.
    /// CAVEAT: Errors may be produced for ObsoleteAttribute, but such errors don't affect lambda convertibility.
    /// </summary>
    internal sealed partial class DiagnosticsPass
    {
        private readonly DiagnosticBag _diagnostics;
        private readonly CSharpCompilation _compilation;
        private bool _inExpressionLambda;
        private LocalFunctionSymbol _staticLocalFunction;
        private bool _reportedUnsafe;
        private readonly MethodSymbol _containingSymbol;

        public static void IssueDiagnostics(CSharpCompilation compilation, BoundNode node, DiagnosticBag diagnostics, MethodSymbol containingSymbol)
        {
            Debug.Assert(node != null);
            Debug.Assert((object)containingSymbol != null);

            try
            {
                var diagnosticPass = new DiagnosticsPass(compilation, diagnostics, containingSymbol);
                diagnosticPass.Visit(node);
            }
            catch (CancelledByStackGuardException ex)
            {
                ex.AddAnError(diagnostics);
            }
        }

        private DiagnosticsPass(CSharpCompilation compilation, DiagnosticBag diagnostics, MethodSymbol containingSymbol)
        {
            Debug.Assert(diagnostics != null);
            Debug.Assert((object)containingSymbol != null);

            _compilation = compilation;
            _diagnostics = diagnostics;
            _containingSymbol = containingSymbol;
        }

        private void Error(ErrorCode code, BoundNode node, params object[] args)
        {
            _diagnostics.Add(code, node.Syntax.Location, args);
        }

        private void CheckUnsafeType(BoundExpression e)
        {
            if (e != null && (object)e.Type != null && e.Type.TypeKind == TypeKind.Pointer) NoteUnsafe(e);
        }

        private void NoteUnsafe(BoundNode node)
        {
            if (_inExpressionLambda && !_reportedUnsafe)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsPointerOp, node);
                _reportedUnsafe = true;
            }
        }

        private bool? _hasCSharpExpression, hasCSharpStatement, hasCSharpDynamic;
        private bool HasCSharpExpression => IsDefined(ref _hasCSharpExpression, WellKnownType.Microsoft_CSharp_Expressions_CSharpExpression);
        private bool HasCSharpStatement => IsDefined(ref hasCSharpStatement, WellKnownType.Microsoft_CSharp_Expressions_CSharpStatement);
        private bool HasCSharpDynamic => IsDefined(ref hasCSharpDynamic, WellKnownType.Microsoft_CSharp_Expressions_DynamicCSharpExpression);

        private bool IsDefined(ref bool? field, WellKnownType type)
        {
            // NB: Always return true from this method if we like to see ERR_PredefinedTypeNotFound instead.
            //     The goal of this metadata check is to report the original errors if the runtime library is missing.

            if (!field.HasValue)
            {
                field = !(_compilation.GetTypeByMetadataName(type.GetMetadataName()) is null);
            }

            return field.Value;
        }

        public override BoundNode VisitArrayCreation(BoundArrayCreation node)
        {
            return base.VisitArrayCreation(node);
        }

        public override BoundNode VisitArrayAccess(BoundArrayAccess node)
        {
            if (_inExpressionLambda &&
                node.Indices.Length == 1 &&
                node.Indices[0].Type!.SpecialType == SpecialType.None)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsPatternIndexOrRangeIndexer, node);
            }

            return base.VisitArrayAccess(node);
        }

        public override BoundNode VisitIndexOrRangePatternIndexerAccess(BoundIndexOrRangePatternIndexerAccess node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsPatternIndexOrRangeIndexer, node);
            }

            return base.VisitIndexOrRangePatternIndexerAccess(node);
        }

        public override BoundNode VisitFromEndIndexExpression(BoundFromEndIndexExpression node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsFromEndIndexExpression, node);
            }

            return base.VisitFromEndIndexExpression(node);
        }

        public override BoundNode VisitRangeExpression(BoundRangeExpression node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsRangeExpression, node);
            }

            return base.VisitRangeExpression(node);
        }

        public override BoundNode VisitSizeOfOperator(BoundSizeOfOperator node)
        {
            if (_inExpressionLambda && node.ConstantValue == null)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsPointerOp, node);
            }

            return base.VisitSizeOfOperator(node);
        }

        public override BoundNode VisitLocalFunctionStatement(BoundLocalFunctionStatement node)
        {
            var outerLocalFunction = _staticLocalFunction;
            if (node.Symbol.IsStatic)
            {
                _staticLocalFunction = node.Symbol;
            }
            var result = base.VisitLocalFunctionStatement(node);
            _staticLocalFunction = outerLocalFunction;
            return result;
        }

        public override BoundNode VisitThisReference(BoundThisReference node)
        {
            CheckReferenceToThisOrBase(node);
            return base.VisitThisReference(node);
        }

        public override BoundNode VisitBaseReference(BoundBaseReference node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsBaseAccess, node);
            }
            CheckReferenceToThisOrBase(node);
            return base.VisitBaseReference(node);
        }

        public override BoundNode VisitLocal(BoundLocal node)
        {
            CheckReferenceToVariable(node, node.LocalSymbol);
            return base.VisitLocal(node);
        }

        public override BoundNode VisitParameter(BoundParameter node)
        {
            CheckReferenceToVariable(node, node.ParameterSymbol);
            return base.VisitParameter(node);
        }

        private void CheckReferenceToThisOrBase(BoundExpression node)
        {
            if ((object)_staticLocalFunction != null)
            {
                Error(ErrorCode.ERR_StaticLocalFunctionCannotCaptureThis, node);
            }
        }

        private void CheckReferenceToVariable(BoundExpression node, Symbol symbol)
        {
            Debug.Assert(symbol.Kind == SymbolKind.Local || symbol.Kind == SymbolKind.Parameter || symbol is LocalFunctionSymbol);

            if (_staticLocalFunction is object && Symbol.IsCaptured(symbol, _staticLocalFunction))
            {
                Error(ErrorCode.ERR_StaticLocalFunctionCannotCaptureVariable, node, new FormattedSymbol(symbol, SymbolDisplayFormat.ShortFormat));
            }
        }

        private void CheckReferenceToMethodIfLocalFunction(BoundExpression node, MethodSymbol method)
        {
            if (method?.OriginalDefinition is LocalFunctionSymbol localFunction)
            {
                CheckReferenceToVariable(node, localFunction);
            }
        }

        public override BoundNode VisitConvertedSwitchExpression(BoundConvertedSwitchExpression node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsSwitchExpression, node);
            }

            return base.VisitConvertedSwitchExpression(node);
        }

        public override BoundNode VisitDeconstructionAssignmentOperator(BoundDeconstructionAssignmentOperator node)
        {
            if (!node.HasAnyErrors)
            {
                CheckForDeconstructionAssignmentToSelf((BoundTupleExpression)node.Left, node.Right);
            }

            return base.VisitDeconstructionAssignmentOperator(node);
        }

        public override BoundNode VisitAssignmentOperator(BoundAssignmentOperator node)
        {
            CheckForAssignmentToSelf(node);

            if (_inExpressionLambda && !HasCSharpExpression && node.Left.Kind != BoundKind.ObjectInitializerMember && node.Left.Kind != BoundKind.DynamicObjectInitializerMember)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsAssignment, node);
            }

            return base.VisitAssignmentOperator(node);
        }

        public override BoundNode VisitDynamicObjectInitializerMember(BoundDynamicObjectInitializerMember node)
        {
            if (_inExpressionLambda)
            {
                // TODO: Can we support this?
                Error(ErrorCode.ERR_ExpressionTreeContainsDynamicOperation, node);
            }

            return base.VisitDynamicObjectInitializerMember(node);
        }

        public override BoundNode VisitEventAccess(BoundEventAccess node)
        {
            // Don't bother reporting an obsolete diagnostic if the access is already wrong for other reasons
            // (specifically, we can't use it as a field here).
            if (node.IsUsableAsField)
            {
                bool hasBaseReceiver = node.ReceiverOpt != null && node.ReceiverOpt.Kind == BoundKind.BaseReference;
                Binder.ReportDiagnosticsIfObsolete(_diagnostics, node.EventSymbol.AssociatedField, node.Syntax, hasBaseReceiver, _containingSymbol, _containingSymbol.ContainingType, BinderFlags.None);
            }
            CheckReceiverIfField(node.ReceiverOpt);
            return base.VisitEventAccess(node);
        }

        public override BoundNode VisitEventAssignmentOperator(BoundEventAssignmentOperator node)
        {
            if (_inExpressionLambda)
            {
                // TODO: Can we support this?
                Error(ErrorCode.ERR_ExpressionTreeContainsAssignment, node);
            }

            bool hasBaseReceiver = node.ReceiverOpt != null && node.ReceiverOpt.Kind == BoundKind.BaseReference;
            Binder.ReportDiagnosticsIfObsolete(_diagnostics, node.Event, ((AssignmentExpressionSyntax)node.Syntax).Left, hasBaseReceiver, _containingSymbol, _containingSymbol.ContainingType, BinderFlags.None);
            CheckReceiverIfField(node.ReceiverOpt);
            return base.VisitEventAssignmentOperator(node);
        }

        public override BoundNode VisitCompoundAssignmentOperator(BoundCompoundAssignmentOperator node)
        {
            CheckCompoundAssignmentOperator(node);

            return base.VisitCompoundAssignmentOperator(node);
        }

        private void VisitCall(
            MethodSymbol method,
            PropertySymbol propertyAccess,
            ImmutableArray<BoundExpression> arguments,
            ImmutableArray<RefKind> argumentRefKindsOpt,
            ImmutableArray<string> argumentNamesOpt,
            bool expanded,
            BoundNode node)
        {
            Debug.Assert((object)method != null);
            Debug.Assert(((object)propertyAccess == null) ||
                (method == propertyAccess.GetOwnOrInheritedGetMethod()) ||
                (method == propertyAccess.GetOwnOrInheritedSetMethod()) ||
                propertyAccess.MustCallMethodsDirectly);

            CheckArguments(argumentRefKindsOpt, arguments, method);

            if (_inExpressionLambda)
            {
                if (method.CallsAreOmitted(node.SyntaxTree))
                {
                    Error(ErrorCode.ERR_PartialMethodInExpressionTree, node);
                }
                else if ((object)propertyAccess != null && propertyAccess.IsIndexedProperty() && !propertyAccess.IsIndexer)
                {
                    // TODO: Can we support this?
                    Error(ErrorCode.ERR_ExpressionTreeContainsIndexedProperty, node);
                }
                else if (IsComCallWithRefOmitted(method, arguments, argumentRefKindsOpt))
                {
                    // TODO: Can we support this?
                    Error(ErrorCode.ERR_ComRefCallInExpressionTree, node);
                }
                else if (method.MethodKind == MethodKind.LocalFunction)
                {
                    Error(ErrorCode.ERR_ExpressionTreeContainsLocalFunction, node);
                }
                else if (method.RefKind != RefKind.None)
                {
                    Error(ErrorCode.ERR_RefReturningCallInExpressionTree, node);
                }
                else if (!HasCSharpExpression)
                {
                    if (arguments.Length < (((object)propertyAccess != null) ? propertyAccess.ParameterCount : method.ParameterCount) + (expanded ? -1 : 0))
                    {
                        Error(ErrorCode.ERR_ExpressionTreeContainsOptionalArgument, node);
                    }
                    else if (!argumentNamesOpt.IsDefault)
                    {
                        Error(ErrorCode.ERR_ExpressionTreeContainsNamedArgument, node);
                    }
                }
            }
        }

        public override BoundNode Visit(BoundNode node)
        {
            if (_inExpressionLambda &&
                // Ignoring BoundConversion nodes prevents redundant diagnostics
                !(node is BoundConversion) &&
                node is BoundExpression expr &&
                expr.Type is TypeSymbol type &&
                type.IsRestrictedType())
            {
                Error(ErrorCode.ERR_ExpressionTreeCantContainRefStruct, node, type.Name);
            }
            return base.Visit(node);
        }

        public override BoundNode VisitRefTypeOperator(BoundRefTypeOperator node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_FeatureNotValidInExpressionTree, node, "__reftype");
            }

            return base.VisitRefTypeOperator(node);
        }

        public override BoundNode VisitRefValueOperator(BoundRefValueOperator node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_FeatureNotValidInExpressionTree, node, "__refvalue");
            }

            return base.VisitRefValueOperator(node);
        }

        public override BoundNode VisitMakeRefOperator(BoundMakeRefOperator node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_FeatureNotValidInExpressionTree, node, "__makeref");
            }

            return base.VisitMakeRefOperator(node);
        }

        public override BoundNode VisitArgListOperator(BoundArgListOperator node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_VarArgsInExpressionTree, node);
            }

            return base.VisitArgListOperator(node);
        }

        public override BoundNode VisitConditionalAccess(BoundConditionalAccess node)
        {
            if (_inExpressionLambda && !HasCSharpExpression)
            {
                Error(ErrorCode.ERR_NullPropagatingOpInExpressionTree, node);
            }

            return base.VisitConditionalAccess(node);
        }

        public override BoundNode VisitObjectInitializerMember(BoundObjectInitializerMember node)
        {
            if (_inExpressionLambda && !node.Arguments.IsDefaultOrEmpty)
            {
                // TODO: Can we support this?
                Error(ErrorCode.ERR_DictionaryInitializerInExpressionTree, node);
            }

            return base.VisitObjectInitializerMember(node);
        }

        public override BoundNode VisitCall(BoundCall node)
        {
            VisitCall(node.Method, null, node.Arguments, node.ArgumentRefKindsOpt, node.ArgumentNamesOpt, node.Expanded, node);
            CheckReceiverIfField(node.ReceiverOpt);
            CheckReferenceToMethodIfLocalFunction(node, node.Method);
            return base.VisitCall(node);
        }

        /// <summary>
        /// Called when a local represents an out variable declaration. Its syntax is of type DeclarationExpressionSyntax.
        /// </summary>
        private void CheckOutDeclaration(BoundLocal local)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsOutVariable, local);
            }
        }

        private void CheckDiscard(BoundDiscardExpression argument)
        {
            if (_inExpressionLambda && !HasCSharpExpression)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsDiscard, argument);
            }
        }

        public override BoundNode VisitCollectionElementInitializer(BoundCollectionElementInitializer node)
        {
            if (_inExpressionLambda && node.AddMethod.IsStatic)
            {
                // TODO: Can we support this?
                Error(ErrorCode.ERR_ExtensionCollectionElementInitializerInExpressionTree, node);
            }

            VisitCall(node.AddMethod, null, node.Arguments, default(ImmutableArray<RefKind>), default(ImmutableArray<string>), node.Expanded, node);
            return base.VisitCollectionElementInitializer(node);
        }

        public override BoundNode VisitObjectCreationExpression(BoundObjectCreationExpression node)
        {
            VisitCall(node.Constructor, null, node.Arguments, node.ArgumentRefKindsOpt, node.ArgumentNamesOpt, node.Expanded, node);
            return base.VisitObjectCreationExpression(node);
        }

        public override BoundNode VisitIndexerAccess(BoundIndexerAccess node)
        {
            var indexer = node.Indexer;
            var method = indexer.GetOwnOrInheritedGetMethod() ?? indexer.GetOwnOrInheritedSetMethod();
            if ((object)method != null)
            {
                VisitCall(method, indexer, node.Arguments, node.ArgumentRefKindsOpt, node.ArgumentNamesOpt, node.Expanded, node);
            }
            CheckReceiverIfField(node.ReceiverOpt);
            return base.VisitIndexerAccess(node);
        }

        public override BoundNode VisitPropertyAccess(BoundPropertyAccess node)
        {
            var property = node.PropertySymbol;
            var method = property.GetMethod; // This is only checking for ref returns, so we don't fall back to the set method.
            if ((object)method != null && _inExpressionLambda && method.RefKind != RefKind.None)
            {
                Error(ErrorCode.ERR_RefReturningCallInExpressionTree, node);
            }
            CheckReceiverIfField(node.ReceiverOpt);
            return base.VisitPropertyAccess(node);
        }

        public override BoundNode VisitLambda(BoundLambda node)
        {
            if (_inExpressionLambda)
            {
                var lambda = node.Symbol;
                foreach (var p in lambda.Parameters)
                {
                    if (p.RefKind != RefKind.None && p.Locations.Length != 0)
                    {
                        _diagnostics.Add(ErrorCode.ERR_ByRefParameterInExpressionTree, p.Locations[0]);
                    }
                    if (p.TypeWithAnnotations.IsRestrictedType())
                    {
                        _diagnostics.Add(ErrorCode.ERR_ExpressionTreeCantContainRefStruct, p.Locations[0], p.Type.Name);
                    }
                }

                switch (node.Syntax.Kind())
                {
                    case SyntaxKind.ParenthesizedLambdaExpression:
                        {
                            var lambdaSyntax = (ParenthesizedLambdaExpressionSyntax)node.Syntax;
                            if (lambdaSyntax.AsyncKeyword.Kind() == SyntaxKind.AsyncKeyword)
                            {
                                if (!HasCSharpExpression)
                                {
                                    Error(ErrorCode.ERR_BadAsyncExpressionTree, node);
                                }
                            }
                            else if (lambdaSyntax.Body.Kind() == SyntaxKind.Block)
                            {
                                if (!HasCSharpStatement)
                                {
                                    Error(ErrorCode.ERR_StatementLambdaToExpressionTree, node);
                                }
                            }
                            else if (lambdaSyntax.Body.Kind() == SyntaxKind.RefExpression)
                            {
                                Error(ErrorCode.ERR_BadRefReturnExpressionTree, node);
                            }
                        }
                        break;

                    case SyntaxKind.SimpleLambdaExpression:
                        {
                            var lambdaSyntax = (SimpleLambdaExpressionSyntax)node.Syntax;
                            if (lambdaSyntax.AsyncKeyword.Kind() == SyntaxKind.AsyncKeyword)
                            {
                                if (!HasCSharpStatement)
                                {
                                    Error(ErrorCode.ERR_BadAsyncExpressionTree, node);
                                }
                            }
                            else if (lambdaSyntax.Body.Kind() == SyntaxKind.Block)
                            {
                                if (!HasCSharpStatement)
                                {
                                    Error(ErrorCode.ERR_StatementLambdaToExpressionTree, node);
                                }
                            }
                            else if (lambdaSyntax.Body.Kind() == SyntaxKind.RefExpression)
                            {
                                Error(ErrorCode.ERR_BadRefReturnExpressionTree, node);
                            }
                        }
                        break;

                    case SyntaxKind.AnonymousMethodExpression:
                        Error(ErrorCode.ERR_ExpressionTreeContainsAnonymousMethod, node);
                        break;

                    default:
                        // other syntax forms arise from query expressions, and always result from implied expression-lambda-like forms
                        break;
                }
            }

            return base.VisitLambda(node);
        }

        public override BoundNode VisitBinaryOperator(BoundBinaryOperator node)
        {
            // It is very common for bound trees to be left-heavy binary operators, eg,
            // a + b + c + d + ...
            // To avoid blowing the stack, do not recurse down the left hand side.

            // In order to avoid blowing the stack, we end up visiting right children
            // before left children; this should not be a problem in the diagnostics 
            // pass.

            BoundBinaryOperator current = node;
            while (true)
            {
                CheckBinaryOperator(current);

                Visit(current.Right);
                if (current.Left.Kind == BoundKind.BinaryOperator)
                {
                    current = (BoundBinaryOperator)current.Left;
                }
                else
                {
                    Visit(current.Left);
                    break;
                }
            }

            return null;
        }

        public override BoundNode VisitUserDefinedConditionalLogicalOperator(BoundUserDefinedConditionalLogicalOperator node)
        {
            CheckLiftedUserDefinedConditionalLogicalOperator(node);
            return base.VisitUserDefinedConditionalLogicalOperator(node);
        }

        private void CheckDynamic(BoundUnaryOperator node)
        {
            if (_inExpressionLambda && node.OperatorKind.IsDynamic() && !HasCSharpDynamic)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsDynamicOperation, node);
            }
        }

        private void CheckDynamic(BoundBinaryOperator node)
        {
            if (_inExpressionLambda && node.OperatorKind.IsDynamic() && !HasCSharpDynamic)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsDynamicOperation, node);
            }
        }

        public override BoundNode VisitUnaryOperator(BoundUnaryOperator node)
        {
            CheckUnsafeType(node);
            CheckLiftedUnaryOp(node);
            CheckDynamic(node);
            return base.VisitUnaryOperator(node);
        }

        public override BoundNode VisitAddressOfOperator(BoundAddressOfOperator node)
        {
            CheckUnsafeType(node);
            BoundExpression operand = node.Operand;
            if (operand.Kind == BoundKind.FieldAccess)
            {
                CheckFieldAddress((BoundFieldAccess)operand, consumerOpt: null);
            }
            return base.VisitAddressOfOperator(node);
        }

        public override BoundNode VisitIncrementOperator(BoundIncrementOperator node)
        {
            if (_inExpressionLambda && !HasCSharpExpression)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsAssignment, node);
            }

            return base.VisitIncrementOperator(node);
        }

        public override BoundNode VisitPointerElementAccess(BoundPointerElementAccess node)
        {
            NoteUnsafe(node);
            return base.VisitPointerElementAccess(node);
        }

        public override BoundNode VisitPointerIndirectionOperator(BoundPointerIndirectionOperator node)
        {
            NoteUnsafe(node);
            return base.VisitPointerIndirectionOperator(node);
        }

        public override BoundNode VisitConversion(BoundConversion node)
        {
            CheckUnsafeType(node.Operand);
            CheckUnsafeType(node);
            bool wasInExpressionLambda = _inExpressionLambda;
            bool oldReportedUnsafe = _reportedUnsafe;
            switch (node.ConversionKind)
            {
                case ConversionKind.MethodGroup:
                    CheckMethodGroup((BoundMethodGroup)node.Operand, node.Conversion.Method, parentIsConversion: true);

                    return node;

                case ConversionKind.AnonymousFunction:
                    if (!wasInExpressionLambda && node.Type.IsExpressionTree())
                    {
                        _inExpressionLambda = true;
                        // we report "unsafe in expression tree" at most once for each expression tree
                        _reportedUnsafe = false;
                    }
                    break;

                case ConversionKind.ImplicitDynamic:
                case ConversionKind.ExplicitDynamic:
                    if (_inExpressionLambda && !HasCSharpDynamic)
                    {
                        Error(ErrorCode.ERR_ExpressionTreeContainsDynamicOperation, node);
                    }
                    break;

                case ConversionKind.ExplicitTuple:
                case ConversionKind.ExplicitTupleLiteral:
                case ConversionKind.ImplicitTuple:
                case ConversionKind.ImplicitTupleLiteral:
                    if (_inExpressionLambda)
                    {
                        Error(ErrorCode.ERR_ExpressionTreeContainsTupleConversion, node);
                    }
                    break;

                default:
                    break;
            }

            var result = base.VisitConversion(node);
            _inExpressionLambda = wasInExpressionLambda;
            _reportedUnsafe = oldReportedUnsafe;
            return result;
        }

        public override BoundNode VisitDelegateCreationExpression(BoundDelegateCreationExpression node)
        {
            if (node.Argument.Kind != BoundKind.MethodGroup)
            {
                this.Visit(node.Argument);
            }
            else
            {
                CheckMethodGroup((BoundMethodGroup)node.Argument, node.MethodOpt, parentIsConversion: true);
            }

            return null;
        }

        public override BoundNode VisitMethodGroup(BoundMethodGroup node)
        {
            CheckMethodGroup(node, method: null, parentIsConversion: false);
            return null;
        }

        private void CheckMethodGroup(BoundMethodGroup node, MethodSymbol method, bool parentIsConversion)
        {
            // Formerly reported ERR_MemGroupInExpressionTree when this occurred, but the expanded 
            // ERR_LambdaInIsAs makes this impossible (since the node will always be wrapped in
            // a failed conversion).
            Debug.Assert(!(!parentIsConversion && _inExpressionLambda));

            if (_inExpressionLambda && (node.LookupSymbolOpt as MethodSymbol)?.MethodKind == MethodKind.LocalFunction)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsLocalFunction, node);
            }

            CheckReceiverIfField(node.ReceiverOpt);
            CheckReferenceToMethodIfLocalFunction(node, method);

            if (method is null || method.RequiresInstanceReceiver)
            {
                Visit(node.ReceiverOpt);
            }
        }

        public override BoundNode VisitNameOfOperator(BoundNameOfOperator node)
        {
            // The nameof(...) operator collapses to a constant in an expression tree,
            // so it does not matter what is recursively within it.
            return node;
        }

        public override BoundNode VisitNullCoalescingOperator(BoundNullCoalescingOperator node)
        {
            if (_inExpressionLambda && (node.LeftOperand.IsLiteralNull() || node.LeftOperand.IsLiteralDefault()))
            {
                // TODO: Investigate this restriction.
                Error(ErrorCode.ERR_ExpressionTreeContainsBadCoalesce, node.LeftOperand);
            }

            return base.VisitNullCoalescingOperator(node);
        }

        public override BoundNode VisitNullCoalescingAssignmentOperator(BoundNullCoalescingAssignmentOperator node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_ExpressionTreeCantContainNullCoalescingAssignment, node);
            }

            return base.VisitNullCoalescingAssignmentOperator(node);
        }

        public override BoundNode VisitDynamicInvocation(BoundDynamicInvocation node)
        {
            if (_inExpressionLambda)
            {
                if (!HasCSharpDynamic)
                {
                    Error(ErrorCode.ERR_ExpressionTreeContainsDynamicOperation, node);
                }

                // avoid reporting errors for the method group:
                if (node.Expression.Kind == BoundKind.MethodGroup)
                {
                    return base.VisitMethodGroup((BoundMethodGroup)node.Expression);
                }
            }

            return base.VisitDynamicInvocation(node);
        }

        public override BoundNode VisitDynamicIndexerAccess(BoundDynamicIndexerAccess node)
        {
            if (_inExpressionLambda && !HasCSharpDynamic)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsDynamicOperation, node);
            }

            CheckReceiverIfField(node.ReceiverOpt);
            return base.VisitDynamicIndexerAccess(node);
        }

        public override BoundNode VisitDynamicMemberAccess(BoundDynamicMemberAccess node)
        {
            if (_inExpressionLambda && !HasCSharpDynamic)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsDynamicOperation, node);
            }

            return base.VisitDynamicMemberAccess(node);
        }

        public override BoundNode VisitDynamicCollectionElementInitializer(BoundDynamicCollectionElementInitializer node)
        {
            if (_inExpressionLambda && !HasCSharpDynamic)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsDynamicOperation, node);
            }

            return base.VisitDynamicCollectionElementInitializer(node);
        }

        public override BoundNode VisitDynamicObjectCreationExpression(BoundDynamicObjectCreationExpression node)
        {
            if (_inExpressionLambda)
            {
                // TODO: Can we support initializers in combination with dynamic?
                if (node.InitializerExpressionOpt != null)
                {
                    Error(ErrorCode.ERR_ExpressionTreeContainsDynamicOperation, node);
                }
            }

            return base.VisitDynamicObjectCreationExpression(node);
        }

        public override BoundNode VisitIsPatternExpression(BoundIsPatternExpression node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsIsMatch, node);
            }

            return base.VisitIsPatternExpression(node);
        }

        public override BoundNode VisitConvertedTupleLiteral(BoundConvertedTupleLiteral node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsTupleLiteral, node);
            }

            return base.VisitConvertedTupleLiteral(node);
        }

        public override BoundNode VisitTupleLiteral(BoundTupleLiteral node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsTupleLiteral, node);
            }

            return base.VisitTupleLiteral(node);
        }

        public override BoundNode VisitTupleBinaryOperator(BoundTupleBinaryOperator node)
        {
            if (_inExpressionLambda)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsTupleBinOp, node);
            }

            return base.VisitTupleBinaryOperator(node);
        }

        public override BoundNode VisitThrowExpression(BoundThrowExpression node)
        {
            if (_inExpressionLambda && !HasCSharpExpression)
            {
                Error(ErrorCode.ERR_ExpressionTreeContainsThrowExpression, node);
            }

            return base.VisitThrowExpression(node);
        }
    }
}
