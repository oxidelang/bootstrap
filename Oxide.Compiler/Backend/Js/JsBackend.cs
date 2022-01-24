using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Backend.Js;

public class JsBackend
{
    public delegate void IntrinsicMapper(JsBodyGenerator generator, StaticCallInst inst, FunctionRef key);

    public IrStore Store { get; }

    public MiddlewareManager Middleware { get; }

    public JsWriter Writer { get; private set; }

    private Dictionary<ConcreteTypeRef, uint> _typeSizes;

    private Dictionary<TypeRef, string> _dropFuncs;

    private Dictionary<QualifiedName, IntrinsicMapper> _intrinsics;

    private List<JsWriter> _functionWriters;

    public JsBackend(IrStore store, MiddlewareManager middleware)
    {
        Store = store;
        Middleware = middleware;
        _typeSizes = new Dictionary<ConcreteTypeRef, uint>();
        _typeSizes.Add(PrimitiveKind.USize.GetRef(), 4);
        _typeSizes.Add(PrimitiveKind.U8.GetRef(), 1);
        _typeSizes.Add(PrimitiveKind.I32.GetRef(), 4);
        _typeSizes.Add(PrimitiveKind.Bool.GetRef(), 1);

        _intrinsics = new Dictionary<QualifiedName, IntrinsicMapper>();
        _intrinsics.Add(QualifiedName.From("std", "size_of"), JsIntrinsics.SizeOf);
        _intrinsics.Add(QualifiedName.From("std", "bitcopy"), JsIntrinsics.Bitcopy);
    }

    public void Compile(string path)
    {
        _functionWriters = new List<JsWriter>();
        Writer = new JsWriter();
        _dropFuncs = new Dictionary<TypeRef, string>();

        // Create types
        foreach (var usedType in Middleware.Usage.UsedTypes.Values)
        {
            CreateType(usedType);
        }

        foreach (var usedFunc in Middleware.Usage.UsedFunctions.Values)
        {
            var func = Store.Lookup<Function>(usedFunc.Name);

            foreach (var version in usedFunc.Versions)
            {
                var key = new FunctionRef
                {
                    TargetMethod = new ConcreteTypeRef(usedFunc.Name, version)
                };

                var context = new GenericContext(null, func.GenericParams, version, null);

                CreateFunction(key, func, context);
            }
        }

        foreach (var fwriter in _functionWriters)
        {
            foreach (var line in fwriter.Generate().TrimEnd().Split(Environment.NewLine))
            {
                Writer.WriteLine(line);
            }
        }

        File.WriteAllText($"{path}/compiled.js", Writer.Generate());
    }

    private void CreateType(UsedType usedType)
    {
        var type = Store.Lookup<OxType>(usedType.Name);
        if (usedType.Versions.Count == 0)
        {
            throw new Exception("Type has no used versions");
        }

        foreach (var version in usedType.Versions.Values)
        {
            var concreteType = new ConcreteTypeRef(usedType.Name, version.Generics);
            var name = GenerateName(concreteType);
            Console.WriteLine($" - Generating {name}");
            Writer.Comment(name);

            var typeSize = GetSize(concreteType);
            Writer.WriteLine($"function {name}___direct(heap, value) {{");
            Writer.Indent(1);
            Writer.WriteLine("this.__heap = heap;");
            Writer.WriteLine($"this.__this = heap.alloc({typeSize});");
            Writer.WriteLine("heap.writeBlob(this.__this, value);");
            Writer.Indent(-1);
            Writer.WriteLine("}");
            Writer.WriteLine(
                $"OxideTypes.wrappers[\"{GeneratePrettyName(concreteType)}\"] = {{ type: \"type\", func: {name}___direct }};"
            );

            Writer.WriteLine($"{name}___direct.prototype.__as_ptr = function() {{");
            Writer.Indent(1);
            Writer.WriteLine($"return new {name}___ptr(this.__heap, this.__this);");
            Writer.Indent(-1);
            Writer.WriteLine("};");

            Writer.WriteLine($"{name}___direct.prototype.__drop = function() {{");
            Writer.Indent(1);
            Writer.WriteLine("this.__heap.free(this.__this);");
            Writer.Indent(-1);
            Writer.WriteLine("};");
            Writer.WriteLine($"{name}___direct.prototype.drop = {name}___direct.prototype.__drop;");

            Writer.WriteLine($"function {name}___ptr(heap, value) {{");
            Writer.Indent(1);
            Writer.WriteLine("this.__heap = heap;");
            Writer.WriteLine("this.__this = value;");
            Writer.Indent(-1);
            Writer.WriteLine("}");
            Writer.WriteLine(
                $"OxideTypes.wrappers[\"&mut {GeneratePrettyName(concreteType)}\"] = {{ type: \"type\", func: {name}___ptr }};"
            );
            Writer.WriteLine(
                $"OxideTypes.wrappers[\"&{GeneratePrettyName(concreteType)}\"] = {{ type: \"type\", func: {name}___ptr }};"
            );
            Writer.WriteLine(
                $"OxideTypes.wrappers[\"*mut {GeneratePrettyName(concreteType)}\"] = {{ type: \"type\", func: {name}___ptr }};"
            );
            Writer.WriteLine(
                $"OxideTypes.wrappers[\"*{GeneratePrettyName(concreteType)}\"] = {{ type: \"type\", func: {name}___ptr }};"
            );

            Writer.WriteLine($"function {name}___ref(heap, value) {{");
            Writer.Indent(1);
            Writer.WriteLine("this.__heap = heap;");
            Writer.WriteLine("this.__this = value;");
            Writer.Indent(-1);
            Writer.WriteLine("}");
            Writer.WriteLine(
                $"OxideTypes.wrappers[\"ref {GeneratePrettyName(concreteType)}\"] = {{ type: \"type\", func: {name}___ref }};"
            );
            Writer.WriteLine(
                $"OxideTypes.wrappers[\"weak {GeneratePrettyName(concreteType)}\"] = {{ type: \"type\", func: {name}___ref }};"
            );

            Writer.WriteLine($"function {name}___static(heap) {{");
            Writer.Indent(1);
            Writer.WriteLine("this.__heap = heap;");
            Writer.Indent(-1);
            Writer.WriteLine("}");
            Writer.WriteLine(
                $"OxideTypes.mappings[\"{GeneratePrettyName(concreteType)}\"] = {{ type: \"static_type\", func: {name}___static }};"
            );

            var context = new GenericContext(null, type.GenericParams, version.Generics, concreteType);

            if (version.DefaultImplementation != null)
            {
                CreateImplementation(context, version.DefaultImplementation);
            }

            foreach (var imp in version.Implementations.Values)
            {
                CreateImplementation(context, imp);
            }
        }
    }

    private void CreateImplementation(GenericContext context, UsedImplementation usedImp)
    {
        foreach (var usedFunc in usedImp.Functions.Values)
        {
            var resolved = Store.LookupImplementation(
                context.ThisRef,
                usedImp.Interface,
                usedFunc.Name.Parts.Single()
            );

            var impContext = new GenericContext(context, resolved.ImplementationGenerics, context.ThisRef);

            foreach (var version in usedFunc.Versions)
            {
                var key = new FunctionRef
                {
                    TargetType = context.ThisRef,
                    TargetImplementation = resolved.Interface,
                    TargetMethod = new ConcreteTypeRef(usedFunc.Name, version),
                };

                var funcContext = new GenericContext(
                    impContext,
                    resolved.Function.GenericParams,
                    version,
                    impContext.ThisRef
                );

                CreateFunction(key, resolved.Function, funcContext);
            }
        }
    }

    private void CreateFunction(FunctionRef key, Function func, GenericContext context)
    {
        if (func.IsExtern)
        {
            return;
        }

        var funcName = GenerateKeyName(key);
        var returnType = context.ResolveRef(func.ReturnType);

        bool first;

        if (key.TargetType != null)
        {
            if (context.ThisRef == null)
            {
                throw new Exception("Unexpected");
            }

            var thisName = GenerateName(context.ThisRef);
            var hasThis = func.Parameters.Count >= 1 && func.Parameters[0].IsThis;

            Writer.BeginLine();
            if (hasThis)
            {
                switch (func.Parameters[0].Type)
                {
                    case BaseTypeRef baseTypeRef:
                        throw new Exception("Unexpected");
                        break;
                    case BorrowTypeRef borrowTypeRef:
                    case PointerTypeRef pointerTypeRef:
                        foreach (var version in new[] { "ref", "direct" })
                        {
                            Writer.Write(
                                $"{thisName}___{version}.prototype.{GenerateName(key.TargetMethod)} = function(");

                            first = true;
                            foreach (var paramDef in func.Parameters.Skip(1))
                            {
                                if (!first)
                                {
                                    Writer.Write(", ");
                                }

                                first = false;

                                Writer.Write($"_{paramDef.Name}");
                            }

                            Writer.Write(") {");
                            Writer.EndLine();
                            Writer.Indent(1);

                            Writer.BeginLine();
                            Writer.Write($"return this.__as_ptr().{GenerateName(key.TargetMethod)}(");

                            first = true;
                            foreach (var paramDef in func.Parameters.Skip(1))
                            {
                                if (!first)
                                {
                                    Writer.Write(", ");
                                }

                                first = false;

                                Writer.Write($"_{paramDef.Name}");
                            }

                            Writer.Write(");");
                            Writer.EndLine();

                            Writer.Indent(-1);
                            Writer.WriteLine("};");
                        }

                        Writer.Write($"{thisName}___ptr.prototype.{GenerateName(key.TargetMethod)} = function(");
                        break;
                    case ReferenceTypeRef referenceTypeRef:
                        Writer.Write($"{thisName}___ref.prototype.{GenerateName(key.TargetMethod)} = function(");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            else
            {
                Writer.Write($"{thisName}___static.prototype.{GenerateName(key.TargetMethod)} = function(");
            }

            first = true;
            foreach (var paramDef in func.Parameters.Skip(1))
            {
                if (!first)
                {
                    Writer.Write(", ");
                }

                first = false;

                Writer.Write($"_{paramDef.Name}");
            }

            Writer.Write(") {");
            Writer.EndLine();
            Writer.Indent(1);

            Writer.BeginLine();

            Writer.Write(
                $"return OxideTypes.wrap(this.__heap, \"{(returnType != null ? GeneratePrettyName(returnType) : "void")}\", ");

            Writer.Write($"{funcName}(this.__heap{(hasThis ? ", this.__this" : "")}");
            foreach (var paramDef in func.Parameters.Skip(1))
            {
                Writer.Write($", _{paramDef.Name}");
            }

            Writer.Write("));");
            Writer.EndLine();

            Writer.Indent(-1);
            Writer.WriteLine("};");
        }
        else
        {
            Writer.WriteLine($"{funcName}_wrapper = function(heap){{");
            Writer.Indent(1);

            Writer.BeginLine();
            Writer.Write("return function(");
            first = true;
            foreach (var paramDef in func.Parameters)
            {
                if (!first)
                {
                    Writer.Write(", ");
                }

                first = false;

                Writer.Write($"_{paramDef.Name}");
            }

            Writer.Write(") {");
            Writer.EndLine();
            Writer.Indent(1);

            Writer.BeginLine();

            Writer.Write(
                $"return OxideTypes.wrap(heap, \"{(returnType != null ? GeneratePrettyName(returnType) : "void")}\", ");

            Writer.Write($"{funcName}(heap");
            foreach (var paramDef in func.Parameters)
            {
                Writer.Write($", _{paramDef.Name}");
            }

            Writer.Write("));");
            Writer.EndLine();

            Writer.Indent(-1);
            Writer.WriteLine("};");

            Writer.Indent(-1);
            Writer.WriteLine("}");

            Writer.WriteLine(
                $"OxideTypes.mappings[\"{GeneratePrettyName(key.TargetMethod)}\"] = {{ type: \"function\", func: {funcName}_wrapper }};"
            );
        }

        var original = Writer;
        Writer = new JsWriter();
        _functionWriters.Add(Writer);

        Writer.BeginLine();
        Writer.Write($"function {funcName}(heap");

        foreach (var paramDef in func.Parameters)
        {
            Writer.Write($", _{paramDef.Name}");
        }

        Writer.Write(") {");
        Writer.EndLine();
        Writer.Indent(1);

        var funcGen = new JsBodyGenerator(this);
        var lifetime = Middleware.Lifetime.FunctionLifetimes[func];
        funcGen.Compile(key, func, context, lifetime);

        Writer.Indent(-1);
        Writer.WriteLine("}");

        Writer = original;
    }

    public string GetDropFunctionRef(TypeRef typeRef)
    {
        if (_dropFuncs.TryGetValue(typeRef, out var resolved))
        {
            return resolved;
        }

        switch (typeRef)
        {
            case ConcreteTypeRef concreteTypeRef:
                return GetDropFunctionForBase(concreteTypeRef);
            case BaseTypeRef:
                throw new Exception("Unresolved type");
            case PointerTypeRef:
            case BorrowTypeRef:
                return null;
            case ReferenceTypeRef referenceTypeRef:
                return GenerateKeyName(
                    new FunctionRef
                    {
                        TargetMethod = ConcreteTypeRef.From(
                            QualifiedName.From(
                                "std",
                                referenceTypeRef.StrongRef ? "box_drop_strong" : "box_drop_weak"
                            ),
                            referenceTypeRef.InnerType
                        )
                    }
                );
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }
    }


    private string GetDropFunctionForBase(ConcreteTypeRef typeRef)
    {
        if (_dropFuncs.TryGetValue(typeRef, out var resolved))
        {
            return resolved;
        }

        var baseType = Store.Lookup(typeRef.Name);
        var dropFunc = Store.GetDropFunction(typeRef);

        switch (baseType)
        {
            case PrimitiveType:
                _dropFuncs[typeRef] = null;
                return null;
            case Variant:
            case Struct:
                break;
            case Interface @interface:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException(nameof(baseType));
        }

        var funcName = $"___drop___{GenerateName(typeRef)}";
        _dropFuncs.Add(typeRef, funcName);

        var original = Writer;
        Writer = new JsWriter();
        _functionWriters.Add(Writer);

        var size = GetSize(typeRef);

        Writer.WriteLine($"function {funcName}(heap, value) {{");
        Writer.Indent(1);
        Writer.WriteLine($"var valPtr = heap.alloc({size});");
        Writer.WriteLine(BuildStore(typeRef, "valPtr", "value"));

        if (dropFunc != null)
        {
            var targetMethod = dropFunc.TargetMethod;

            Function funcDef;
            GenericContext funcContext;

            if (dropFunc.TargetType != null)
            {
                var targetType = dropFunc.TargetType;
                var resolvedFunc = Store.LookupImplementation(
                    targetType,
                    dropFunc.TargetImplementation,
                    dropFunc.TargetMethod.Name.Parts.Single()
                );
                funcDef = resolvedFunc.Function;

                var typeObj = Store.Lookup(targetType.Name);
                funcContext = new GenericContext(
                    null,
                    typeObj.GenericParams,
                    targetType.GenericParams,
                    targetType
                );
            }
            else
            {
                funcDef = Store.Lookup<Function>(targetMethod.Name);
                funcContext = GenericContext.Default;
            }

            if (funcDef == null)
            {
                throw new Exception($"Failed to find unit for {targetMethod}");
            }

            if (targetMethod.GenericParams.Length > 0)
            {
                funcContext = new GenericContext(
                    funcContext,
                    funcDef.GenericParams,
                    targetMethod.GenericParams,
                    funcContext.ThisRef
                );
            }

            var dropFuncRef = GenerateKeyName(dropFunc);

            if (funcDef.Parameters.Count != 1)
            {
                throw new Exception("Invalid number of arguments");
            }

            var param = funcDef.Parameters[0];
            var paramType = funcContext.ResolveRef(param.Type);

            var matches = paramType switch
            {
                BorrowTypeRef borrowTypeRef => Equals(borrowTypeRef.InnerType, typeRef) && !borrowTypeRef.MutableRef,
                PointerTypeRef pointerTypeRef =>
                    Equals(pointerTypeRef.InnerType, typeRef) && !pointerTypeRef.MutableRef,
                _ => throw new ArgumentOutOfRangeException(nameof(paramType))
            };

            if (!matches)
            {
                throw new Exception($"Argument does not match parameter type for {param.Name}");
            }

            Writer.WriteLine($"{dropFuncRef}(heap, valPtr);");
        }

        var structContext = new GenericContext(null, baseType.GenericParams, typeRef.GenericParams, null);

        switch (baseType)
        {
            case Variant variant:
            {
                Writer.WriteLine("var id_value = heap.readU8(valPtr);");

                var first = true;

                Writer.BeginLine();
                for (var i = 0; i < variant.Items.Count; i++)
                {
                    var item = variant.Items[i];
                    if (item.Content == null)
                    {
                        continue;
                    }

                    var itemRef = new ConcreteTypeRef(
                        new QualifiedName(true, variant.Name.Parts.Add(item.Name)),
                        typeRef.GenericParams
                    );

                    var itemDropFunc = GetDropFunctionRef(itemRef);
                    if (itemDropFunc == null)
                    {
                        continue;
                    }

                    if (!first)
                    {
                        Writer.Write("else ");
                    }

                    first = false;

                    Writer.Write($"if (id_value === {i}) {{");
                    Writer.Indent(1);
                    Writer.EndLine();


                    Writer.WriteLine($"var item_{i}_value = {BuildLoad(itemRef, $"valPtr + 1")};");
                    Writer.WriteLine($"{itemDropFunc}(heap, item_{i}_value);");

                    Writer.Indent(-1);
                    Writer.BeginLine();
                    Writer.Write("} ");
                }

                Writer.EndLine();

                break;
            }
            case Struct structDef:
            {
                for (var i = 0; i < structDef.Fields.Count; i++)
                {
                    var fieldDef = structDef.Fields[i];
                    var fieldType = structContext.ResolveRef(fieldDef.Type);
                    var fieldDropFunc = GetDropFunctionRef(fieldType);
                    if (fieldDropFunc == null)
                    {
                        continue;
                    }

                    var offset = GetFieldOffset(typeRef, fieldDef.Name);

                    Writer.WriteLine($"var field_{i}_value = {BuildLoad(fieldType, $"valPtr + {offset}")};");
                    Writer.WriteLine($"{fieldDropFunc}(heap, field_{i}_value);");
                }

                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(baseType));
        }

        Writer.WriteLine("heap.free(valPtr);");
        Writer.Indent(-1);
        Writer.WriteLine("}");

        Writer = original;

        return funcName;
    }

    public uint GetBoxValueOffset(ConcreteTypeRef innerType)
    {
        return GetFieldOffset(ConcreteTypeRef.From(
            QualifiedName.From("std", "Box"),
            innerType
        ), "value");
    }

    public uint GetFieldOffset(ConcreteTypeRef type, string name)
    {
        var structDef = Store.Lookup<Struct>(type.Name);
        var structContext = new GenericContext(null, structDef.GenericParams, type.GenericParams, null);

        uint offset = 0;

        foreach (var fieldDef in structDef.Fields)
        {
            var fieldType = structContext.ResolveRef(fieldDef.Type);
            var fieldSize = GetSize(fieldType);

            if (fieldDef.Name == name)
            {
                return offset;
            }

            offset += fieldSize;
        }

        throw new Exception($"Unknown field {name}");
    }

    public string BuildLoad(TypeRef type, string pointer)
    {
        switch (type)
        {
            case ConcreteTypeRef concreteTypeRef:
                if (Equals(concreteTypeRef, PrimitiveKind.U8.GetRef()) ||
                    Equals(concreteTypeRef, PrimitiveKind.Bool.GetRef()))
                {
                    return $"heap.readU8({pointer})";
                }
                else if (Equals(concreteTypeRef, PrimitiveKind.USize.GetRef()))
                {
                    return $"heap.readU32({pointer})";
                }
                else if (Equals(concreteTypeRef, PrimitiveKind.I32.GetRef()))
                {
                    return $"heap.readI32({pointer})";
                }
                else
                {
                    var size = GetSize(type);
                    return $"heap.readBlob({pointer}, {size})";
                }
            case BaseTypeRef:
                throw new Exception("Not resolved");
            case BorrowTypeRef:
            case PointerTypeRef:
            case ReferenceTypeRef:
                return $"heap.readI32({pointer})";
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    public string BuildStore(TypeRef type, string pointer, string value, string heapName = "heap")
    {
        switch (type)
        {
            case ConcreteTypeRef concreteTypeRef:
                if (Equals(concreteTypeRef, PrimitiveKind.U8.GetRef()) ||
                    Equals(concreteTypeRef, PrimitiveKind.Bool.GetRef()))
                {
                    return $"{heapName}.writeU8({pointer}, {value});";
                }
                else if (Equals(concreteTypeRef, PrimitiveKind.USize.GetRef()))
                {
                    return $"{heapName}.writeU32({pointer}, {value});";
                }
                else if (Equals(concreteTypeRef, PrimitiveKind.I32.GetRef()))
                {
                    return $"{heapName}.writeI32({pointer}, {value});";
                }
                else
                {
                    return $"{heapName}.writeBlob({pointer}, {value});";
                }
            case BaseTypeRef:
                throw new Exception("Not resolved");
            case BorrowTypeRef:
            case PointerTypeRef:
            case ReferenceTypeRef:
                return $"{heapName}.writeI32({pointer}, {value});";
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    public string GenerateKeyName(FunctionRef key)
    {
        var funcSb = new StringBuilder();
        if (key.TargetType != null)
        {
            funcSb.Append(GenerateName(key.TargetType));
            funcSb.Append("____");
        }

        if (key.TargetImplementation != null)
        {
            funcSb.Append(GenerateName(key.TargetType));
            funcSb.Append("____");
        }

        funcSb.Append(GenerateName(key.TargetMethod));
        return funcSb.ToString();
    }

    private string GenerateName(TypeRef typeRef)
    {
        switch (typeRef)
        {
            case ConcreteTypeRef concreteTypeRef:
            {
                var sb = new StringBuilder();
                sb.Append(concreteTypeRef.Name.ToString().Replace("::", "__"));

                if (concreteTypeRef.GenericParams.Length > 0)
                {
                    sb.Append("_O_");

                    var first = true;
                    foreach (var param in concreteTypeRef.GenericParams)
                    {
                        if (!first)
                        {
                            sb.Append("_A_");
                        }

                        first = false;

                        sb.Append(GenerateName(param));
                    }

                    sb.Append("_C_");
                }

                return sb.ToString();
            }
            case DerivedTypeRef derivedTypeRef:
            case GenericTypeRef genericTypeRef:
            case ThisTypeRef thisTypeRef:
                throw new Exception("Unexpected");
            case BorrowTypeRef borrowTypeRef:
                return (borrowTypeRef.MutableRef ? "BRWMUT_" : "_BOR_") + GenerateName(borrowTypeRef.InnerType);
            case PointerTypeRef pointerTypeRef:
                return (pointerTypeRef.MutableRef ? "_PTRMUT_" : "_PTR_") + GenerateName(pointerTypeRef.InnerType);
            case ReferenceTypeRef referenceTypeRef:
                return (referenceTypeRef.StrongRef ? "_REF_" : "_REFWEAK_") + GenerateName(referenceTypeRef.InnerType);
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }
    }

    private string GeneratePrettyName(TypeRef typeRef)
    {
        switch (typeRef)
        {
            case ConcreteTypeRef concreteTypeRef:
            {
                var sb = new StringBuilder();
                sb.Append(concreteTypeRef.Name);

                if (concreteTypeRef.GenericParams.Length > 0)
                {
                    sb.Append('<');

                    var first = true;
                    foreach (var param in concreteTypeRef.GenericParams)
                    {
                        if (!first)
                        {
                            sb.Append(", ");
                        }

                        first = false;

                        sb.Append(GeneratePrettyName(param));
                    }

                    sb.Append('>');
                }

                return sb.ToString();
            }
            case DerivedTypeRef derivedTypeRef:
            case GenericTypeRef genericTypeRef:
            case ThisTypeRef thisTypeRef:
                throw new Exception("Unexpected");
            case BorrowTypeRef borrowTypeRef:
                return (borrowTypeRef.MutableRef ? "&mut " : "&") + GeneratePrettyName(borrowTypeRef.InnerType);
            case PointerTypeRef pointerTypeRef:
                return (pointerTypeRef.MutableRef ? "*mut " : "*") + GeneratePrettyName(pointerTypeRef.InnerType);
            case ReferenceTypeRef referenceTypeRef:
                return (referenceTypeRef.StrongRef ? "ref " : "weak ") + GeneratePrettyName(referenceTypeRef.InnerType);
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }
    }

    public uint GetSize(TypeRef typeRef)
    {
        if (typeRef == null)
        {
            throw new Exception("Unexpected");
            // return 0;
        }

        switch (typeRef)
        {
            case ConcreteTypeRef concreteTypeRef:
                return GetConcreteTypeSize(concreteTypeRef);
            case BaseTypeRef:
                throw new Exception("Unresolved type");
            case BorrowTypeRef:
            case PointerTypeRef:
            case ReferenceTypeRef:
                return 4;
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }
    }

    private uint GetConcreteTypeSize(ConcreteTypeRef typeRef)
    {
        if (_typeSizes.TryGetValue(typeRef, out var mappedType))
        {
            return mappedType;
        }

        var objectDef = Store.Lookup(typeRef.Name);
        if (objectDef == null)
        {
            throw new Exception($"Unable to resolve {typeRef.Name}");
        }

        if (objectDef.GenericParams.Count != typeRef.GenericParams.Length)
        {
            throw new Exception("Mismatch of generic parameters");
        }

        var structContext = new GenericContext(null, objectDef.GenericParams, typeRef.GenericParams, null);

        switch (objectDef)
        {
            case Struct structDef:
            {
                uint size = 0;

                foreach (var fieldDef in structDef.Fields)
                {
                    size += GetSize(structContext.ResolveRef(fieldDef.Type));
                }

                _typeSizes.Add(typeRef, size);
                return size;
            }
            case Function functionDef:
                throw new Exception("Unexpected function type");
            case Interface interfaceDef:
                throw new NotImplementedException("Interface types not implemented");
            case Variant variantDef:
            {
                var bodySize = GetVariantBodySize(variantDef, typeRef.GenericParams);
                _typeSizes.Add(typeRef, 1 + bodySize);
                return 1 + bodySize;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(objectDef));
        }
    }

    public uint GetVariantBodySize(Variant variant, ImmutableArray<TypeRef> genericParams)
    {
        uint maxSize = 0;

        foreach (var item in variant.Items)
        {
            if (item.Content == null) continue;

            var itemRef = new ConcreteTypeRef(
                new QualifiedName(true, variant.Name.Parts.Add(item.Name)),
                genericParams
            );
            maxSize = Math.Max(maxSize, GetConcreteTypeSize(itemRef));
        }

        return maxSize;
    }

    public bool GetIntrinsic(QualifiedName qn, out IntrinsicMapper mapper)
    {
        return _intrinsics.TryGetValue(qn, out mapper);
    }
}