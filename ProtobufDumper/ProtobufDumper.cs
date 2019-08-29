using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using google.protobuf;
using ProtoBuf;

namespace ProtobufDumper
{
    internal class ProtobufDumper
    {
        public delegate void ProcessProtobuf(string name, string buffer);

        private readonly Stack<string> _messageNameStack;
        private readonly Dictionary<string, ProtoNode> _protobufMap;

        private readonly List<FileDescriptorProto> _protobufs;
        private readonly Dictionary<string, ProtoTypeNode> _protobufTypeMap;

        public ProtobufDumper(List<FileDescriptorProto> protobufs)
        {
            _protobufs = protobufs;
            _messageNameStack = new Stack<string>();
            _protobufMap = new Dictionary<string, ProtoNode>();
            _protobufTypeMap = new Dictionary<string, ProtoTypeNode>();
        }

        private ProtoTypeNode GetOrCreateTypeNode(string name, FileDescriptorProto proto = null, object source = null)
        {
            if (!_protobufTypeMap.TryGetValue(name, out var node))
            {
                node = new ProtoTypeNode()
                {
                    Name = name,
                    Proto = proto,
                    Source = source,
                    Defined = source != null
                };

                _protobufTypeMap.Add(name, node);
            }
            else if (source != null)
            {
                Debug.Assert(node.Defined == false);

                node.Proto = proto;
                node.Source = source;
                node.Defined = true;
            }

            return node;
        }

        public bool Analyze()
        {
            foreach (var proto in _protobufs)
            {
                var protoNode = new ProtoNode()
                {
                    Name = proto.name,
                    Proto = proto,
                    Dependencies = new List<ProtoNode>(),
                    AllPublicDependencies = new HashSet<FileDescriptorProto>(),
                    Types = new List<ProtoTypeNode>(),
                    Defined = true
                };

                _protobufMap.Add(proto.name, protoNode);

                foreach (var extension in proto.extension)
                {
                    protoNode.Types.Add(GetOrCreateTypeNode(GetPackagePath(proto.package, extension.name), proto,
                        extension));

                    if (IsNamedType(extension.type) && !string.IsNullOrEmpty(extension.type_name))
                        protoNode.Types.Add(GetOrCreateTypeNode(GetPackagePath(proto.package, extension.type_name)));

                    if (!string.IsNullOrEmpty(extension.extendee))
                        protoNode.Types.Add(GetOrCreateTypeNode(GetPackagePath(proto.package, extension.extendee)));
                }

                foreach (var enumType in proto.enum_type)
                {
                    protoNode.Types.Add(GetOrCreateTypeNode(GetPackagePath(proto.package, enumType.name), proto,
                        enumType));
                }

                foreach (var messageType in proto.message_type)
                {
                    RecursiveAnalyzeMessageDescriptor(messageType, protoNode, proto.package);
                }

                foreach (var service in proto.service)
                {
                    protoNode.Types.Add(
                        GetOrCreateTypeNode(GetPackagePath(proto.package, service.name), proto, service));

                    foreach (var method in service.method)
                    {
                        if (!string.IsNullOrEmpty(method.input_type))
                            protoNode.Types.Add(GetOrCreateTypeNode(GetPackagePath(proto.package, method.input_type)));

                        if (!string.IsNullOrEmpty(method.output_type))
                            protoNode.Types.Add(GetOrCreateTypeNode(GetPackagePath(proto.package, method.output_type)));
                    }
                }
            }

            // inspect file dependencies
            var missingDependencies = new List<ProtoNode>();

            foreach (var (key, value) in _protobufMap)
            {
                foreach (var dependency in value.Proto.dependency)
                {
                    if (dependency.StartsWith("google", StringComparison.Ordinal))
                        continue;

                    if (_protobufMap.TryGetValue(dependency, out var depends))
                    {
                        value.Dependencies.Add(depends);
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"Unknown dependency: {dependency} for {value.Proto.name}");
                        Console.ResetColor();

                        var missing = missingDependencies.Find(x => x.Name == dependency);
                        if (missing == null)
                        {
                            missing = new ProtoNode()
                            {
                                Name = dependency,
                                Proto = null,
                                Dependencies = new List<ProtoNode>(),
                                AllPublicDependencies = new HashSet<FileDescriptorProto>(),
                                Types = new List<ProtoTypeNode>(),
                                Defined = false
                            };
                            missingDependencies.Add(missing);
                        }

                        value.Dependencies.Add(missing);
                    }
                }
            }

            foreach (var depend in missingDependencies)
            {
                _protobufMap.Add(depend.Name, depend);
            }

            foreach (var (key, value) in _protobufMap)
            {
                var undefinedFiles = value.Dependencies.Where(x => !x.Defined).ToList();

                if (undefinedFiles.Count > 0)
                {
                    Console.WriteLine($"Not all dependencies were found for {key}");

                    foreach (var file in undefinedFiles)
                    {
                        var x = _protobufMap[file.Name];
                        Console.WriteLine($"Dependency not found: {file.Name}");
                    }

                    return false;
                }

                var undefinedTypes = value.Types.Where(x => !x.Defined).ToList();

                if (undefinedTypes.Count > 0)
                {
                    Console.WriteLine($"Not all types were resolved for {key}");

                    foreach (var type in undefinedTypes)
                    {
                        var x = _protobufTypeMap[type.Name];
                        Console.WriteLine($"Type not found: {type.Name}");
                    }

                    return false;
                }

                // build the list of all publicly accessible types from each file
                RecursiveAddPublicDependencies(value.AllPublicDependencies, value, 0);
            }

            return true;
        }

        private void RecursiveAddPublicDependencies(ISet<FileDescriptorProto> set, ProtoNode node, int depth)
        {
            if (depth == 0)
            {
                foreach (var dep in node.Proto.dependency)
                {
                    var depend = _protobufMap[dep];
                    set.Add(depend.Proto);
                    RecursiveAddPublicDependencies(set, depend, depth + 1);
                }
            }
            else
            {
                foreach (var idx in node.Proto.public_dependency)
                {
                    var depend = _protobufMap[node.Proto.dependency[idx]];
                    set.Add(depend.Proto);
                    RecursiveAddPublicDependencies(set, depend, depth + 1);
                }
            }
        }

        private void RecursiveAnalyzeMessageDescriptor(DescriptorProto messageType, ProtoNode protoNode,
            string packagePath)
        {
            protoNode.Types.Add(GetOrCreateTypeNode(GetPackagePath(packagePath, messageType.name), protoNode.Proto,
                messageType));

            foreach (var extension in messageType.extension)
            {
                if (!string.IsNullOrEmpty(extension.extendee))
                    protoNode.Types.Add(GetOrCreateTypeNode(GetPackagePath(packagePath, extension.extendee)));
            }

            foreach (var enumType in messageType.enum_type)
            {
                protoNode.Types.Add(GetOrCreateTypeNode(
                    GetPackagePath(GetPackagePath(packagePath, messageType.name), enumType.name),
                    protoNode.Proto, enumType));
            }

            foreach (var field in messageType.field)
            {
                if (IsNamedType(field.type) && !string.IsNullOrEmpty(field.type_name))
                    protoNode.Types.Add(GetOrCreateTypeNode(GetPackagePath(packagePath, field.type_name)));

                if (!string.IsNullOrEmpty(field.extendee))
                    protoNode.Types.Add(GetOrCreateTypeNode(GetPackagePath(packagePath, field.extendee)));
            }

            foreach (var nested in messageType.nested_type)
            {
                RecursiveAnalyzeMessageDescriptor(nested, protoNode, GetPackagePath(packagePath, messageType.name));
            }
        }

        public void DumpFiles(ProcessProtobuf callback)
        {
            foreach (var proto in _protobufs)
            {
                var sb = new StringBuilder();
                DumpFileDescriptor(proto, sb);

                callback(proto.name, sb.ToString());
            }
        }

        private void DumpFileDescriptor(FileDescriptorProto proto, StringBuilder sb)
        {
            if (!string.IsNullOrEmpty(proto.package))
                PushDescriptorName(proto);

            var marker = false;

            var depSet = new HashSet<string>(proto.dependency);
            var dep = _protobufMap[proto.name].AllPublicDependencies;
            foreach (var (_, value) in _protobufTypeMap)
            {
                if (dep.Contains(value.Proto) && value.Source is FieldDescriptorProto field)
                {
                    if (!string.IsNullOrEmpty(field.extendee))
                    {
                        var path = value.Proto.name;
                        if (depSet.Add(path))
                        {
                            proto.dependency.Add(path);
                        }
                    }
                }
            }
            
            for (var i = 0; i < proto.dependency.Count; i++)
            {
                var dependency = proto.dependency[i];
                var modifier = proto.public_dependency.Contains(i) ? "public " : "";
                sb.AppendLine($"import {modifier}\"{dependency}\";");
                marker = true;
            }

            if (marker)
            {
                sb.AppendLine();
                marker = false;
            }

            if (!string.IsNullOrEmpty(proto.package))
            {
                sb.AppendLine($"package {proto.package};");
                marker = true;
            }

            if (marker)
            {
                sb.AppendLine();
                marker = false;
            }

            foreach (var (key, value) in DumpOptions(proto, proto.options))
            {
                sb.AppendLine($"option {key} = {value};");
                marker = true;
            }

            if (marker)
            {
                sb.AppendLine();
            }

            DumpExtensionDescriptor(proto, proto.extension, sb, string.Empty);

            foreach (var field in proto.enum_type)
            {
                DumpEnumDescriptor(proto, field, sb, 0);
            }

            foreach (var message in proto.message_type)
            {
                DumpDescriptor(proto, message, sb, 0);
            }

            foreach (var service in proto.service)
            {
                DumpService(proto, service, sb);
            }

            if (!string.IsNullOrEmpty(proto.package))
                PopDescriptorName();
        }

        private Dictionary<string, string> DumpOptions(FileDescriptorProto source, FileOptions options)
        {
            var optionsKv = new Dictionary<string, string>();

            if (options == null)
                return optionsKv;

            if (options.deprecatedSpecified)
                optionsKv.Add("deprecated", options.deprecated ? "true" : "false");
            if (options.optimize_forSpecified)
                optionsKv.Add("optimize_for", $"{options.optimize_for}");
            if (options.cc_generic_servicesSpecified)
                optionsKv.Add("cc_generic_services", options.cc_generic_services ? "true" : "false");
            if (options.go_packageSpecified)
                optionsKv.Add("go_package", $"\"{options.go_package}\"");
            if (options.java_packageSpecified)
                optionsKv.Add("java_package", $"\"{options.java_package}\"");
            if (options.java_outer_classnameSpecified)
                optionsKv.Add("java_outer_classname", $"\"{options.java_outer_classname}\"");
            if (options.java_generate_equals_and_hashSpecified)
                optionsKv.Add("java_generate_equals_and_hash",
                    options.java_generate_equals_and_hash ? "true" : "false");
            if (options.java_generic_servicesSpecified)
                optionsKv.Add("java_generic_services", options.java_generic_services ? "true" : "false");
            if (options.java_multiple_filesSpecified)
                optionsKv.Add("java_multiple_files", options.java_multiple_files ? "true" : "false");
            if (options.java_string_check_utf8Specified)
                optionsKv.Add("java_string_check_utf8", options.java_string_check_utf8 ? "true" : "false");
            if (options.py_generic_servicesSpecified)
                optionsKv.Add("py_generic_services", options.py_generic_services ? "true" : "false");

            DumpOptionsMatching(source, ".google.protobuf.FileOptions", options, optionsKv);

            return optionsKv;
        }

        private Dictionary<string, string> DumpOptions(FileDescriptorProto source, FieldOptions options)
        {
            var optionsKv = new Dictionary<string, string>();

            if (options == null)
                return optionsKv;

            if (options.ctypeSpecified)
                optionsKv.Add("ctype", $"{options.ctype}");
            if (options.deprecatedSpecified)
                optionsKv.Add("deprecated", options.deprecated ? "true" : "false");
            if (options.lazySpecified)
                optionsKv.Add("lazy", options.lazy ? "true" : "false");
            if (options.packedSpecified)
                optionsKv.Add("packed", options.packed ? "true" : "false");
            if (options.weakSpecified)
                optionsKv.Add("weak", options.weak ? "true" : "false");
            if (options.experimental_map_keySpecified)
                optionsKv.Add("experimental_map_key", $"\"{options.experimental_map_key}\"");

            DumpOptionsMatching(source, ".google.protobuf.FieldOptions", options, optionsKv);

            return optionsKv;
        }

        private Dictionary<string, string> DumpOptions(FileDescriptorProto source, MessageOptions options)
        {
            var optionsKv = new Dictionary<string, string>();

            if (options == null)
                return optionsKv;

            if (options.message_set_wire_formatSpecified)
                optionsKv.Add("message_set_wire_format", options.message_set_wire_format ? "true" : "false");
            if (options.no_standard_descriptor_accessorSpecified)
                optionsKv.Add("no_standard_descriptor_accessor",
                    options.no_standard_descriptor_accessor ? "true" : "false");
            if (options.deprecatedSpecified)
                optionsKv.Add("deprecated", options.deprecated ? "true" : "false");

            DumpOptionsMatching(source, ".google.protobuf.MessageOptions", options, optionsKv);

            return optionsKv;
        }

        private Dictionary<string, string> DumpOptions(FileDescriptorProto source, EnumOptions options)
        {
            var optionsKv = new Dictionary<string, string>();

            if (options == null)
                return optionsKv;

            if (options.allow_aliasSpecified)
                optionsKv.Add("allow_alias", options.allow_alias ? "true" : "false");
            if (options.deprecatedSpecified)
                optionsKv.Add("deprecated", options.deprecated ? "true" : "false");

            DumpOptionsMatching(source, ".google.protobuf.EnumOptions", options, optionsKv);

            return optionsKv;
        }

        private Dictionary<string, string> DumpOptions(FileDescriptorProto source, EnumValueOptions options)
        {
            var optionsKv = new Dictionary<string, string>();

            if (options == null)
                return optionsKv;

            if (options.deprecatedSpecified)
                optionsKv.Add("deprecated", options.deprecated ? "true" : "false");

            DumpOptionsMatching(source, ".google.protobuf.EnumValueOptions", options, optionsKv);

            return optionsKv;
        }


        private Dictionary<string, string> DumpOptions(FileDescriptorProto source, ServiceOptions options)
        {
            var optionsKv = new Dictionary<string, string>();

            if (options == null)
                return optionsKv;

            if (options.deprecatedSpecified)
                optionsKv.Add("deprecated", options.deprecated ? "true" : "false");

            DumpOptionsMatching(source, ".google.protobuf.ServiceOptions", options, optionsKv);

            return optionsKv;
        }

        private Dictionary<string, string> DumpOptions(FileDescriptorProto source, MethodOptions options)
        {
            var optionsKv = new Dictionary<string, string>();

            if (options == null)
                return optionsKv;

            if (options.deprecatedSpecified)
                optionsKv.Add("deprecated", options.deprecated ? "true" : "false");

            DumpOptionsMatching(source, ".google.protobuf.MethodOptions", options, optionsKv);

            return optionsKv;
        }

        private void DumpOptionsFieldRecursive(FieldDescriptorProto field, IExtensible options,
            IDictionary<string, string> optionsKv, string path)
        {
            var key = string.IsNullOrEmpty(path) ? $"({field.name})" : $"{path}.{field.name}";

            if (IsNamedType(field.type) && !string.IsNullOrEmpty(field.type_name))
            {
                var fieldData = _protobufTypeMap[field.type_name].Source;

                switch (fieldData)
                {
                    case EnumDescriptorProto enumProto:
                    {
                        if (Extensible.TryGetValue(options, field.number, out int idx))
                        {
                            var value = enumProto.value.Find(x => x.number == idx);

                            optionsKv.Add(key, value.name);
                        }

                        break;
                    }
                    case DescriptorProto messageProto:
                    {
                        var extension = Extensible.GetValue<ExtensionPlaceholder>(options, field.number);

                        if (extension != null)
                        {
                            foreach (var subField in messageProto.field)
                            {
                                DumpOptionsFieldRecursive(subField, extension, optionsKv, key);
                            }
                        }

                        break;
                    }
                }
            }
            else
            {
                if (ExtractType(options, field, out var value))
                {
                    optionsKv.Add(key, value);
                }
            }
        }

        private void DumpOptionsMatching(FileDescriptorProto source, string typeName, IExtensible options,
            IDictionary<string, string> optionsKv)
        {
            var dep = _protobufMap[source.name].AllPublicDependencies;

            foreach (var (key, value) in _protobufTypeMap)
            {
                if (dep.Contains(value.Proto) && value.Source is FieldDescriptorProto field)
                {
                    if (!string.IsNullOrEmpty(field.extendee) && field.extendee == typeName)
                    {
                        DumpOptionsFieldRecursive(field, options, optionsKv, null);
                    }
                }
            }
        }

        private void DumpExtensionDescriptor(FileDescriptorProto source, IEnumerable<FieldDescriptorProto> fields,
            StringBuilder sb, string levelSpace)
        {
            foreach (var mapping in fields.GroupBy(x => x.extendee))
            {
                if (string.IsNullOrEmpty(mapping.Key))
                    throw new Exception("Empty extendee in extension, this should not be possible");

                sb.AppendLine($"{levelSpace}extend {mapping.Key} {{");

                foreach (var field in mapping)
                {
                    sb.AppendLine($"{levelSpace}\t{BuildDescriptorDeclaration(source, field)}");
                }

                sb.AppendLine($"{levelSpace}}}");
                sb.AppendLine();
            }
        }

        private void DumpDescriptor(FileDescriptorProto source, DescriptorProto proto, StringBuilder sb, int level)
        {
            PushDescriptorName(proto);

            var levelSpace = new string('\t', level);

            sb.AppendLine($"{levelSpace}message {proto.name} {{");

            foreach (var (key, value) in DumpOptions(source, proto.options))
            {
                sb.AppendLine($"{levelSpace}\toption {key} = {value};");
            }

            foreach (var field in proto.nested_type)
            {
                DumpDescriptor(source, field, sb, level + 1);
            }

            foreach (var field in proto.enum_type)
            {
                DumpEnumDescriptor(source, field, sb, level + 1);
            }

            foreach (var field in proto.field.Where(x => !x.oneof_indexSpecified))
            {
                sb.AppendLine($"{levelSpace}\t{BuildDescriptorDeclaration(source, field)}");
            }

            for (var i = 0; i < proto.oneof_decl.Count; i++)
            {
                var oneof = proto.oneof_decl[i];
                var fields = proto.field.Where(x => x.oneof_indexSpecified && x.oneof_index == i).ToArray();

                sb.AppendLine($"{levelSpace}\toneof {oneof.name} {{");

                foreach (var field in fields)
                {
                    sb.AppendLine(
                        $"{levelSpace}\t\t{BuildDescriptorDeclaration(source, field, emitFieldLabel: false)}");
                }

                sb.AppendLine($"{levelSpace}\t}}");
            }

            if (proto.extension_range.Count > 0)
                sb.AppendLine();

            foreach (var range in proto.extension_range)
            {
                var max = Convert.ToString(range.end);

                // http://code.google.com/apis/protocolbuffers/docs/proto.html#extensions
                // If your numbering convention might involve extensions having very large numbers as tags, you can specify
                // that your extension range goes up to the maximum possible field number using the max keyword:
                // max is 2^29 - 1, or 536,870,911. 
                if (range.end >= 536870911)
                {
                    max = "max";
                }

                sb.AppendLine($"{levelSpace}\textensions {range.start} to {max};");
            }

            sb.AppendLine($"{levelSpace}}}");
            sb.AppendLine();

            PopDescriptorName();
        }

        private void DumpEnumDescriptor(FileDescriptorProto source, EnumDescriptorProto field, StringBuilder sb,
            int level)
        {
            var levelSpace = new string('\t', level);

            sb.AppendLine($"{levelSpace}enum {field.name} {{");

            foreach (var (key, value) in DumpOptions(source, field.options))
            {
                sb.AppendLine($"{levelSpace}\toption {key} = {value};");
            }

            foreach (var enumValue in field.value)
            {
                var options = DumpOptions(source, enumValue.options);

                var parameters = string.Empty;
                if (options.Count > 0)
                {
                    parameters = $" [{string.Join(", ", options.Select(kvp => $"{kvp.Key} = {kvp.Value}"))}]";
                }

                sb.AppendLine($"{levelSpace}\t{enumValue.name} = {enumValue.number}{parameters};");
            }

            sb.AppendLine($"{levelSpace}}}");
            sb.AppendLine();
        }

        private void DumpService(FileDescriptorProto source, ServiceDescriptorProto service, StringBuilder sb)
        {
            sb.AppendLine($"service {service.name} {{");

            foreach (var (key, value) in DumpOptions(source, service.options))
            {
                sb.AppendLine($"\toption {key} = {value};");
            }

            foreach (var method in service.method)
            {
                var declaration = $"\trpc {method.name} ({method.input_type}) returns ({method.output_type})";

                var options = DumpOptions(source, method.options);

                if (options.Count == 0)
                {
                    sb.AppendLine($"{declaration};");
                }
                else
                {
                    sb.AppendLine($"{declaration} {{");

                    foreach (var (key, value) in options)
                    {
                        sb.AppendLine($"\t\toption {key} = {value};");
                    }

                    sb.AppendLine("\t}");
                }
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        private string BuildDescriptorDeclaration(FileDescriptorProto source, FieldDescriptorProto field,
            bool emitFieldLabel = true)
        {
            PushDescriptorName(field);

            var type = ResolveType(field);
            var options = new Dictionary<string, string>();

            if (!string.IsNullOrEmpty(field.default_value))
            {
                var defaultValue = field.default_value;

                if (field.type == FieldDescriptorProto.Type.TYPE_STRING)
                    defaultValue = $"\"{defaultValue}\"";

                options.Add("default", defaultValue);
            }
            else if (field.type == FieldDescriptorProto.Type.TYPE_ENUM &&
                     field.label != FieldDescriptorProto.Label.LABEL_REPEATED)
            {
                var lookup = _protobufTypeMap[field.type_name];

                if (lookup.Source is EnumDescriptorProto enumDescriptor && enumDescriptor.value.Count > 0)
                    options.Add("default", enumDescriptor.value[0].name);
            }

            var fieldOptions = DumpOptions(source, field.options);
            foreach (var (key, value) in fieldOptions)
            {
                options[key] = value;
            }

            var parameters = string.Empty;
            if (options.Count > 0)
            {
                parameters = $" [{string.Join(", ", options.Select(kvp => $"{kvp.Key} = {kvp.Value}"))}]";
            }

            PopDescriptorName();

            var descriptorDeclarationBuilder = new StringBuilder();
            if (emitFieldLabel)
            {
                descriptorDeclarationBuilder.Append(GetLabel(field.label));
                descriptorDeclarationBuilder.Append(" ");
            }

            descriptorDeclarationBuilder.Append($"{type} {field.name} = {field.number}{parameters};");

            return descriptorDeclarationBuilder.ToString();
        }

        private static bool IsNamedType(FieldDescriptorProto.Type type)
        {
            return type == FieldDescriptorProto.Type.TYPE_MESSAGE || type == FieldDescriptorProto.Type.TYPE_ENUM;
        }

        private static string GetPackagePath(string package, string name)
        {
            package = package.Length == 0 || package.StartsWith('.') ? package : $".{package}";
            return name.StartsWith('.') ? name : $"{package}.{name}";
        }

        private static string GetLabel(FieldDescriptorProto.Label label)
        {
            switch (label)
            {
                case FieldDescriptorProto.Label.LABEL_REQUIRED:
                    return "required";
                case FieldDescriptorProto.Label.LABEL_REPEATED:
                    return "repeated";
                default:
                    return "optional";
            }
        }

        private static string GetType(FieldDescriptorProto.Type type)
        {
            switch (type)
            {
                case FieldDescriptorProto.Type.TYPE_INT32:
                    return "int32";
                case FieldDescriptorProto.Type.TYPE_INT64:
                    return "int64";
                case FieldDescriptorProto.Type.TYPE_SINT32:
                    return "sint32";
                case FieldDescriptorProto.Type.TYPE_SINT64:
                    return "sint64";
                case FieldDescriptorProto.Type.TYPE_UINT32:
                    return "uint32";
                case FieldDescriptorProto.Type.TYPE_UINT64:
                    return "uint64";
                case FieldDescriptorProto.Type.TYPE_STRING:
                    return "string";
                case FieldDescriptorProto.Type.TYPE_BOOL:
                    return "bool";
                case FieldDescriptorProto.Type.TYPE_BYTES:
                    return "bytes";
                case FieldDescriptorProto.Type.TYPE_DOUBLE:
                    return "double";
                case FieldDescriptorProto.Type.TYPE_ENUM:
                    return "enum";
                case FieldDescriptorProto.Type.TYPE_FLOAT:
                    return "float";
                case FieldDescriptorProto.Type.TYPE_GROUP:
                    return "GROUP";
                case FieldDescriptorProto.Type.TYPE_MESSAGE:
                    return "message";
                case FieldDescriptorProto.Type.TYPE_FIXED32:
                    return "fixed32";
                case FieldDescriptorProto.Type.TYPE_FIXED64:
                    return "fixed64";
                case FieldDescriptorProto.Type.TYPE_SFIXED32:
                    return "sfixed32";
                case FieldDescriptorProto.Type.TYPE_SFIXED64:
                    return "sfixed64";
                default:
                    return type.ToString();
            }
        }

        private static bool ExtractType(IExtensible data, FieldDescriptorProto field, out string value)
        {
            switch (field.type)
            {
                case FieldDescriptorProto.Type.TYPE_INT32:
                case FieldDescriptorProto.Type.TYPE_UINT32:
                case FieldDescriptorProto.Type.TYPE_FIXED32:
                    if (Extensible.TryGetValue(data, field.number, out uint int32))
                    {
                        value = Convert.ToString(int32);
                        return true;
                    }

                    break;
                case FieldDescriptorProto.Type.TYPE_INT64:
                case FieldDescriptorProto.Type.TYPE_UINT64:
                case FieldDescriptorProto.Type.TYPE_FIXED64:
                    if (Extensible.TryGetValue(data, field.number, out ulong int64))
                    {
                        value = Convert.ToString(int64);
                        return true;
                    }

                    break;
                case FieldDescriptorProto.Type.TYPE_SINT32:
                case FieldDescriptorProto.Type.TYPE_SFIXED32:
                    if (Extensible.TryGetValue(data, field.number, out int sint32))
                    {
                        value = Convert.ToString(sint32);
                        return true;
                    }

                    break;
                case FieldDescriptorProto.Type.TYPE_SINT64:
                case FieldDescriptorProto.Type.TYPE_SFIXED64:
                    if (Extensible.TryGetValue(data, field.number, out long sint64))
                    {
                        value = Convert.ToString(sint64);
                        return true;
                    }

                    break;
                case FieldDescriptorProto.Type.TYPE_STRING:
                    if (Extensible.TryGetValue(data, field.number, out string str))
                    {
                        value = $"\"{str}\"";
                        return true;
                    }

                    break;
                case FieldDescriptorProto.Type.TYPE_BOOL:
                    if (Extensible.TryGetValue(data, field.number, out bool boolean))
                    {
                        value = boolean ? "true" : "false";
                        return true;
                    }

                    break;
                case FieldDescriptorProto.Type.TYPE_BYTES:
                    if (Extensible.TryGetValue(data, field.number, out byte[] bytes))
                    {
                        value = Convert.ToString(bytes);
                        return true;
                    }

                    break;
                case FieldDescriptorProto.Type.TYPE_DOUBLE:
                    if (Extensible.TryGetValue(data, field.number, out double dbl))
                    {
                        value = Convert.ToString(dbl, CultureInfo.InvariantCulture);
                        return true;
                    }

                    break;
                case FieldDescriptorProto.Type.TYPE_FLOAT:
                    if (Extensible.TryGetValue(data, field.number, out float flt))
                    {
                        value = Convert.ToString(flt, CultureInfo.InvariantCulture);
                        return true;
                    }

                    break;
                default:
                    value = null;
                    return false;
            }

            value = null;
            return false;
        }

        private static string ResolveType(FieldDescriptorProto field)
        {
            if (IsNamedType(field.type))
            {
                return field.type_name;
            }

            return GetType(field.type);
        }

        private void PushDescriptorName(FileDescriptorProto file)
        {
            _messageNameStack.Push(file.package);
        }

        private void PushDescriptorName(DescriptorProto proto)
        {
            _messageNameStack.Push(proto.name);
        }

        private void PushDescriptorName(FieldDescriptorProto field)
        {
            _messageNameStack.Push(field.name);
        }

        private void PopDescriptorName()
        {
            _messageNameStack.Pop();
        }

        private class ProtoNode
        {
            public HashSet<FileDescriptorProto> AllPublicDependencies;
            public bool Defined;
            public List<ProtoNode> Dependencies;
            public string Name;
            public FileDescriptorProto Proto;
            public List<ProtoTypeNode> Types;
        }

        private class ProtoTypeNode
        {
            public bool Defined;
            public string Name;
            public FileDescriptorProto Proto;
            public object Source;
        }

        [ProtoContract]
        private class ExtensionPlaceholder : IExtensible
        {
            private IExtension _extensionObject;

            IExtension IExtensible.GetExtensionObject(bool createIfMissing)
            {
                return Extensible.GetExtensionObject(ref _extensionObject, createIfMissing);
            }
        }
    }
}