using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;

namespace Oxide.Compiler.Backend.Llvm
{
    public class LlvmBackend
    {
        public IrStore Store { get; }

        public MiddlewareManager Middleware { get; }

        public LLVMModuleRef Module { get; private set; }

        public LLVMContextRef Context { get; private set; }

        private Dictionary<ConcreteTypeRef, LLVMTypeRef> _typeStore;

        public LlvmBackend(IrStore store, MiddlewareManager middleware)
        {
            Store = store;
            Middleware = middleware;
            _typeStore = new Dictionary<ConcreteTypeRef, LLVMTypeRef>();
            _typeStore.Add(PrimitiveType.I32Ref, LLVMTypeRef.Int32);
            _typeStore.Add(PrimitiveType.BoolRef, LLVMTypeRef.Int1);
        }

        public void Begin()
        {
            Module = LLVMModuleRef.CreateWithName("OxideModule");
            Context = Module.Context;
        }

        public void Compile()
        {
            foreach (var usedType in Middleware.Usage.UsedTypes.Values)
            {
                CreateType(usedType);
            }

            foreach (var funcName in Middleware.Usage.UsedFunctions)
            {
                var func = Store.Lookup<Function>(funcName);
                CompileFunction(func);
            }
        }

        private void CreateType(UsedType usedType)
        {
            var type = Store.Lookup<OxType>(usedType.Name);

            var concreteTypes = new List<ConcreteTypeRef>();
            foreach (var generics in usedType.GenericVersions)
            {
                concreteTypes.Add(new ConcreteTypeRef(usedType.Name, generics));
            }

            if (concreteTypes.Count == 0)
            {
                concreteTypes.Add(new ConcreteTypeRef(usedType.Name, ImmutableArray<TypeRef>.Empty));
            }

            foreach (var concreteType in concreteTypes)
            {
                Console.WriteLine($" - Generating {GenerateName(concreteType)}");
                ResolveConcreteType(concreteType);
            }
        }

        private void CompileFunction(Function func)
        {
            var funcGen = new FunctionGenerator(this);
            funcGen.Compile(func);
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

            var structContext = new GenericContext(null, objectDef.GenericParams, typeRef.GenericParams);

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
                    throw new NotImplementedException("Variant types not implemented");
                default:
                    throw new ArgumentOutOfRangeException(nameof(objectDef));
            }
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