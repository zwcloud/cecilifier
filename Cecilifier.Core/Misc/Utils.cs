using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Cecilifier.Core.AST;
using Cecilifier.Core.Extensions;
using Microsoft.CodeAnalysis;

#nullable enable
namespace Cecilifier.Core.Misc
{
    internal struct Utils
    {
        public static string ConstructorMethodName(bool isStatic) => $".{ (isStatic ? Constants.CommonCecilConstants.StaticConstructorName : Constants.CommonCecilConstants.InstanceConstructorName)}";

        public static string ImportFromMainModule(string expression) => $"assembly.MainModule.ImportReference({expression})";

        public static string MakeGenericTypeIfAppropriate(IVisitorContext context, ISymbol memberSymbol, string backingFieldVar, string memberDeclaringTypeVar)
        {
            if (!(memberSymbol.ContainingSymbol is INamedTypeSymbol ts) || !ts.IsGenericType || !memberSymbol.IsDefinedInCurrentType(context))
                return backingFieldVar;

            //TODO: Register the following variable?
            var genTypeVar = context.Naming.GenericInstance(memberSymbol);
            context.WriteCecilExpression($"var {genTypeVar} = {memberDeclaringTypeVar}.MakeGenericInstanceType({memberDeclaringTypeVar}.GenericParameters.ToArray());");
            context.WriteNewLine();

            var fieldRefVar = context.Naming.MemberReference("fld_");
            context.WriteCecilExpression($"var {fieldRefVar} = new FieldReference({backingFieldVar}.Name, {backingFieldVar}.FieldType, {genTypeVar});");
            context.WriteNewLine();

            return fieldRefVar;
        }

        public static void EnsureNotNull([NotNull] ISymbol? symbol, string msg)
        {
            if (symbol == null)
                throw new System.NotSupportedException(msg);
        }
      
        [Conditional("DEBUG")]
        public static void EnsureNotNull([NotNull] SyntaxNode? node, [CallerArgumentExpression("node")] string? msg = null)
        {
            if (node == null)
                throw new System.NotSupportedException(msg);
        }
        public static string BackingFieldNameForAutoProperty(string propertyName) => $"<{propertyName}>k__BackingField";
    }
}
