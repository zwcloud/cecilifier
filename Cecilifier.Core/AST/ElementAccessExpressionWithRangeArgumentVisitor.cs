using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using Cecilifier.Core.Extensions;
using Cecilifier.Core.Mappings;
using Cecilifier.Core.Misc;
using Cecilifier.Core.Variables;
using Mono.Cecil.Cil;

namespace Cecilifier.Core.AST;

internal class ElementAccessExpressionWithRangeArgumentVisitor : SyntaxWalkerBase
{
    internal ElementAccessExpressionWithRangeArgumentVisitor(IVisitorContext context, string ilVar, ExpressionVisitor expressionVisitor) : base(context)
    {
        _expressionVisitor = expressionVisitor;
        _ilVar = ilVar;
    }

    public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        using var _ = LineInformationTracker.Track(Context, node);
        node.Expression.Accept(_expressionVisitor);
        
        var elementAccessExpressionType = Context.SemanticModel.GetTypeInfo(node.Expression).Type.EnsureNotNull();
        _targetSpanType = elementAccessExpressionType; 
        _spanCopyVariable = AddLocalVariableWithResolvedType("localSpanCopy", Context.DefinitionVariables.GetLastOf(VariableMemberKind.Method), Context.TypeResolver.Resolve(elementAccessExpressionType));
        Context.EmitCilInstruction(_ilVar, OpCodes.Stloc, _spanCopyVariable);
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloca, _spanCopyVariable);

        node.ArgumentList.Accept(this); // Visit the argument list with ourselves.

        var sliceMethod = elementAccessExpressionType.GetMembers("Slice").OfType<IMethodSymbol>().Single(candidate => candidate.Parameters.Length == 2); // Slice(int, int)
        AddMethodCall(_ilVar, sliceMethod);
    }

    // This will handle usages like s[1..^3], i.e, RangeExpressions used in the argument
    public override void VisitRangeExpression(RangeExpressionSyntax node)
    {
        using var __ = LineInformationTracker.Track(Context, node);
        using var _ = Context.WithFlag(Constants.ContextFlags.InRangeExpression);
 
        Utils.EnsureNotNull(node.LeftOperand);
        Utils.EnsureNotNull(node.RightOperand);
                
        // Compute range start index
        node.LeftOperand.Accept(_expressionVisitor);
        var startIndexVar = AddLocalVariableWithResolvedType("startIndex", Context.DefinitionVariables.GetLastOf(VariableMemberKind.Method), Context.TypeResolver.Bcl.System.Int32);
        Context.EmitCilInstruction(_ilVar, OpCodes.Stloc, startIndexVar);
        
        // Compute number of elements to slice
        
        // compute range right index.
        node.RightOperand.Accept(_expressionVisitor);
                
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloc, startIndexVar);
        Context.EmitCilInstruction(_ilVar, OpCodes.Sub);
        var elementCountVar = AddLocalVariableWithResolvedType("elementCount", Context.DefinitionVariables.GetLastOf(VariableMemberKind.Method), Context.TypeResolver.Bcl.System.Int32);
        Context.EmitCilInstruction(_ilVar, OpCodes.Stloc, elementCountVar);
                
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloc, startIndexVar);
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloc, elementCountVar);
    }

    // This will handle usages like s[r]
    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        ProcessIndexerExpressionWithRangeAsArgument(node);
    }

    // This will handle usages like s[o.r]
    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        ProcessIndexerExpressionWithRangeAsArgument(node);
    }

    private void ProcessIndexerExpressionWithRangeAsArgument(ExpressionSyntax node)
    {
        using var _ = LineInformationTracker.Track(Context, node);
        AddMethodCall(_ilVar, _targetSpanType.GetMembers().OfType<IPropertySymbol>().Single(p => p.Name == "Length").GetMethod);
        var spanLengthVar = AddLocalVariableWithResolvedType("spanLengthVar", Context.DefinitionVariables.GetLastOf(VariableMemberKind.Method), Context.TypeResolver.Bcl.System.Int32);
        Context.EmitCilInstruction(_ilVar, OpCodes.Stloc, spanLengthVar);

        node.Accept(_expressionVisitor);

        var systemIndex = Context.RoslynTypeSystem.SystemIndex;
        var systemRange = Context.RoslynTypeSystem.SystemRange;
        var rangeVar = AddLocalVariableWithResolvedType("rangeVar", Context.DefinitionVariables.GetLastOf(VariableMemberKind.Method), Context.TypeResolver.Resolve(systemRange));
        Context.EmitCilInstruction(_ilVar, OpCodes.Stloc, rangeVar);
        
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloca, rangeVar); 
        AddMethodCall(_ilVar, systemRange.GetMembers().OfType<IPropertySymbol>().Single(p => p.Name == "Start").GetMethod);
        var indexVar = AddLocalVariableWithResolvedType("index", Context.DefinitionVariables.GetLastOf(VariableMemberKind.Method), Context.TypeResolver.Resolve(systemIndex));
        Context.EmitCilInstruction(_ilVar, OpCodes.Stloc, indexVar);        
        
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloca, indexVar);
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloc, spanLengthVar);
        AddMethodCall(_ilVar, systemIndex.GetMembers().OfType<IMethodSymbol>().Single(p => p.Name == "GetOffset"));

        var startIndexVar = AddLocalVariableWithResolvedType("startIndex", Context.DefinitionVariables.GetLastOf(VariableMemberKind.Method), Context.TypeResolver.Bcl.System.Int32);
        Context.EmitCilInstruction(_ilVar, OpCodes.Stloc, startIndexVar);
        
        // Calculate number of elements to slice.
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloca, rangeVar); 
        AddMethodCall(_ilVar, systemRange.GetMembers().OfType<IPropertySymbol>().Single(p => p.Name == "End").GetMethod);
        Context.EmitCilInstruction(_ilVar, OpCodes.Stloc, indexVar);        
        
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloca, indexVar);
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloc, spanLengthVar);
        AddMethodCall(_ilVar, systemIndex.GetMembers().OfType<IMethodSymbol>().Single(p => p.Name == "GetOffset"));
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloc, startIndexVar);
        Context.EmitCilInstruction(_ilVar, OpCodes.Sub);
        var elementCountVar = AddLocalVariableWithResolvedType("elementCount", Context.DefinitionVariables.GetLastOf(VariableMemberKind.Method), Context.TypeResolver.Bcl.System.Int32);
        Context.EmitCilInstruction(_ilVar, OpCodes.Stloc, elementCountVar);

        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloca, _spanCopyVariable);
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloc, startIndexVar);
        Context.EmitCilInstruction(_ilVar, OpCodes.Ldloc, elementCountVar);
    }

    private readonly ExpressionVisitor _expressionVisitor;
    private string _spanCopyVariable;
    private readonly string _ilVar;
    private ITypeSymbol _targetSpanType; // Span<T> in which indexer is being invoked
}
