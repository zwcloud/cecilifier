﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace Cecilifier.Runtime
{
	public class TypeHelpers
	{
		public static MethodReference DefaultCtorFor(TypeDefinition type)
		{
			var ctor = type.Methods.Where(m => m.IsConstructor && m.Parameters.Count == 0).SingleOrDefault();
			return ctor ?? DefaultCtorFor(type.BaseType.Resolve());
		}

		public static MethodInfo ResolveGenericMethod(string assemblyName, string declaringTypeName, string methodName, BindingFlags bindingFlags, IEnumerable<string> typeArguments, IEnumerable<ParamData> paramTypes)
		{
			var containingAssembly = Assembly.Load(assemblyName);
			var declaringType = containingAssembly.GetType(declaringTypeName);

			var typeArgumentsCount = typeArguments.Count();
			var methods = declaringType.GetMethods(bindingFlags)
				.Where(c => c.Name == methodName
				            && c.IsGenericMethodDefinition
				            && c.GetParameters().Length == paramTypes.Count()
				            && typeArgumentsCount == c.GetGenericArguments().Length);

			if (methods == null)
			{
				throw new MissingMethodException(declaringTypeName, methodName);
			}

			var paramTypesArray = paramTypes.ToArray();
			foreach (var mc in methods)
			{
				var parameters = mc.GetParameters();
				var found = true;

				for (int i = 0; i < parameters.Length; i++)
				{
					if (!CompareParameters(parameters[i], paramTypesArray[i]))
					{
						found = false;
						break;
					}
				}

				if (found)
					return mc.MakeGenericMethod(typeArguments.Select(ta => Type.GetType(ta)).ToArray());
			}
			
			return null;
		}

		public static MethodInfo ResolveMethod(string assemblyName, string declaringTypeName, string methodName, BindingFlags bindingFlags, params string[] paramTypes)
		{
			var containingAssembly = Assembly.Load(new AssemblyName(assemblyName));
			var declaringType = containingAssembly.GetType(declaringTypeName);

			return declaringType.GetMethod(methodName,
			                               bindingFlags, 
										   null,
			                               paramTypes.Select(typeName => Type.GetType(typeName)).ToArray(), 
										   null);
		}
		
		public static MethodInfo ResolveMethod(string assemblyName, string declaringTypeName, string methodName)
		{
			var containingAssembly = Assembly.Load(new AssemblyName(assemblyName));
			var declaringType = containingAssembly.GetType(declaringTypeName);

			return declaringType.GetMethod(methodName);
		}

		public static Type ResolveType(string assemblyName, string typeName)
		{
			var containingAssembly = Assembly.Load(new AssemblyName(assemblyName));
			return containingAssembly.GetType(typeName);
		}

		public static Type ResolveParameter(string assemblyName, string typeName)
		{
			var containingAssembly = Assembly.Load(new AssemblyName(assemblyName));
			return containingAssembly.GetType(typeName);
		}

		public static FieldInfo ResolveField(string declaringType, string fieldName)
		{
			var type = Type.GetType(declaringType);
			if (type == null)
			{
				throw new Exception("Could not resolve field: '" + fieldName + "'. Type '" + declaringType + "' not found.");
			}

			return type.GetField(fieldName);
		}
		
		private static bool CompareParameters(ParameterInfo candidate, ParamData original)
		{
			if (candidate.ParameterType.IsArray ^ original.IsArray)
				return false;
		    
			var candiateElementType = candidate.ParameterType.HasElementType ? candidate.ParameterType.GetElementType() : candidate.ParameterType;
			if (candiateElementType.IsGenericParameter ^ original.IsTypeParameter)
			{
				return false;
			}

			if (original.IsTypeParameter)
			{
				return candiateElementType.Name == original.FullName;;
			}
		    
			return candiateElementType.FullName == original.FullName;
		}
	}

	public struct ParamData
	{
		public string FullName { get; set; }
		public bool IsTypeParameter { get; set; }
		public bool IsArray { get; set; }
	}
}
