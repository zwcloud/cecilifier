﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Cecilifier.Core.Extensions;
using Cecilifier.Core.Mappings;
using Cecilifier.Core.Misc;
using Cecilifier.Core.Naming;
using Cecilifier.Core.Variables;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil.Cil;
using static Cecilifier.Core.Misc.Utils;

namespace Cecilifier.Core.AST
{
    internal class PropertyDeclarationVisitor : SyntaxWalkerBase
    {
        private static readonly List<ParamData> NoParameters = new List<ParamData>();
        private string backingFieldVar;

        public PropertyDeclarationVisitor(IVisitorContext context) : base(context) { }

        public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            if (PropertyAlreadyProcessed(node))
                return;
            
            using var _ = LineInformationTracker.Track(Context, node);
            Context.WriteNewLine();
            Context.WriteComment($"** Property indexer **");
            
            var propertyType = ResolveType(node.Type);
            var propertyDeclaringTypeVar = Context.DefinitionVariables.GetLastOf(VariableMemberKind.Type).VariableName;
            var propName = "Item";

            AddDefaultMemberAttribute(propertyDeclaringTypeVar, propName);
            var propDefVar = AddPropertyDefinition(node, propName, propertyType);

            var indexerSymbol = Context.SemanticModel.GetDeclaredSymbol(node).EnsureNotNull();
            var paramsVar = new List<ParamData>();
            foreach (var parameter in node.ParameterList.Parameters)
            {
                var paramVar = Context.Naming.Parameter(parameter);
                paramsVar.Add(new ParamData
                {
                    VariableName = paramVar,
                    Type = Context.GetTypeInfo(parameter.Type).Type.EnsureNotNull().ToDisplayString()
                });

                var exps = CecilDefinitionsFactory.Parameter(parameter, Context.SemanticModel, propDefVar, paramVar, ResolveType(parameter.Type), parameter.Accept(DefaultParameterExtractorVisitor.Instance));
                AddCecilExpressions(Context, exps);
                Context.DefinitionVariables.RegisterNonMethod(indexerSymbol.ToDisplayString(), parameter.Identifier.ValueText, VariableMemberKind.Parameter, paramVar);
            }

            ProcessPropertyAccessors(node, propertyDeclaringTypeVar, propName, propertyType, propDefVar, paramsVar, node.ExpressionBody);

            AddCecilExpression($"{propertyDeclaringTypeVar}.Properties.Add({propDefVar});");
            
            HandleAttributesInMemberDeclaration(node.AttributeLists, TargetDoesNotMatch, SyntaxKind.FieldKeyword, propDefVar); // Normal property attrs
            HandleAttributesInMemberDeclaration(node.AttributeLists, TargetMatches,      SyntaxKind.FieldKeyword, backingFieldVar); // [field: attr], i.e, attr belongs to the backing field.
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            if (PropertyAlreadyProcessed(node))
                return;
            
            using var _ = LineInformationTracker.Track(Context, node);
            Context.WriteNewLine();
            Context.WriteComment($"** Property: {node.Identifier} **");
            var propertyType = ResolveType(node.Type);
            var propertyDeclaringTypeVar = Context.DefinitionVariables.GetLastOf(VariableMemberKind.Type).VariableName;
            var propName = node.Identifier.ValueText;

            var propDefVar = AddPropertyDefinition(node, propName, propertyType);
            ProcessPropertyAccessors(node, propertyDeclaringTypeVar, propName, propertyType, propDefVar, NoParameters, node.ExpressionBody);

            AddCecilExpression($"{propertyDeclaringTypeVar}.Properties.Add({propDefVar});");
            
            HandleAttributesInMemberDeclaration(node.AttributeLists, TargetDoesNotMatch, SyntaxKind.FieldKeyword, propDefVar); // Normal property attrs
            HandleAttributesInMemberDeclaration(node.AttributeLists, TargetMatches,      SyntaxKind.FieldKeyword, backingFieldVar); // [field: attr], i.e, attr belongs to the backing field.
        }

        private bool PropertyAlreadyProcessed(BasePropertyDeclarationSyntax node)
        {
            var propInfo = (IPropertySymbol) Context.SemanticModel.GetDeclaredSymbol(node);
            if (propInfo == null)
                return false;
            
            // check the methods of the property because we do not register the property itself, only its methods. 
            var methodToCheck = propInfo?.GetMethod ?? propInfo?.SetMethod;
            var found = Context.DefinitionVariables.GetMethodVariable(methodToCheck.AsMethodDefinitionVariable());
            return found.IsValid;
        }

        private void AddDefaultMemberAttribute(string definitionVar, string value)
        {
            var ctorVar = Context.Naming.MemberReference("ctor");
            var customAttrVar = Context.Naming.CustomAttribute("DefaultMember"); 
            
            var exps = new[]
            {
                $"var {ctorVar} = {ImportFromMainModule("typeof(System.Reflection.DefaultMemberAttribute).GetConstructor(new Type[] { typeof(string) })")};",
                $"var {customAttrVar} = new CustomAttribute({ctorVar});",
                $"{customAttrVar}.ConstructorArguments.Add(new CustomAttributeArgument({Context.TypeResolver.Bcl.System.String}, \"{value}\"));",
                $"{definitionVar}.CustomAttributes.Add({customAttrVar});"
            };

            AddCecilExpressions(Context, exps);
        }
        
        private void ProcessPropertyAccessors(BasePropertyDeclarationSyntax node, string propertyDeclaringTypeVar, string propName, string propertyType, string propDefVar, List<ParamData> parameters, ArrowExpressionClauseSyntax? arrowExpression)
        {
            using var _ = LineInformationTracker.Track(Context, node);
            var propertySymbol = (IPropertySymbol) Context.SemanticModel.GetDeclaredSymbol(node);
            var accessorModifiers = node.Modifiers.MethodModifiersToCecil((targetEnum, modifiers, defaultAccessibility) => ModifiersToCecil(modifiers, targetEnum, defaultAccessibility), "MethodAttributes.SpecialName", propertySymbol.GetMethod ?? propertySymbol.SetMethod);

            var declaringType = node.ResolveDeclaringType<TypeDeclarationSyntax>();
            if (arrowExpression != null)
            {
                AddExpressionBodiedGetterMethod(propertySymbol.HasCovariantGetter());
                return;
            }
            
            foreach (var accessor in node.AccessorList!.Accessors)
            {
                Context.WriteNewLine();
                switch (accessor.Keyword.Kind())
                {
                    case SyntaxKind.GetKeyword:
                        AddGetterMethod(accessor, propertySymbol);
                        break;

                    case SyntaxKind.InitKeyword:
                        Context.WriteComment(" Init");
                        var setterReturnType = $"new RequiredModifierType({ImportExpressionForType(typeof(IsExternalInit))}, {Context.TypeResolver.Bcl.System.Void})";
                        AddSetterMethod(setterReturnType, accessor);
                        break;
                    
                    case SyntaxKind.SetKeyword:
                        Context.WriteComment(" Setter");
                        AddSetterMethod(Context.TypeResolver.Bcl.System.Void, accessor);
                        break; 
                    default:
                        throw new NotImplementedException($"Accessor: {accessor.Keyword}");
                }
            }

            void AddBackingFieldIfNeeded(AccessorDeclarationSyntax accessor, bool hasInitProperty)
            {
                if (backingFieldVar != null)
                    return;

                backingFieldVar = Context.Naming.FieldDeclaration(node);
                var modifiers = ModifiersToCecil(accessor.Modifiers, "FieldAttributes", "Private");
                if (hasInitProperty) 
                    modifiers = modifiers.AppendModifier("FieldAttributes.InitOnly");

                if (propertySymbol.IsStatic)
                    modifiers = modifiers.AppendModifier("FieldAttributes.Static");
                
                var backingFieldExps = CecilDefinitionsFactory.Field(Context, propertySymbol.ContainingSymbol.ToDisplayString() , propertyDeclaringTypeVar, backingFieldVar, Utils.BackingFieldNameForAutoProperty(propName), propertyType, modifiers);
                AddCecilExpressions(Context, backingFieldExps);
            }

            void AddSetterMethod(string setterReturnType, AccessorDeclarationSyntax accessor)
            {
                var setMethodVar = Context.Naming.SyntheticVariable("set", ElementKind.LocalVariable);
                
                var localParams = new List<string>(parameters.Select(p => p.Type));
                localParams.Add(Context.GetTypeInfo(node.Type).Type.ToDisplayString()); // Setters always have at least one `value` parameter.
                using (Context.DefinitionVariables.WithCurrentMethod(declaringType.Identifier.Text, $"set_{propName}", localParams.ToArray(), setMethodVar))
                {
                    var ilSetVar = Context.Naming.ILProcessor("set");

                    //TODO : NEXT : try to use CecilDefinitionsFactory.Method()
                    AddCecilExpression($"var {setMethodVar} = new MethodDefinition(\"set_{propName}\", {accessorModifiers}, {setterReturnType});");
                    parameters.ForEach(p => AddCecilExpression($"{setMethodVar}.Parameters.Add({p.VariableName});"));
                    AddCecilExpression($"{propertyDeclaringTypeVar}.Methods.Add({setMethodVar});");

                    AddCecilExpression($"{setMethodVar}.Body = new MethodBody({setMethodVar});");
                    AddCecilExpression($"{propDefVar}.SetMethod = {setMethodVar};");

                    AddCecilExpression($"{setMethodVar}.Parameters.Add(new ParameterDefinition(\"value\", ParameterAttributes.None, {propertyType}));");
                    AddCecilExpression($"var {ilSetVar} = {setMethodVar}.Body.GetILProcessor();");

                    if (propertySymbol.ContainingType.TypeKind == TypeKind.Interface)
                        return;

                    if (accessor.Body == null && accessor.ExpressionBody == null) //is this an auto property ?
                    {
                        AddBackingFieldIfNeeded(accessor, node.AccessorList.Accessors.Any(acc => acc.IsKind(SyntaxKind.InitAccessorDeclaration)));

                        Context.EmitCilInstruction(ilSetVar, OpCodes.Ldarg_0);
                        if (!propertySymbol.IsStatic)
                            Context.EmitCilInstruction(ilSetVar, OpCodes.Ldarg_1);
                        
                        var operand = MakeGenericTypeIfAppropriate(Context, propertySymbol, backingFieldVar, propertyDeclaringTypeVar);
                        Context.EmitCilInstruction(ilSetVar, propertySymbol.StoreOpCodeForFieldAccess(), operand);
                    }
                    else if (accessor.Body != null)
                    {
                        StatementVisitor.Visit(Context, ilSetVar, accessor.Body);
                    }
                    else
                    {
                        ExpressionVisitor.Visit(Context, ilSetVar, accessor.ExpressionBody);
                    }

                    Context.EmitCilInstruction(ilSetVar, OpCodes.Ret);
                }
            }

            ScopedDefinitionVariable AddGetterMethodGuts(bool isCovariant, out string ilVar)
            {
                Context.WriteComment(" Getter");
                var getMethodVar = Context.Naming.SyntheticVariable("get", ElementKind.Method);
                var definitionVariable = Context.DefinitionVariables.WithCurrentMethod(declaringType.Identifier.Text, $"get_{propName}", parameters.Select(p => p.Type).ToArray(), getMethodVar);
                
                AddCecilExpression($"var {getMethodVar} = new MethodDefinition(\"get_{propName}\", {accessorModifiers}, {propertyType});");
                if (isCovariant)
                    AddCecilExpression($"{getMethodVar}.CustomAttributes.Add(new CustomAttribute(assembly.MainModule.Import(typeof(System.Runtime.CompilerServices.PreserveBaseOverridesAttribute).GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, new Type[0], null))));");

                parameters.ForEach(p => AddCecilExpression($"{getMethodVar}.Parameters.Add({p.VariableName});"));
                AddCecilExpression($"{propertyDeclaringTypeVar}.Methods.Add({getMethodVar});");

                AddCecilExpression($"{getMethodVar}.Body = new MethodBody({getMethodVar});");
                AddCecilExpression($"{propDefVar}.GetMethod = {getMethodVar};");

                if (propertySymbol.ContainingType.TypeKind != TypeKind.Interface)
                {
                    ilVar = Context.Naming.ILProcessor("get");
                    AddCecilExpression($"var {ilVar} = {getMethodVar}.Body.GetILProcessor();");
                }
                else
                {
                    ilVar = null;
                }

                return definitionVariable;
            }
            
            void AddExpressionBodiedGetterMethod(bool isCovariant)
            {
                using var _ = AddGetterMethodGuts(isCovariant, out var ilVar);
                ProcessExpressionBodiedGetter(ilVar, arrowExpression);
            }
            
            void AddGetterMethod(AccessorDeclarationSyntax accessor, IPropertySymbol propertySymbol)
            {
                using var _ = AddGetterMethodGuts(propertySymbol.HasCovariantGetter(), out var ilVar);
                if (ilVar == null)
                    return;
             
                if (accessor.Body == null && accessor.ExpressionBody == null) //is this an auto property ?
                {
                    AddBackingFieldIfNeeded(accessor, node.AccessorList.Accessors.Any(acc => acc.IsKind(SyntaxKind.InitAccessorDeclaration)));

                    if (!propertySymbol.IsStatic)
                        Context.EmitCilInstruction(ilVar, OpCodes.Ldarg_0);
                    var operand = Utils.MakeGenericTypeIfAppropriate(Context, propertySymbol, backingFieldVar, propertyDeclaringTypeVar);
                    Context.EmitCilInstruction(ilVar, propertySymbol.LoadOpCodeForFieldAccess(), operand);

                    Context.EmitCilInstruction(ilVar, OpCodes.Ret);
                }
                else if (accessor.Body != null)
                {
                    StatementVisitor.Visit(Context, ilVar, accessor.Body);
                }
                else
                {
                    ProcessExpressionBodiedGetter(ilVar, accessor.ExpressionBody);
                }
            }

            void ProcessExpressionBodiedGetter(string ilVar, ArrowExpressionClauseSyntax expression)
            {
                ExpressionVisitor.Visit(Context, ilVar, expression);
                Context.EmitCilInstruction(ilVar, OpCodes.Ret);
            }
        }

        private string AddPropertyDefinition(BasePropertyDeclarationSyntax propertyDeclarationSyntax, string propName, string propertyType)
        {
            var propDefVar = Context.Naming.PropertyDeclaration(propertyDeclarationSyntax);
            var propDefExp = $"var {propDefVar} = new PropertyDefinition(\"{propName}\", PropertyAttributes.None, {propertyType});";
            AddCecilExpression(propDefExp);

            return propDefVar;
        }
    }

    struct ParamData
    {
        public string VariableName; // the name of the variable in the generated code that holds the ParameterDefinition instance.
        public string Type;
    }
}
