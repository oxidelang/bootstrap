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

    private LLVMTargetDataRef DataLayout { get; set; }

    private Dictionary<QualifiedName, IntrinsicMapper> _intrinsics;

    public LlvmBackend(IrStore store, MiddlewareManager middleware)
    {
        Store = store;
        Middleware = middleware;
        _typeSizeStore = new Dictionary<ConcreteTypeRef, uint>();
        _typeStore = new Dictionary<ConcreteTypeRef, LLVMTypeRef>();
        _functionRefs = new Dictionary<FunctionRef, LLVMValueRef>();
        _intrinsics = new Dictionary<QualifiedName, IntrinsicMapper>();

        // TODO: Check target size
        _typeStore.Add(PrimitiveKind.USize.GetRef(), LLVMTypeRef.Int64);
        _typeStore.Add(PrimitiveKind.U8.GetRef(), LLVMTypeRef.Int8);
        _typeStore.Add(PrimitiveKind.I32.GetRef(), LLVMTypeRef.Int32);
        _typeStore.Add(PrimitiveKind.Bool.GetRef(), LLVMTypeRef.Int1);

        _intrinsics.Add(QualifiedName.From("std", "size_of"), LlvmIntrinsics.SizeOf);
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
        var funcSb = new StringBuilder();
        if (key.TargetType != null)
        {
            funcSb.Append(GenerateName(key.TargetType));
            funcSb.Append('#');
        }

        if (key.TargetImplementation != null)
        {
            funcSb.Append(GenerateName(key.TargetType));
            funcSb.Append('#');
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

        _functionRefs.Add(key, funcRef);
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
        var funcGen = new FunctionGenerator(this);
        funcGen.Compile(key, func, context);
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
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }
    }

    public LLVMTypeRef GetBoxType(TypeRef typeRef)
    {
        return ResolveConcreteType(ConcreteTypeRef.From(QualifiedName.From("std", "Box"), typeRef));
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

        return (uint)maxSize;
    }

    public uint GetConcreteTypeSize(ConcreteTypeRef typeRef)
    {
        if (_typeSizeStore.TryGetValue(typeRef, out var size))
        {
            return size;
        }

        var resolvedType = ResolveConcreteType(typeRef);
        size = (uint)DataLayout.ABISizeOfType(resolvedType);
        _typeSizeStore.Add(typeRef, size);

        return size;
    }

    private string GenerateName(TypeRef typeRef)
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

                        sb.Append(GenerateName(param));
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
                return (borrowTypeRef.MutableRef ? "&mut " : "&") + GenerateName(borrowTypeRef.InnerType);
            case PointerTypeRef pointerTypeRef:
                return (pointerTypeRef.MutableRef ? "*mut " : "*") + GenerateName(pointerTypeRef.InnerType);
            case ReferenceTypeRef referenceTypeRef:
                return (referenceTypeRef.StrongRef ? "ref " : "weak ") + GenerateName(referenceTypeRef.InnerType);
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }
    }

    public bool GetIntrinsic(QualifiedName qn, out IntrinsicMapper mapper)
    {
        return _intrinsics.TryGetValue(qn, out mapper);
    }

    public void Complete(string path)
    {
        if (!Module.TryVerify(LLVMVerifierFailureAction.LLVMPrintMessageAction, out var error))
        {
            Console.WriteLine($"Error: {error}");
        }

        var llvmIr = Module.PrintToString();
        File.WriteAllText($"{path}/compiled.preopt.llvm", llvmIr);

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