using System;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;

namespace Oxide.Compiler.Backend.Llvm
{
    public class LlvmBackend
    {
        public IrStore Store { get; }

        public LLVMModuleRef Module { get; private set; }

        public LlvmBackend(IrStore store)
        {
            Store = store;
        }

        public void Begin()
        {
            Module = LLVMModuleRef.CreateWithName("OxideModule");
        }

        public void CompileUnit(IrUnit unit)
        {
            foreach (var funcDef in unit.Functions.Values)
            {
                CompileFunction(funcDef);
            }
        }

        public void CompileFunction(FunctionDef funcDef)
        {
            var funcGen = new FunctionGenerator(this);
            funcGen.Compile(funcDef);
        }

        public void Complete()
        {
            if (!Module.TryVerify(LLVMVerifierFailureAction.LLVMPrintMessageAction, out var error))
            {
                Console.WriteLine($"Error: {error}");
            }

            Module.Dump();

            // unsafe
            // {
            //     LLVMPassManagerBuilderRef passManagerBuilder = LLVM.PassManagerBuilderCreate();
            //     passManagerBuilder.SetOptLevel(1);
            //     var passManager = LLVMPassManagerRef.Create();
            //     passManagerBuilder.PopulateModulePassManager(passManager);
            //     passManager.Run(Module);
            //     Module.Dump();
            // }

            // if (moduleRef.WriteBitcodeToFile("sum.bc") != 0)
            // {
            //     Console.WriteLine("error writing bitcode to file, skipping");
            // }
            // Console.WriteLine(moduleRef.PrintToString());
        }

        public void Run()
        {
            var options = new LLVMMCJITCompilerOptions { NoFramePointerElim = 1 };
            if (!Module.TryCreateMCJITCompiler(out var engine, ref options, out var error))
            {
                throw new Exception($"Error: {error}");
            }

            engine.AddGlobalMapping(
                Module.GetNamedFunction("::std::debug_int"),
                Marshal.GetFunctionPointerForDelegate<DebugInt>(DebugIntImp)
            );
            engine.AddGlobalMapping(
                Module.GetNamedFunction("::std::debug_bool"),
                Marshal.GetFunctionPointerForDelegate<DebugBool>(DebugBoolImp)
            );

            var funcRef = Module.GetNamedFunction("::examples::main");
            var mainMethod =
                (MainMethod)Marshal.GetDelegateForFunctionPointer(engine.GetPointerToGlobal(funcRef),
                    typeof(MainMethod));
            mainMethod();

            engine.Dispose();
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