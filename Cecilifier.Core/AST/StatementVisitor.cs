using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Net.Http;
using Cecilifier.Core.Extensions;
using Cecilifier.Core.Mappings;
using Cecilifier.Core.Naming;
using Cecilifier.Core.Variables;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil.Cil;

namespace Cecilifier.Core.AST
{
    internal class StatementVisitor : SyntaxWalkerBase
    {
        private static string _ilVar;

        private StatementVisitor(IVisitorContext ctx) : base(ctx)
        {
        }

        internal static void Visit(IVisitorContext context, string ilVar, CSharpSyntaxNode node)
        {
            _ilVar = ilVar;
            node.Accept(new StatementVisitor(context));
        }

        public override void Visit(SyntaxNode node)
        {
            using var _ = LineInformationTracker.Track(Context, node);
            Context.WriteNewLine();
            Context.WriteComment(node.HumanReadableSummary());
            
            base.Visit(node);
        }

        public override void VisitForStatement(ForStatementSyntax node)
        {
            // Initialization
            HandleVariableDeclaration(node.Declaration);
            foreach (var init in node.Initializers)
            {
                ExpressionVisitor.Visit(Context, _ilVar, init);
            }

            var forEndLabel = Context.Naming.Label("fel");
            WriteCecilExpression(Context, $"var {forEndLabel} = {_ilVar}.Create(OpCodes.Nop);");

            var forConditionLabel = AddCilInstructionWithLocalVariable(_ilVar, OpCodes.Nop);
            
            // Condition
            ExpressionVisitor.Visit(Context, _ilVar, node.Condition);
            Context.EmitCilInstruction(_ilVar, OpCodes.Brfalse, forEndLabel);
            
            // Body
            node.Statement.Accept(this);

            // Increment
            foreach (var incrementExpression in node.Incrementors)
            {
                ExpressionVisitor.VisitAndPopIfNotConsumed(Context, _ilVar, incrementExpression);
            }
            
            Context.EmitCilInstruction(_ilVar, OpCodes.Br, forConditionLabel);
            WriteCecilExpression(Context, $"{_ilVar}.Append({forEndLabel});");
        }
    
        public override void VisitSwitchStatement(SwitchStatementSyntax node)
        {
            var switchExpressionType = ResolveExpressionType(node.Expression);
            var evaluatedExpressionVariable = AddLocalVariableToCurrentMethod("switchCondition", switchExpressionType);

            ExpressionVisitor.Visit(Context, _ilVar, node.Expression);
            Context.EmitCilInstruction(_ilVar, OpCodes.Stloc, evaluatedExpressionVariable); // stores evaluated expression in local var
            
            // Add label to end of switch
            var endOfSwitchLabel = CreateCilInstruction(_ilVar, OpCodes.Nop);
            breakToInstructionVars.Push(endOfSwitchLabel);
            
            // Write the switch conditions.
            var nextTestLabels = node.Sections.Select(_ => CreateCilInstruction(_ilVar, OpCodes.Nop)).ToArray();
            var currentLabelIndex = 0;
            foreach (var switchSection in node.Sections)
            {
                if (switchSection.Labels.First().Kind() == SyntaxKind.DefaultSwitchLabel)
                {
                    string operand = nextTestLabels[currentLabelIndex];
                    Context.EmitCilInstruction(_ilVar, OpCodes.Br, operand);   
                    continue;
                }
                
                foreach (var sectionLabel in switchSection.Labels)
                {
                    Context.WriteComment($"{sectionLabel.ToString()} (condition)");
                    Context.EmitCilInstruction(_ilVar, OpCodes.Ldloc, evaluatedExpressionVariable);
                    ExpressionVisitor.Visit(Context, _ilVar, sectionLabel);
                    string operand = nextTestLabels[currentLabelIndex];
                    Context.EmitCilInstruction(_ilVar, OpCodes.Beq_S, operand);
                    Context.WriteNewLine();
                }
                currentLabelIndex++;
            }

            // if at runtime the code hits this point it means none of the labels matched.
            // so, just jump to the end of the switch.
            Context.EmitCilInstruction(_ilVar, OpCodes.Br, endOfSwitchLabel);
            
            // Write the statements for each switch section...
            currentLabelIndex = 0;
            foreach (var switchSection in node.Sections)
            {
                Context.WriteComment($"{switchSection.Labels.First().ToString()} (code)");
                AddCecilExpression($"{_ilVar}.Append({nextTestLabels[currentLabelIndex]});");
                foreach (var statement in switchSection.Statements)
                {
                    statement.Accept(this);
                }
                Context.WriteNewLine();
                currentLabelIndex++;
            }
            
            Context.WriteComment("End of switch");
            AddCecilExpression($"{_ilVar}.Append({endOfSwitchLabel});");

            breakToInstructionVars.Pop();
        }

        public override void VisitBreakStatement(BreakStatementSyntax node)
        {
            if (breakToInstructionVars.Count == 0)
            {
                throw new InvalidOperationException("Invalid break.");
            }

            Context.EmitCilInstruction(_ilVar, OpCodes.Br, breakToInstructionVars.Peek());
        }

        public override void VisitFixedStatement(FixedStatementSyntax node)
        {
            using (Context.WithFlag(Constants.ContextFlags.Fixed))
            {
                var declaredType = Context.GetTypeInfo((PointerTypeSyntax)node.Declaration.Type).Type;
                var pointerType = (IPointerTypeSymbol) declaredType.EnsureNotNull();

                var currentMethodVar = Context.DefinitionVariables.GetLastOf(VariableMemberKind.Method);
                var localVar = node.Declaration.Variables[0];
                AddLocalVariableWithResolvedType(localVar.Identifier.Text, currentMethodVar, Context.TypeResolver.Resolve(pointerType.PointedAtType).MakeByReferenceType());
                ProcessVariableInitialization(localVar, declaredType);
            }
            
            Visit(node.Statement);
        }

        public override void VisitReturnStatement(ReturnStatementSyntax node)
        {
            ExpressionVisitor.Visit(Context, _ilVar, node);
            Context.EmitCilInstruction(_ilVar, OpCodes.Ret);
        }

        public override void VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            ExpressionVisitor.Visit(Context, _ilVar, node);
        }

        public override void VisitIfStatement(IfStatementSyntax node)
        {
            IfStatementVisitor.Visit(Context, _ilVar, node);
        }

        public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            HandleVariableDeclaration(node.Declaration);
        }

        public override void VisitTryStatement(TryStatementSyntax node)
        {

            var finallyBlockHandler = node.Finally == null ? 
                                (Action<string>) null :
                                (inst) => node.Finally.Accept(this);
            
            ProcessTryCatchFinallyBlock(node.Block, node.Catches.ToArray(), finallyBlockHandler);
        }

        public override void VisitThrowStatement(ThrowStatementSyntax node)
        {
            CecilExpressionFactory.EmitThrow(Context, _ilVar, node.Expression);
        }

        public override void VisitUsingStatement(UsingStatementSyntax node)
        {
            //https://github.com/dotnet/csharpstandard/blob/draft-v7/standard/statements.md#1214-the-using-statement
            
            ExpressionVisitor.Visit(Context, _ilVar, node.Expression);
            var localVarDef = string.Empty;

            ITypeSymbol usingType; 
            if (node.Declaration != null)
            {
                usingType = (ITypeSymbol) Context.SemanticModel.GetSymbolInfo(node.Declaration.Type).Symbol;
                HandleVariableDeclaration(node.Declaration);
                localVarDef = Context.DefinitionVariables.GetVariable(node.Declaration.Variables[0].Identifier.ValueText, VariableMemberKind.LocalVariable);
            }
            else
            {
                usingType = Context.SemanticModel.GetTypeInfo(node.Expression).Type;
                var resolvedVarType = Context.TypeResolver.Resolve(usingType);
                var methodVar = Context.DefinitionVariables.GetLastOf(VariableMemberKind.Method);
                localVarDef = AddLocalVariableWithResolvedType("tempDisp", methodVar, resolvedVarType);
                Context.EmitCilInstruction(_ilVar, OpCodes.Stloc, localVarDef);
            }
            
            void FinallyBlockHandler(string finallyEndVar)
            {
                string? lastFinallyInstructionLabel = null;
                if (usingType.TypeKind == TypeKind.TypeParameter || usingType.IsValueType)
                {
                    Context.EmitCilInstruction(_ilVar, OpCodes.Ldloca, localVarDef);
                    Context.EmitCilInstruction(_ilVar, OpCodes.Constrained, $"{localVarDef}.VariableType");
                }
                else
                {
                    lastFinallyInstructionLabel = Context.Naming.SyntheticVariable("endFinally", ElementKind.Label);
                    
                    Context.EmitCilInstruction(_ilVar, OpCodes.Ldloc, localVarDef);
                    CreateCilInstruction(_ilVar, lastFinallyInstructionLabel, OpCodes.Nop);
                    Context.EmitCilInstruction(_ilVar, OpCodes.Brfalse, lastFinallyInstructionLabel, "check if the disposable is not null");
                    Context.EmitCilInstruction(_ilVar, OpCodes.Ldloc, localVarDef);
                }

                Context.EmitCilInstruction(_ilVar, OpCodes.Callvirt, Context.RoslynTypeSystem.SystemIDisposable.GetMembers("Dispose").OfType<IMethodSymbol>().Single().MethodResolverExpression(Context));
                if (lastFinallyInstructionLabel != null)
                    AddCecilExpression($"{_ilVar}.Append({lastFinallyInstructionLabel});");
            }

            ProcessTryCatchFinallyBlock(node.Statement, Array.Empty<CatchClauseSyntax>(), FinallyBlockHandler);
        }

        public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node.Accept(new MethodDeclarationVisitor(Context));
        public override void VisitForEachStatement(ForEachStatementSyntax node) { LogUnsupportedSyntax(node); }
        public override void VisitWhileStatement(WhileStatementSyntax node) { LogUnsupportedSyntax(node); }
        public override void VisitLockStatement(LockStatementSyntax node) { LogUnsupportedSyntax(node); }
        public override void VisitUnsafeStatement(UnsafeStatementSyntax node) { LogUnsupportedSyntax(node); }
        public override void VisitCheckedStatement(CheckedStatementSyntax node) => LogUnsupportedSyntax(node);
        public override void VisitContinueStatement(ContinueStatementSyntax node) => LogUnsupportedSyntax(node);
        public override void VisitDoStatement(DoStatementSyntax node) => LogUnsupportedSyntax(node);
        public override void VisitGotoStatement(GotoStatementSyntax node) => LogUnsupportedSyntax(node);
        public override void VisitYieldStatement(YieldStatementSyntax node) { LogUnsupportedSyntax(node); }
        
        private void ProcessTryCatchFinallyBlock(CSharpSyntaxNode tryStatement, CatchClauseSyntax[] catches, Action<string> finallyBlockHandler)
        {
            var exceptionHandlerTable = new ExceptionHandlerEntry[catches.Length + (finallyBlockHandler != null ? 1 : 0)];

            var tryStartVar = AddCilInstructionWithLocalVariable(_ilVar, OpCodes.Nop);
            exceptionHandlerTable[0].TryStart = tryStartVar;

            tryStatement.Accept(this);

            var firstInstructionAfterTryCatchBlock = CreateCilInstruction(_ilVar, OpCodes.Nop);
            exceptionHandlerTable[^1].HandlerEnd = firstInstructionAfterTryCatchBlock; // sets up last handler end instruction

            Context.EmitCilInstruction(_ilVar, OpCodes.Leave, firstInstructionAfterTryCatchBlock);

            for (var i = 0; i < catches.Length; i++)
            {
                HandleCatchClause(catches[i], exceptionHandlerTable, i, firstInstructionAfterTryCatchBlock);
            }
            
            HandleFinallyClause(finallyBlockHandler, exceptionHandlerTable);

            AddCecilExpression($"{_ilVar}.Append({firstInstructionAfterTryCatchBlock});");

            WriteExceptionHandlers(exceptionHandlerTable);
        }
        
        private void WriteExceptionHandlers(ExceptionHandlerEntry[] exceptionHandlerTable)
        {
            string methodVar = Context.DefinitionVariables.GetLastOf(VariableMemberKind.Method);
            foreach (var handlerEntry in exceptionHandlerTable)
            {
                AddCecilExpression($"{methodVar}.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.{handlerEntry.Kind})");
                AddCecilExpression("{");
                if (handlerEntry.Kind == ExceptionHandlerType.Catch)
                {
                    AddCecilExpression($"    CatchType = {handlerEntry.CatchType},");
                }

                AddCecilExpression($"    TryStart = {handlerEntry.TryStart},");
                AddCecilExpression($"    TryEnd = {handlerEntry.TryEnd},");
                AddCecilExpression($"    HandlerStart = {handlerEntry.HandlerStart},");
                AddCecilExpression($"    HandlerEnd = {handlerEntry.HandlerEnd}");
                AddCecilExpression("});");
            }
        }

        private void HandleCatchClause(CatchClauseSyntax node, ExceptionHandlerEntry[] exceptionHandlerTable, int currentIndex, string firstInstructionAfterTryCatchBlock)
        {
            exceptionHandlerTable[currentIndex].Kind = ExceptionHandlerType.Catch;
            exceptionHandlerTable[currentIndex].HandlerStart = AddCilInstructionWithLocalVariable(_ilVar, OpCodes.Pop); // pops the exception object from stack...

            if (currentIndex == 0)
            {
                // The last instruction of the try block is the first instruction of the first catch block
                exceptionHandlerTable[0].TryEnd = exceptionHandlerTable[currentIndex].HandlerStart;
            }
            else
            {
                exceptionHandlerTable[currentIndex - 1].HandlerEnd = exceptionHandlerTable[currentIndex].HandlerStart;
            }

            exceptionHandlerTable[currentIndex].TryStart = exceptionHandlerTable[0].TryStart;
            exceptionHandlerTable[currentIndex].TryEnd = exceptionHandlerTable[0].TryEnd;
            exceptionHandlerTable[currentIndex].CatchType = ResolveType(node.Declaration.Type);

            VisitCatchClause(node);
            Context.EmitCilInstruction(_ilVar, OpCodes.Leave, firstInstructionAfterTryCatchBlock);
        }

        private void HandleFinallyClause(Action<string> finallyBlockHandler,ExceptionHandlerEntry[] exceptionHandlerTable)
        {
            if (finallyBlockHandler == null)
            {
                return;
            }

            var finallyEntryIndex = exceptionHandlerTable.Length - 1;

            exceptionHandlerTable[finallyEntryIndex].TryStart = exceptionHandlerTable[0].TryStart;
            exceptionHandlerTable[finallyEntryIndex].TryEnd = exceptionHandlerTable[0].TryEnd;
            exceptionHandlerTable[finallyEntryIndex].Kind = ExceptionHandlerType.Finally;

            var finallyStartVar = AddCilInstructionWithLocalVariable(_ilVar, OpCodes.Nop);
            exceptionHandlerTable[finallyEntryIndex].HandlerStart = finallyStartVar;
            exceptionHandlerTable[finallyEntryIndex].TryEnd = finallyStartVar;
            
            if (finallyEntryIndex != 0)
            {
                // We have one or more catch blocks... set the end of the last catch block as the first instruction of the *finally*
                exceptionHandlerTable[finallyEntryIndex - 1].HandlerEnd = finallyStartVar;
            }

            finallyBlockHandler(exceptionHandlerTable[finallyEntryIndex].HandlerEnd);
            Context.EmitCilInstruction(_ilVar, OpCodes.Endfinally);
        }

        private void AddLocalVariable(TypeSyntax type, VariableDeclaratorSyntax localVar, DefinitionVariable methodVar)
        {
            var resolvedVarType = type.IsVar
                ? ResolveExpressionType(localVar.Initializer?.Value)
                : ResolveType(type);
            
            AddLocalVariableWithResolvedType(localVar.Identifier.Text, methodVar, resolvedVarType);
        }

        private void ProcessVariableInitialization(VariableDeclaratorSyntax localVar, ITypeSymbol variableType)
        {
            if (localVar.Initializer == null)
                return;
            
            var localVarDef = Context.DefinitionVariables.GetVariable(localVar.Identifier.ValueText, VariableMemberKind.LocalVariable);
            if (localVar.Initializer.Value.IsKind(SyntaxKind.IndexExpression))
            {
                // code is something like `Index field = ^5`; 
                // in this case we need to load the address of the field since the expression ^5 (IndexerExpression) will result in a call to System.Index ctor (which is a value type and expects
                // the address of the value type to be in the top of the stack
                Context.EmitCilInstruction(_ilVar, OpCodes.Ldloca, localVarDef.VariableName);
            }

            if (ExpressionVisitor.Visit(Context, _ilVar, localVar.Initializer))
            {
                return;
            }

            var valueBeingAssignedIsByRef = Context.SemanticModel.GetSymbolInfo(localVar.Initializer.Value).Symbol.IsByRef();
            if (!variableType.IsByRef() && valueBeingAssignedIsByRef)
            {
                OpCode opCode = LoadIndirectOpCodeFor(variableType.SpecialType);
                Context.EmitCilInstruction(_ilVar, opCode);
            }
            
            Context.EmitCilInstruction(_ilVar, OpCodes.Stloc, localVarDef.VariableName);
        }

        private void HandleVariableDeclaration(VariableDeclarationSyntax declaration)
        {
            var variableType = Context.SemanticModel.GetTypeInfo(declaration.Type);
            var methodVar = Context.DefinitionVariables.GetLastOf(VariableMemberKind.Method);
            foreach (var localVar in declaration.Variables)
            {
                AddLocalVariable(declaration.Type, localVar, methodVar);
                ProcessVariableInitialization(localVar, variableType.Type);
            }
        }

        private struct ExceptionHandlerEntry
        {
            public ExceptionHandlerType Kind;
            public string CatchType;
            public string TryStart;
            public string TryEnd;
            public string HandlerStart;
            public string HandlerEnd;
        }
        
        // Stack with name of variables that holds instructions that a *break statement* 
        // will jump to. Each statement that supports *breaking* must push the instruction
        // target of the break and pop it back when it gets out of scope.
        private Stack<string> breakToInstructionVars = new Stack<string>();
    }
}
