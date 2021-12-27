using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Backend.Llvm
{
    public class LlvmBackend
    {
        public IrStore Store { get; }

        public MiddlewareManager Middleware { get; }

        public LLVMModuleRef Module { get; private set; }

        public LLVMContextRef Context { get; private set; }

        private Dictionary<ConcreteTypeRef, LLVMTypeRef> _typeStore;

        private Dictionary<ConcreteTypeRef, uint> _typeSizeStore;

        private Dictionary<Function, LLVMValueRef> _functionRefs;

        private LLVMTargetDataRef DataLayout { get; set; }

        public LlvmBackend(IrStore store, MiddlewareManager middleware)
        {
            Store = store;
            Middleware = middleware;
            _typeSizeStore = new Dictionary<ConcreteTypeRef, uint>();
            _typeStore = new Dictionary<ConcreteTypeRef, LLVMTypeRef>();
            _typeStore.Add(PrimitiveType.I32Ref, LLVMTypeRef.Int32);
            _typeStore.Add(PrimitiveType.BoolRef, LLVMTypeRef.Int1);
            _functionRefs = new Dictionary<Function, LLVMValueRef>();
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

            foreach (var funcName in Middleware.Usage.UsedFunctions)
            {
                var func = Store.Lookup<Function>(funcName);
                CreateFunction(func, null, null);
            }

            // Compile functions
            foreach (var usedType in Middleware.Usage.UsedTypes.Values)
            {
                CompileType(usedType);
            }

            foreach (var funcName in Middleware.Usage.UsedFunctions)
            {
                var func = Store.Lookup<Function>(funcName);
                CompileFunction(func, GenericContext.Default);
            }
        }

        private void CreateFunction(Function func, string namePrefix, GenericContext context)
        {
            if (!func.GenericParams.IsEmpty)
            {
                throw new NotImplementedException("Generic function support is not implemented");
            }

            var funcName = $"{namePrefix}{(namePrefix != null ? "#" : "")}{func.Name}";
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
            _functionRefs.Add(func, funcRef);
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
                var (imp, func) = Store.LookupImplementation(context.ThisRef, usedImp.Interface,
                    usedFunc.Name.Parts.Single());
                var baseName = $"{imp.Target}#{(imp.Interface != null ? imp.Interface.ToString() : "direct")}";
                CreateFunction(func, baseName, context);
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
                var (imp, func) = Store.LookupImplementation(context.ThisRef, usedImp.Interface,
                    usedFunc.Name.Parts.Single());
                CompileFunction(func, context);
            }
        }

        private void CompileFunction(Function func, GenericContext context)
        {
            var funcGen = new FunctionGenerator(this);
            funcGen.Compile(func, context);
        }

        public LLVMValueRef GetFunctionRef(Function func)
        {
            if (_functionRefs.TryGetValue(func, out var funcRef))
            {
                return funcRef;
            }

            throw new Exception("Failed to find function");
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
                    throw new NotImplementedException("Reference types not implemented");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(typeRef));
            }
        }

        private LLVMTypeRef ResolveConcreteType(ConcreteTypeRef typeRef)
        {
            if (_typeStore.ContainsKey(typeRef))
            {
                return _typeStore[typeRef];
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

        public void Run()
        {
            var options = new LLVMMCJITCompilerOptions { NoFramePointerElim = 1 };
            if (!Module.TryCreateMCJITCompiler(out var engine, ref options, out var error))
            {
                throw new Exception($"Error: {error}");
            }

            MapFunction<DebugInt>(engine, "::std::debug_int", DebugIntImp);
            MapFunction<DebugBool>(engine, "::std::debug_bool", DebugBoolImp);

            var mainMethod = GetFunction<MainMethod>(engine, "::examples::main");

            Console.WriteLine("Running...");
            Console.WriteLine("------------");
            Console.WriteLine();
            mainMethod();

            engine.Dispose();
        }

        public void MapFunction<TDelegate>(LLVMExecutionEngineRef engine, string target, TDelegate d)
            where TDelegate : notnull
        {
            var funcRef = Module.GetNamedFunction(target);
            if (funcRef.IsNull || funcRef.IsUndef || funcRef.Handle == IntPtr.Zero)
            {
                return;
            }

            engine.AddGlobalMapping(
                funcRef,
                Marshal.GetFunctionPointerForDelegate(d)
            );
        }

        public TDelegate GetFunction<TDelegate>(LLVMExecutionEngineRef engine, string target)
        {
            var funcRef = Module.GetNamedFunction(target);
            if (funcRef.IsNull || funcRef.IsUndef || funcRef.Handle == IntPtr.Zero)
            {
                throw new Exception($"Could not find {target}");
            }

            var globalRef = engine.GetPointerToGlobal(funcRef);
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(globalRef);
        }


        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void MainMethod();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DebugInt(int a);

        public static void DebugIntImp(int val)
        {
            Console.WriteLine($"DebugInt: {val}");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DebugBool(byte a);

        public static void DebugBoolImp(byte val)
        {
            Console.WriteLine($"DebugBool: {val}");
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
}