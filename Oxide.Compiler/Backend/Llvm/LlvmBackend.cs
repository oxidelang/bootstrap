using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Backend.Llvm
{
    public class LlvmBackend
    {
        public IrStore Store { get; }

        public LLVMModuleRef Module { get; private set; }

        public LLVMContextRef Context { get; private set; }

        private Dictionary<QualifiedName, LLVMTypeRef> _typeStore;

        public LlvmBackend(IrStore store)
        {
            Store = store;
            _typeStore = new Dictionary<QualifiedName, LLVMTypeRef>();
            _typeStore.Add(PrimitiveType.I32.Name, LLVMTypeRef.Int32);
            _typeStore.Add(PrimitiveType.Bool.Name, LLVMTypeRef.Int1);
        }

        public void Begin()
        {
            Module = LLVMModuleRef.CreateWithName("OxideModule");
            Context = Module.Context;
        }

        public void CompileUnit(IrUnit unit)
        {
            foreach (var funcDef in unit.Objects.Values.Where(x => x is Function).Cast<Function>())
            {
                CompileFunction(funcDef);
            }
        }

        public void CompileFunction(Function func)
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
                {
                    if (concreteTypeRef.GenericParams != null && !concreteTypeRef.GenericParams.IsEmpty)
                    {
                        throw new NotImplementedException("Generic type support is not implemented");
                    }

                    return ResolveBaseType(concreteTypeRef.Name);
                }
                case BaseTypeRef baseTypeRef:
                    throw new NotImplementedException();
                case BorrowTypeRef borrowTypeRef:
                    return LLVMTypeRef.CreatePointer(ConvertType(borrowTypeRef.InnerType), 0);
                case PointerTypeRef pointerTypeRef:
                    throw new NotImplementedException("Pointer types not implemented");
                    break;
                case ReferenceTypeRef referenceTypeRef:
                    throw new NotImplementedException("Reference types not implemented");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(typeRef));
            }
        }

        public LLVMTypeRef ResolveBaseType(QualifiedName qn)
        {
            if (_typeStore.ContainsKey(qn))
            {
                return _typeStore[qn];
            }

            return ResolveMissingType(qn);
        }

        private LLVMTypeRef ResolveMissingType(QualifiedName qn)
        {
            var objectDef = Store.Lookup(qn);
            if (objectDef == null)
            {
                throw new Exception($"Unable to resolve {qn}");
            }

            if (objectDef.GenericParams != null && objectDef.GenericParams.Count > 0)
            {
                throw new NotImplementedException("Generic types");
            }

            switch (objectDef)
            {
                case Struct structDef:
                {
                    var structType = Context.CreateNamedStruct(structDef.Name.ToString());
                    _typeStore.Add(qn, structType);

                    var bodyTypes = new List<LLVMTypeRef>();
                    foreach (var fieldDef in structDef.Fields)
                    {
                        bodyTypes.Add(ConvertType(fieldDef.Type));
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

        public void Complete(string path)
        {
            if (!Module.TryVerify(LLVMVerifierFailureAction.LLVMPrintMessageAction, out var error))
            {
                Console.WriteLine($"Error: {error}");
            }

            var llvmIr = Module.PrintToString();
            File.WriteAllText($"{path}/compiled.llvm", llvmIr);

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