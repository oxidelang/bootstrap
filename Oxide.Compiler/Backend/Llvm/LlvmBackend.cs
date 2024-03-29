using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Backend.Llvm;

/// <summary>
/// Llvm Backend - Produces LLVM IR from Oxide IR
/// </summary>
public class LlvmBackend
{
    public delegate void IntrinsicMapper(FunctionGenerator generator, StaticCallInst inst, FunctionRef key);

    public IrStore Store { get; }

    public MiddlewareManager Middleware { get; }

    public LLVMModuleRef Module { get; private set; }

    public LLVMContextRef Context { get; private set; }

    private Dictionary<ConcreteTypeRef, LLVMTypeRef> _typeStore;

    private Dictionary<ConcreteTypeRef, uint> _typeSizeStore;

    private Dictionary<FunctionRef, LLVMValueRef> _functionRefs;

    private Dictionary<FunctionRef, LLVMTypeRef> _functionTypes;

    private Dictionary<TypeRef, LLVMValueRef> _dropFuncs;

    private LLVMTargetDataRef DataLayout { get; set; }

    private Dictionary<QualifiedName, IntrinsicMapper> _intrinsics;

    public LLVMTypeRef TypeInfoType { get; private set; }

    public LLVMTypeRef DropPtrFuncType { get; private set; }

    private Dictionary<TypeRef, LLVMValueRef> _typeInfos;

    public LlvmBackend(IrStore store, MiddlewareManager middleware)
    {
        Store = store;
        Middleware = middleware;
        _typeSizeStore = new Dictionary<ConcreteTypeRef, uint>();
        _typeStore = new Dictionary<ConcreteTypeRef, LLVMTypeRef>();
        _functionRefs = new Dictionary<FunctionRef, LLVMValueRef>();
        _functionTypes = new Dictionary<FunctionRef, LLVMTypeRef>();
        _intrinsics = new Dictionary<QualifiedName, IntrinsicMapper>();
        _dropFuncs = new Dictionary<TypeRef, LLVMValueRef>();

        // TODO: Check target size, assume 64bit for now
        _typeStore.Add(PrimitiveKind.USize.GetRef(), LLVMTypeRef.Int64);
        _typeStore.Add(PrimitiveKind.U8.GetRef(), LLVMTypeRef.Int8);
        _typeStore.Add(PrimitiveKind.U16.GetRef(), LLVMTypeRef.Int16);
        _typeStore.Add(PrimitiveKind.U32.GetRef(), LLVMTypeRef.Int32);
        _typeStore.Add(PrimitiveKind.U64.GetRef(), LLVMTypeRef.Int64);
        _typeStore.Add(PrimitiveKind.ISize.GetRef(), LLVMTypeRef.Int64);
        _typeStore.Add(PrimitiveKind.I8.GetRef(), LLVMTypeRef.Int8);
        _typeStore.Add(PrimitiveKind.I16.GetRef(), LLVMTypeRef.Int16);
        _typeStore.Add(PrimitiveKind.I32.GetRef(), LLVMTypeRef.Int32);
        _typeStore.Add(PrimitiveKind.I64.GetRef(), LLVMTypeRef.Int64);
        _typeStore.Add(PrimitiveKind.Bool.GetRef(), LLVMTypeRef.Int1);

        _intrinsics.Add(QualifiedName.From("std", "size_of"), LlvmIntrinsics.SizeOf);
        _intrinsics.Add(QualifiedName.From("std", "bitcopy"), LlvmIntrinsics.Bitcopy);
        _intrinsics.Add(QualifiedName.From("std", "type_id"), LlvmIntrinsics.TypeId);
        _intrinsics.Add(QualifiedName.From("std", "type_call_drop"), LlvmIntrinsics.TypeDrop);
        _intrinsics.Add(QualifiedName.From("std", "atomic_swap"), LlvmIntrinsics.AtomicSwap);
        _intrinsics.Add(QualifiedName.From("std", "atomic_add"), LlvmIntrinsics.AtomicAdd);
        _intrinsics.Add(QualifiedName.From("std", "atomic_sub"), LlvmIntrinsics.AtomicSub);
    }

    public void Begin()
    {
        Module = LLVMModuleRef.CreateWithName("OxideModule");
        Context = Module.Context;
        DataLayout = LLVMTargetDataRef.FromStringRepresentation(Module.DataLayout);
    }

    public void Compile()
    {
        // Create types & functions
        {
            _typeInfos = new Dictionary<TypeRef, LLVMValueRef>();
            DropPtrFuncType = LLVMTypeRef.CreateFunction(
                LLVMTypeRef.Void,
                new[] {LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)}
            );

            TypeInfoType = Context.CreateNamedStruct("TypeInfo");
            TypeInfoType.StructSetBody(new[]
            {
                LLVMTypeRef.CreatePointer(DropPtrFuncType, 0)
            }, false);
        }

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

        // Compile functions
        foreach (var usedType in Middleware.Usage.UsedTypes.Values)
        {
            CompileType(usedType);
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

                var funcContext = new GenericContext(null, func.GenericParams, version, null);

                CompileFunction(key, func, funcContext);
            }
        }
    }

    private void CreateFunction(FunctionRef key, Function func, GenericContext context)
    {
        // Generate unique name
        var funcSb = new StringBuilder();
        if (key.TargetType != null)
        {
            funcSb.Append(GenerateName(key.TargetType));
            funcSb.Append("__INNER__");
        }

        if (key.TargetImplementation != null)
        {
            funcSb.Append(GenerateName(key.TargetType));
            funcSb.Append("__INNER__");
        }

        funcSb.Append(GenerateName(key.TargetMethod));

        var funcName = funcSb.ToString();
        var paramTypes = new List<LLVMTypeRef>();
        foreach (var paramDef in func.Parameters)
        {
            var paramType = context != null ? context.ResolveRef(paramDef.Type) : paramDef.Type;
            paramTypes.Add(ConvertType(paramType));
        }

        var returnTypeRef = context != null ? context.ResolveRef(func.ReturnType) : func.ReturnType;
        var returnType = ConvertType(returnTypeRef);
        var funcType = LLVMTypeRef.CreateFunction(returnType, paramTypes.ToArray());
        var funcRef = Module.AddFunction(funcName, funcType);

        if (func.AlwaysInline)
        {
            funcRef.AddAttribute(
                LlvmAttributes.Target.Function,
                Context.CreateEnumAttribute(LlvmAttributes.AttrKind.AlwaysInline)
            );
        }

        if (func.IsExported)
        {
        }
        else if (!func.IsExtern)
        {
            // funcRef.FunctionCallConv = FunctionCall
            funcRef.Linkage = LLVMLinkage.LLVMPrivateLinkage;
        }

        _functionRefs.Add(key, funcRef);
        _functionTypes.Add(key, funcType);
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
            Console.WriteLine($" - Generating {GenerateName(concreteType)}");
            ResolveConcreteType(concreteType);

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

    private void CompileType(UsedType usedType)
    {
        var type = Store.Lookup<OxType>(usedType.Name);
        if (usedType.Versions.Count == 0)
        {
            throw new Exception("Type has no used versions");
        }

        foreach (var version in usedType.Versions.Values)
        {
            var concreteType = new ConcreteTypeRef(usedType.Name, version.Generics);
            Console.WriteLine($" - Compiling {GenerateName(concreteType)}");
            ResolveConcreteType(concreteType);

            var context = new GenericContext(null, type.GenericParams, version.Generics, concreteType);

            if (version.DefaultImplementation != null)
            {
                CompileImplementation(context, version.DefaultImplementation);
            }

            foreach (var imp in version.Implementations.Values)
            {
                CompileImplementation(context, imp);
            }
        }
    }

    private void CompileImplementation(GenericContext context, UsedImplementation usedImp)
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
                    TargetMethod = new ConcreteTypeRef(usedFunc.Name, version)
                };

                var funcContext = new GenericContext(
                    impContext,
                    resolved.Function.GenericParams,
                    version,
                    impContext.ThisRef
                );

                CompileFunction(key, resolved.Function, funcContext);
            }
        }
    }

    private void CompileFunction(FunctionRef key, Function func, GenericContext context)
    {
        Console.WriteLine($"   - Compiling func {key}");
        var funcGen = new FunctionGenerator(this);
        if (func.IsExtern)
        {
            return;
        }

        var lifetime = Middleware.Lifetime.FunctionLifetimes[func];
        funcGen.Compile(key, func, context, lifetime);
    }

    public LLVMValueRef GetFunctionRef(FunctionRef key, bool throwIfMissing = true)
    {
        if (_functionRefs.TryGetValue(key, out var funcRef))
        {
            return funcRef;
        }

        if (throwIfMissing)
        {
            throw new Exception("Failed to find function");
        }

        return null;
    }

    public LLVMTypeRef ConvertType(TypeRef typeRef)
    {
        if (typeRef == null)
        {
            return LLVMTypeRef.Void;
        }

        switch (typeRef)
        {
            case ConcreteTypeRef concreteTypeRef:
                return ResolveConcreteType(concreteTypeRef);
            case BaseTypeRef:
                throw new Exception("Unresolved type");
            case BorrowTypeRef borrowTypeRef:
                return LLVMTypeRef.CreatePointer(ConvertType(borrowTypeRef.InnerType), 0);
            case PointerTypeRef pointerTypeRef:
                return LLVMTypeRef.CreatePointer(ConvertType(pointerTypeRef.InnerType), 0);
            case ReferenceTypeRef referenceTypeRef:
                return LLVMTypeRef.CreatePointer(GetBoxType(referenceTypeRef.InnerType), 0);
            case DerivedRefTypeRef derivedRefTypeRef:
                return GetDerivedBoxType(derivedRefTypeRef.InnerType);
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }
    }

    public LLVMTypeRef GetBoxType(TypeRef typeRef)
    {
        return ResolveConcreteType(ConcreteTypeRef.From(QualifiedName.From("std", "Box"), typeRef));
    }

    public LLVMTypeRef GetDerivedBoxType(TypeRef typeRef)
    {
        return ResolveConcreteType(ConcreteTypeRef.From(QualifiedName.From("std", "DerivedBox"), typeRef));
    }

    private LLVMTypeRef ResolveConcreteType(ConcreteTypeRef typeRef)
    {
        if (_typeStore.TryGetValue(typeRef, out var mappedType))
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
            case OxEnum oxEnum:
            {
                // Map enum to primitive type
                var underlyingType = PrimitiveType.GetRef(oxEnum.UnderlyingType);
                var underlyingTypeRef = ResolveConcreteType(underlyingType);
                _typeStore.Add(typeRef, underlyingTypeRef);

                return underlyingTypeRef;
            }
            case Struct structDef:
            {
                var structType = Context.CreateNamedStruct(GenerateName(typeRef));
                _typeStore.Add(typeRef, structType);

                var bodyTypes = new List<LLVMTypeRef>();
                foreach (var fieldDef in structDef.Fields)
                {
                    bodyTypes.Add(ConvertType(structContext.ResolveRef(fieldDef.Type)));
                }

                structType.StructSetBody(bodyTypes.ToArray(), false);

                return structType;
            }
            case Function functionDef:
                throw new Exception("Unexpected function type");
            case Interface interfaceDef:
                throw new NotImplementedException("Interface types not implemented");
            case Variant variantDef:
            {
                var structType = Context.CreateNamedStruct(GenerateName(typeRef));
                _typeStore.Add(typeRef, structType);

                var bodySize = GetVariantBodySize(variantDef, typeRef.GenericParams);

                structType.StructSetBody(new[]
                {
                    LLVMTypeRef.Int8,
                    LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, bodySize)
                }, false);

                return structType;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(objectDef));
        }
    }

    public uint GetVariantBodySize(Variant variant, ImmutableArray<TypeRef> genericParams)
    {
        ulong maxSize = 0;

        foreach (var item in variant.Items)
        {
            if (item.Content == null) continue;

            var itemRef = new ConcreteTypeRef(
                new QualifiedName(true, variant.Name.Parts.Add(item.Name)),
                genericParams
            );
            maxSize = Math.Max(maxSize, GetConcreteTypeSize(itemRef));
        }

        return (uint) maxSize;
    }

    public uint GetConcreteTypeSize(ConcreteTypeRef typeRef)
    {
        if (_typeSizeStore.TryGetValue(typeRef, out var size))
        {
            return size;
        }

        var resolvedType = ResolveConcreteType(typeRef);
        size = (uint) DataLayout.ABISizeOfType(resolvedType);
        _typeSizeStore.Add(typeRef, size);

        return size;
    }


    private string GenerateName(TypeRef typeRef)
    {
        return GenerateNameInner(typeRef).Replace(":", "__");
    }

    private string GenerateNameInner(TypeRef typeRef)
    {
        switch (typeRef)
        {
            case ConcreteTypeRef concreteTypeRef:
            {
                var sb = new StringBuilder();
                sb.Append(concreteTypeRef.Name);

                if (concreteTypeRef.GenericParams.Length > 0)
                {
                    sb.Append("__GSTART__");

                    var first = true;
                    foreach (var param in concreteTypeRef.GenericParams)
                    {
                        if (!first)
                        {
                            sb.Append("__GNEXT__");
                        }

                        first = false;

                        sb.Append(GenerateName(param));
                    }

                    sb.Append("__GEND__");
                }

                return sb.ToString();
            }
            case DerivedTypeRef derivedTypeRef:
            case GenericTypeRef genericTypeRef:
            case ThisTypeRef thisTypeRef:
                throw new Exception("Unexpected");
            case BorrowTypeRef borrowTypeRef:
                return (borrowTypeRef.MutableRef ? "__BORROW_MUT__" : "__BORROW__") +
                       GenerateName(borrowTypeRef.InnerType);
            case PointerTypeRef pointerTypeRef:
                return (pointerTypeRef.MutableRef ? "__POINTER_MUT__" : "__POINTER__") +
                       GenerateName(pointerTypeRef.InnerType);
            case ReferenceTypeRef referenceTypeRef:
                return (referenceTypeRef.StrongRef ? "__REF__" : "__WEAK__")
                       + GenerateName(referenceTypeRef.InnerType);
            case DerivedRefTypeRef derivedRefTypeRef:
                return (derivedRefTypeRef.StrongRef ? "__DER_REF__ " : "__DER_WEAK__") +
                       GenerateName(derivedRefTypeRef.InnerType);
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }
    }

    /// <summary>
    /// Returns the drop implementation for a given type. Returns null if there is no drop logic.
    /// </summary>
    public LLVMValueRef GetDropFunctionRef(TypeRef typeRef)
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
            // Drop function provided by std library
            case ReferenceTypeRef referenceTypeRef:
                return GetFunctionRef(
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
            case DerivedRefTypeRef derivedRefTypeRef:
                return GetFunctionRef(
                    new FunctionRef
                    {
                        TargetMethod = ConcreteTypeRef.From(
                            QualifiedName.From(
                                "std",
                                derivedRefTypeRef.StrongRef ? "box_drop_strong_derived" : "box_drop_weak_derived"
                            ),
                            derivedRefTypeRef.InnerType
                        )
                    }
                );
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }
    }

    private LLVMValueRef GetDropFunctionForBase(ConcreteTypeRef typeRef)
    {
        // Cache function
        if (_dropFuncs.TryGetValue(typeRef, out var resolved))
        {
            return resolved;
        }

        var baseType = Store.Lookup(typeRef.Name);
        var dropFunc = Store.GetDropFunction(typeRef);

        switch (baseType)
        {
            case OxEnum:
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

        var funcName = $"__INBUILT_DROP__{GenerateName(typeRef)}";

        // Define function signature
        var varType = ConvertType(new PointerTypeRef(typeRef, true));
        var paramTypes = new[]
        {
            varType
        };

        var funcType = LLVMTypeRef.CreateFunction(ConvertType(null), paramTypes);
        var funcRef = Module.AddFunction(funcName, funcType);

        // Always inline drop functions for better performance
        funcRef.AddAttribute(
            LlvmAttributes.Target.Function,
            Context.CreateEnumAttribute(LlvmAttributes.AttrKind.AlwaysInline)
        );

        _dropFuncs.Add(typeRef, funcRef);

        using var builder = Context.CreateBuilder();

        var entryBlock = funcRef.AppendBasicBlock("entry");
        builder.PositionAtEnd(entryBlock);

        var valuePtr = funcRef.Params[0];
        // var valuePtr = builder.BuildAlloca(varType, "value");
        // builder.BuildStore(funcRef.Params[0], valuePtr);

        // Call "Drop" implementation if exists
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

            var dropFuncRef = GetFunctionRef(dropFunc);

            if (funcDef.Parameters.Count != 1)
            {
                throw new Exception("Invalid number of arguments");
            }

            var param = funcDef.Parameters[0];
            var paramType = funcContext.ResolveRef(param.Type);

            var matches = paramType switch
            {
                BorrowTypeRef borrowTypeRef => Equals(borrowTypeRef.InnerType, typeRef),
                PointerTypeRef pointerTypeRef => Equals(pointerTypeRef.InnerType, typeRef),
                _ => throw new ArgumentOutOfRangeException(nameof(paramType))
            };

            if (!matches)
            {
                throw new Exception($"Argument does not match parameter type for {param.Name}");
            }

            builder.BuildCall(dropFuncRef, new[] {valuePtr});
        }


        var structContext = new GenericContext(null, baseType.GenericParams, typeRef.GenericParams, null);

        switch (baseType)
        {
            case Variant variant:
            {
                // Drop variants by checking type, and then calling inner type drop function
                var noActionBlock = funcRef.AppendBasicBlock($"item_none");
                builder.PositionAtEnd(noActionBlock);
                builder.BuildRetVoid();

                var itemBlocks = new LLVMBasicBlockRef[variant.Items.Count];
                for (var i = 0; i < variant.Items.Count; i++)
                {
                    var item = variant.Items[i];
                    if (item.Content == null)
                    {
                        itemBlocks[i] = noActionBlock;
                        continue;
                    }

                    var itemRef = new ConcreteTypeRef(
                        new QualifiedName(true, variant.Name.Parts.Add(item.Name)),
                        typeRef.GenericParams
                    );

                    var itemDropFunc = GetDropFunctionRef(itemRef);
                    if (itemDropFunc == null)
                    {
                        itemBlocks[i] = noActionBlock;
                        continue;
                    }

                    itemBlocks[i] = funcRef.AppendBasicBlock($"item_{i}");
                    builder.PositionAtEnd(itemBlocks[i]);

                    var itemAddr = builder.BuildInBoundsGEP(
                        valuePtr,
                        new[]
                        {
                            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
                        },
                        $"item_{i}_addr"
                    );

                    var itemResolvedType = ConvertType(itemRef);
                    var itemAddrCasted = builder.BuildBitCast(
                        itemAddr,
                        LLVMTypeRef.CreatePointer(itemResolvedType, 0),
                        $"item_{i}_addr_cast"
                    );

                    // var itemValue = builder.BuildLoad(itemAddrCasted, $"item_{i}_value");

                    builder.BuildCall(itemDropFunc, new[] {itemAddrCasted});
                    builder.BuildRetVoid();
                }

                builder.PositionAtEnd(entryBlock);

                var idAddr = builder.BuildInBoundsGEP(
                    valuePtr,
                    new[]
                    {
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
                    },
                    "id_addr"
                );
                var idValue = builder.BuildLoad(idAddr, "id_value");

                var idSwitch = builder.BuildSwitch(idValue, noActionBlock, (uint) itemBlocks.Length);
                for (var i = 0; i < itemBlocks.Length; i++)
                {
                    idSwitch.AddCase(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong) i), itemBlocks[i]);
                }

                break;
            }
            case Struct structDef:
            {
                // Drop structs by dropping each field in turn
                for (var i = 0; i < structDef.Fields.Count; i++)
                {
                    var fieldDef = structDef.Fields[i];
                    var fieldType = structContext.ResolveRef(fieldDef.Type);
                    var fieldDropFunc = GetDropFunctionRef(fieldType);
                    if (fieldDropFunc == null)
                    {
                        continue;
                    }

                    var fieldAddr = builder.BuildInBoundsGEP(
                        valuePtr,
                        new[]
                        {
                            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong) i)
                        },
                        $"field_{i}_addr"
                    );
                    // var fieldValue = builder.BuildLoad(fieldAddr, $"field_{i}_value");

                    builder.BuildCall(fieldDropFunc, new[] {fieldAddr});
                }

                builder.BuildRetVoid();
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(baseType));
        }

        return funcRef;
    }

    /// <summary>
    /// Return a pointer to a types runtime information
    /// </summary>
    public LLVMValueRef GetTypeInfo(TypeRef typeRef)
    {
        if (_typeInfos.TryGetValue(typeRef, out var constant))
        {
            return constant;
        }

        // Define constant
        constant = Module.AddGlobalInAddressSpace(TypeInfoType, $"INBUILT__TypeInfo__{GenerateName(typeRef)}", 0);
        constant.IsGlobalConstant = true;

        var dropFuncRef = GetDropFunctionRef(typeRef);

        // Add pointer to drop function
        var dropPtrFunc = Module.AddFunction($"INBUILT___DropPtr___{GenerateName(typeRef)}", DropPtrFuncType);
        {
            using var builder = Context.CreateBuilder();

            var entryBlock = dropPtrFunc.AppendBasicBlock("entry");
            builder.PositionAtEnd(entryBlock);

            var resolvedType = ConvertType(new PointerTypeRef(typeRef, false));
            var casted = builder.BuildBitCast(dropPtrFunc.Params[0], resolvedType, "casted");
            // var loaded = builder.BuildLoad(casted, "load");

            if (dropFuncRef != null)
            {
                builder.BuildCall(dropFuncRef, new[] {casted});
            }

            builder.BuildRetVoid();
        }

        constant.Initializer = LLVMValueRef.CreateConstNamedStruct(TypeInfoType, new[]
        {
            dropPtrFunc
        });

        _typeInfos.Add(typeRef, constant);
        return constant;
    }

    public bool GetIntrinsic(QualifiedName qn, out IntrinsicMapper mapper)
    {
        return _intrinsics.TryGetValue(qn, out mapper);
    }

    public void Complete(string path)
    {
        Console.WriteLine(" - Dumping reopt");
        var llvmIr = Module.PrintToString();
        File.WriteAllText($"{path}/compiled.preopt.llvm", llvmIr);

        if (!Module.TryVerify(LLVMVerifierFailureAction.LLVMPrintMessageAction, out var error))
        {
            Console.WriteLine($"Error: {error}");
        }


        unsafe
        {
            LLVMPassManagerBuilderRef passManagerBuilder = LLVM.PassManagerBuilderCreate();
            passManagerBuilder.SetOptLevel(3);
            var passManager = LLVMPassManagerRef.Create();
            passManager.AddAlwaysInlinerPass();
            passManagerBuilder.PopulateModulePassManager(passManager);
            passManager.Run(Module);
        }

        llvmIr = Module.PrintToString();
        File.WriteAllText($"{path}/compiled.opt.llvm", llvmIr);

        if (Module.WriteBitcodeToFile($"{path}/compiled.opt.bc") != 0)
        {
            Console.WriteLine("error writing bitcode to file");
        }

        // Emit object file
        var target = LLVMTargetRef.GetTargetFromTriple(LLVMTargetRef.DefaultTriple);
        var machine = target.CreateTargetMachine(
            LLVMTargetRef.DefaultTriple,
            "generic",
            "",
            LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault,
            LLVMRelocMode.LLVMRelocPIC,
            LLVMCodeModel.LLVMCodeModelDefault
        );
        machine.EmitToFile(Module, $"{path}/compiled.o", LLVMCodeGenFileType.LLVMObjectFile);
    }

    static LlvmBackend()
    {
        LLVM.LinkInMCJIT();

        LLVM.InitializeX86TargetMC();
        LLVM.InitializeX86Target();
        LLVM.InitializeX86TargetInfo();
        LLVM.InitializeX86AsmParser();
        LLVM.InitializeX86AsmPrinter();
    }
}