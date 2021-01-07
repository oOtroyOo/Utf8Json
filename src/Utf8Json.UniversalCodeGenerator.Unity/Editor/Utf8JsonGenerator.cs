using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Configuration;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Utf8Json.CodeGenerator.Generator;
using Utf8Json.Resolvers;
using Utf8Json.UniversalCodeGenerator;

namespace Utf8Json
{
    [InitializeOnLoad]
    public class Utf8JsonGenerator
    {
        public static string GenPath = "Assets/Scripts/Formatters";
        public static string NameSpace = "Utf8Json.Formatters";

        private static List<Type> typesToGen = new List<Type>();
        private static PlayModeStateChange playmodeState = PlayModeStateChange.EnteredEditMode;

        static IJsonFormatterResolver[] CompositeResolverBase = new[]
        {
            BuiltinResolver.Instance, // Builtin
#if !NETSTANDARD
            Utf8Json.Unity.UnityResolver.Instance,
#endif
            EnumResolver.Default,     // Enum(default => string)
            DynamicGenericResolver.Instance, // T[], List<T>, etc...
            AttributeFormatterResolver.Instance // [JsonFormatter]
        };
        static Utf8JsonGenerator()
        {
            EditorApplication.playModeStateChanged += EditorApplication_playModeStateChanged;
        }

        private static void EditorApplication_playModeStateChanged(PlayModeStateChange obj)
        {
            playmodeState = obj;
            if (obj == PlayModeStateChange.EnteredEditMode)
            {
                if (typesToGen.Count > 0)
                    GenTypes();
            }
        }

        public static void GenType<T>()
        {
            GenType(typeof(T));
        }
        public static void GenType(Type type)
        {
            lock (typesToGen)
            {
                if (typesToGen.Contains(type))
                    return;
            }
            if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
            {
                return;
            }

            if (type.GenericTypeArguments.Length > 0)
            {
                foreach (Type argument in type.GenericTypeArguments)
                {
                    GenType(argument);
                }
                return;
            }

            if (type.HasElementType)
            {
                GenType(type.GetElementType());
                return;
            }

            if (InvokeGetFormatter(type) != null) return;
            lock (typesToGen)
            {
                typesToGen.Add(type);
            }
            var fields = (from fieldInfo in type.GetFields()
                          where !fieldInfo.IsStatic
                                && !fieldInfo.IsDefined(typeof(IgnoreDataMemberAttribute))
                                && !fieldInfo.IsDefined(typeof(NonSerializedAttribute))
                          select fieldInfo);
            foreach (FieldInfo fieldInfo in fields)
            {
                GenType(fieldInfo.FieldType);
            }
            if (playmodeState == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.delayCall += GenTypes;
                return;
            }

        }

        private static void GenTypes()
        {
            if (typesToGen.Count > 0)
            {

                for (int i = 0; i < typesToGen.Count; i++)
                {
                    GenTypeInternal(typesToGen[i]);
                }

                typesToGen.Clear();

                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();

            }
        }

        private static void GenTypeInternal(Type type)
        {

            string path = GenPath + "/" + type.Name + "Formatter.cs";
            Debug.Log("Gen " + type.FullName + "\n" + path);

            //if (File.Exists(path))
            //    return;
            FormatterTemplate formatterTemplate = new FormatterTemplate();
            formatterTemplate.Namespace = NameSpace;
            var fields = (from fieldInfo in type.GetFields()
                          where !fieldInfo.IsStatic
                                && !fieldInfo.IsDefined(typeof(IgnoreDataMemberAttribute))
                                && !fieldInfo.IsDefined(typeof(NonSerializedAttribute))
                          select fieldInfo)
                .ToArray();
            var members = (from fieldInfo in fields
                           select new MemberSerializationInfo()
                           {
                               IsField = true,
                               Name = fieldInfo.Name,
                               MemberName = fieldInfo.Name,
                               Type = GetPrimitiveTypeName(fieldInfo.FieldType),
                               ShortTypeName = fieldInfo.FieldType.Name,
                               IsReadable = true,
                               IsWritable = true
                           })
                .ToArray();
            ObjectSerializationInfo objectSerializationInfo = new ObjectSerializationInfo()
            {
                Members = members,
                Name = type.Name,
                FullName = type.FullName,
                Namespace = type.Namespace,
                IsClass = type.IsClass,
                HasConstructor = true,
                ConstructorParameters = new MemberSerializationInfo[0]
            };
            formatterTemplate.objectSerializationInfos = new[] { objectSerializationInfo };

            var sb = new StringBuilder();
            sb.AppendLine(formatterTemplate.TransformText());
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString());

        }

        private static IJsonFormatter InvokeGetFormatter(Type fieldType)
        {
            //MethodInfo methodInfo = JsonSerializer.DefaultResolver.GetType().GetMethod("GetFormatter");
            //MethodInfo getformatter = methodInfo.MakeGenericMethod(fieldType);
            //var formatter = getformatter.Invoke(JsonSerializer.DefaultResolver, null);
            //Debug.Log("Get Formatter " + formatter);
            IJsonFormatter[] CompositeFormatters = (IJsonFormatter[])typeof(CompositeResolver).GetField("formatters", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            IJsonFormatterResolver[] CompositeResolvers = (IJsonFormatterResolver[])typeof(CompositeResolver).GetField("resolvers", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

            foreach (var formatter in CompositeFormatters)
            {
                foreach (var implInterface in formatter.GetType().GetTypeInfo().ImplementedInterfaces)
                {
                    var ti = implInterface.GetTypeInfo();
                    if (ti.IsGenericType && ti.GenericTypeArguments[0] == fieldType)
                    {
                        return formatter;
                    }
                }
            }
            foreach (IJsonFormatterResolver jsonFormatterResolver in CompositeResolverBase.Union(CompositeResolvers))
            {
                MethodInfo methodInfo = typeof(IJsonFormatterResolver).GetMethod("GetFormatter");
                MethodInfo getformatter = methodInfo.MakeGenericMethod(fieldType);
                IJsonFormatter jsonFormatter = getformatter.Invoke(jsonFormatterResolver, null) as IJsonFormatter;
                if (jsonFormatter != null)
                {
                    return jsonFormatter;
                }
            }

            return null;
        }

        private static string GetPrimitiveTypeName(Type type)
        {
            /*
               "short",
            "int",
            "long",
            "ushort",
            "uint",
            "ulong",
            "float",
            "double",
            "bool",
            "byte",
            "sbyte",
            //"char",
            //"global::System.DateTime",
            //"byte[]",
            "string",
             */
            if (!type.IsEnum)
                switch (Type.GetTypeCode(type))
                {
                    case TypeCode.Boolean:
                        return "bool";
                    case TypeCode.Byte:
                        return "byte";
                    case TypeCode.Double:
                        return "double";
                    case TypeCode.Int16:
                        return "short";
                    case TypeCode.Int32:
                        return "int";
                    case TypeCode.Int64:
                        return "long";
                    case TypeCode.SByte:
                        return "sbyte";
                    case TypeCode.Single:
                        return "float";
                    case TypeCode.String:
                        return "string";
                    case TypeCode.UInt16:
                        return "ushort";
                    case TypeCode.UInt32:
                        return "uint";
                    case TypeCode.UInt64:
                        return "ulong";
                }

            if (type.IsGenericType)
            {
                Type[] arguments = type.GetGenericArguments();
                return type.FullName.Split('`')[0] + "<" + string.Join(",", arguments.Select(GetPrimitiveTypeName)) + ">";
            }

            return type.FullName
                .Replace("+", ".");
        }
    }
}
