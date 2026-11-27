using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace VirtualDesktop.Unlock
{
    public sealed class SymbolSignature
    {
        public int LegacyToken;
        public string Key, Body;
    }

    // Reflection-only compatible: no target constructors, properties or methods
    // are invoked. Hashes keep recursive type descriptions bounded in memory.
    public sealed class CompatibilitySignatures
    {
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private readonly Dictionary<string,string> shapes = new Dictionary<string,string>();
        private static readonly Dictionary<ushort,OpCode> opcodes = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => (ushort)o.Value);

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }
        private static bool Obfuscated(string name) { return name.Any(c => Char.IsControl(c) || Char.GetUnicodeCategory(c) == UnicodeCategory.Format); }
        public string TypeKey(Type type) { return Shape(type, 3); }
        private string Shape(Type type, int depth)
        {
            if (type.IsGenericParameter) return (type.DeclaringMethod == null ? "!" : "!!") + type.GenericParameterPosition;
            if (type.HasElementType) return (type.IsByRef ? "ref:" : type.IsPointer ? "ptr:" : "array" + type.GetArrayRank() + ":") + Shape(type.GetElementType(), depth);
            if (type.IsGenericType && !type.IsGenericTypeDefinition) return Shape(type.GetGenericTypeDefinition(), depth) + "<" + String.Join(",", type.GetGenericArguments().Select(t => Shape(t, depth))) + ">";
            string name = type.FullName ?? type.ToString();
            if (!Obfuscated(name)) return name;
            string cache = type.Module.ModuleVersionId + ":" + type.MetadataToken + ":" + depth, result;
            if (shapes.TryGetValue(cache, out result)) return result;
            if (depth == 0) return type.IsValueType ? "value" : type.BaseType != null && type.BaseType.FullName == "System.MulticastDelegate" ? "delegate" : "class";
            if (type.BaseType != null && type.BaseType.FullName == "System.MulticastDelegate")
            {
                var invoke = type.GetMethod("Invoke");
                result = "delegate:" + Shape(invoke.ReturnType, depth - 1) + "(" + String.Join(",", invoke.GetParameters().Select(p => Shape(p.ParameterType, depth - 1))) + ")";
            }
            else
            {
                var fields = type.GetFields(All).OrderBy(f => f.MetadataToken).Where(f => !f.IsStatic).ToArray();
                result = (type.IsValueType ? "struct:" : "class:") + fields.Length + ":" + String.Join(";", fields.Select(f => Shape(f.FieldType, depth - 1)));
            }
            result = "shape:" + Hash(result); shapes[cache] = result; return result;
        }
        public string Key(MemberInfo member)
        {
            var type = member as Type; if (type != null) return "T:" + TypeKey(type);
            var field = member as FieldInfo;
            if (field != null)
            {
                string signature = TypeKey(field.FieldType);
                var siblings = field.DeclaringType.GetFields(All).OrderBy(f => f.MetadataToken).Where(f => f.IsStatic == field.IsStatic && TypeKey(f.FieldType) == signature).ToArray();
                int index = Array.FindIndex(siblings, f => f.MetadataToken == field.MetadataToken);
                return "F:" + TypeKey(field.DeclaringType) + ":" + signature + ":" + field.IsStatic + ":" + index;
            }
            var method = (MethodBase)member; var info = method as MethodInfo;
            return "M:" + TypeKey(method.DeclaringType) + "::" + method.Name + "(" + String.Join(",", method.GetParameters().Select(p => TypeKey(p.ParameterType))) + "):" +
                (info == null ? "ctor" : TypeKey(info.ReturnType)) + ":" + method.IsStatic + ":" + (method.IsGenericMethod ? method.GetGenericArguments().Length : 0);
        }
        public string Body(MemberInfo member)
        {
            var method = member as MethodBase; if (method == null) return "";
            var body = method.GetMethodBody(); if (body == null) return "no-body";
            var bytes = body.GetILAsByteArray(); var text = new StringBuilder();
            var typeArgs = method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            text.Append(body.InitLocals).Append(':').Append(body.MaxStackSize).Append(':');
            foreach (var local in body.LocalVariables) text.Append(TypeKey(local.LocalType)).Append(local.IsPinned).Append(';');
            foreach (var clause in body.ExceptionHandlingClauses)
            {
                text.Append(clause.Flags).Append(':').Append(clause.TryOffset).Append(':').Append(clause.TryLength).Append(':').Append(clause.HandlerOffset).Append(':').Append(clause.HandlerLength);
                if (clause.Flags == ExceptionHandlingClauseOptions.Clause) text.Append(TypeKey(clause.CatchType));
                if (clause.Flags == ExceptionHandlingClauseOptions.Filter) text.Append(clause.FilterOffset);
            }
            for (int pos = 0; pos < bytes.Length;)
            {
                ushort value = bytes[pos++]; if (value == 254) value = (ushort)(0xFE00 | bytes[pos++]);
                OpCode op; if (!opcodes.TryGetValue(value, out op)) throw new InvalidDataException("Unknown IL opcode.");
                text.Append('|').Append(op.Name).Append(':'); int size;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: size = 0; break;
                    case OperandType.InlineString: text.Append(method.Module.ResolveString(BitConverter.ToInt32(bytes, pos))); size = 4; break;
                    case OperandType.InlineField: case OperandType.InlineMethod: case OperandType.InlineType: case OperandType.InlineTok:
                        text.Append(Key(method.Module.ResolveMember(BitConverter.ToInt32(bytes, pos), typeArgs, methodArgs))); size = 4; break;
                    case OperandType.InlineSig: text.Append(Convert.ToBase64String(method.Module.ResolveSignature(BitConverter.ToInt32(bytes, pos)))); size = 4; break;
                    case OperandType.InlineSwitch: size = checked(4 + 4 * BitConverter.ToInt32(bytes, pos)); text.Append(Convert.ToBase64String(bytes, pos, size)); break;
                    case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: size = 1; text.Append(bytes[pos]); break;
                    case OperandType.InlineVar: size = 2; text.Append(Convert.ToBase64String(bytes, pos, size)); break;
                    case OperandType.InlineI8: case OperandType.InlineR: size = 8; text.Append(Convert.ToBase64String(bytes, pos, size)); break;
                    default: size = 4; text.Append(Convert.ToBase64String(bytes, pos, size)); break;
                }
                pos += size;
            }
            return Hash(text.ToString());
        }
        public SymbolSignature Describe(Module module, int token)
        {
            var member = module.ResolveMember(token); return new SymbolSignature { LegacyToken = token, Key = Key(member), Body = Body(member) };
        }
        public static List<SymbolSignature> Read(Stream input)
        {
            var result = new List<SymbolSignature>();
            using (var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
            {
                reader.MoveToContent(); if (reader.Name != "StreamerSignatures" || reader.GetAttribute("version") != "1") throw new InvalidDataException("Invalid compatibility signatures.");
                while (reader.Read()) if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name != "Symbol" || reader.Depth != 1 || result.Count >= 1024) throw new InvalidDataException("Invalid compatibility symbol.");
                    result.Add(new SymbolSignature { LegacyToken = Int32.Parse(reader.GetAttribute("legacy"), NumberStyles.HexNumber, CultureInfo.InvariantCulture), Key = Encoding.UTF8.GetString(Convert.FromBase64String(reader.GetAttribute("key"))), Body = reader.GetAttribute("body") ?? "" });
                }
            }
            return result;
        }
        public static void Write(string path, IEnumerable<SymbolSignature> signatures)
        {
            using (var writer = XmlWriter.Create(path, new XmlWriterSettings { Indent = true }))
            {
                writer.WriteStartElement("StreamerSignatures"); writer.WriteAttributeString("version", "1");
                foreach (var signature in signatures.OrderBy(s => s.LegacyToken))
                {
                    writer.WriteStartElement("Symbol"); writer.WriteAttributeString("legacy", signature.LegacyToken.ToString("X8"));
                    writer.WriteAttributeString("key", Convert.ToBase64String(Encoding.UTF8.GetBytes(signature.Key))); writer.WriteAttributeString("body", signature.Body); writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
        }
    }
}
