using System;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Backend.Llvm
{
    public class LlvmRunner
    {
        public LlvmBackend Backend { get; }

        public LlvmRunner(LlvmBackend backend)
        {
            Backend = backend;
        }

        public void Run()
        {
            unsafe
            {
                var options = new LLVMMCJITCompilerOptions { NoFramePointerElim = 1 };
                if (!Backend.Module.TryCreateMCJITCompiler(out var engine, ref options, out var error))
                {
                    throw new Exception($"Error: {error}");
                }

                MapFunction<DebugInt>(
                    engine,
                    new FunctionRef
                    {
                        TargetMethod = ConcreteTypeRef.From(QualifiedName.From("std", "debug_int"))
                    },
                    DebugIntImp
                );

                MapFunction<DebugBool>(
                    engine,
                    new FunctionRef
                    {
                        TargetMethod = ConcreteTypeRef.From(QualifiedName.From("std", "debug_bool"))
                    },
                    DebugBoolImp
                );

                MapFunction<Alloc>(
                    engine,
                    new FunctionRef
                    {
                        TargetMethod = ConcreteTypeRef.From(QualifiedName.From("std", "alloc"))
                    },
                    AllocImp
                );

                var mainMethod = GetFunction<MainMethod>(
                    engine,
                    new FunctionRef
                    {
                        TargetMethod = ConcreteTypeRef.From(QualifiedName.From("examples", "main"))
                    }
                );

                Console.WriteLine("Running...");
                Console.WriteLine("------------");
                Console.WriteLine();
                mainMethod();

                engine.Dispose();
            }
        }

        public void MapFunction<TDelegate>(LLVMExecutionEngineRef engine, FunctionRef key, TDelegate d)
            where TDelegate : notnull
        {
            var funcRef = Backend.GetFunctionRef(key, false);
            if (funcRef == null)
            {
                return;
            }

            engine.AddGlobalMapping(
                funcRef,
                Marshal.GetFunctionPointerForDelegate(d)
            );
        }

        public TDelegate GetFunction<TDelegate>(LLVMExecutionEngineRef engine, FunctionRef key)
        {
            var funcRef = Backend.GetFunctionRef(key);
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

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate void* Alloc(nuint size);

        public static unsafe void* AllocImp(nuint size)
        {
            Console.WriteLine($"Allocating: {size}");

            return NativeMemory.Alloc(size);
        }
    }
}