using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Backend.Llvm;

public class LlvmRunner
{
    public LlvmBackend Backend { get; }

    public LlvmRunner(LlvmBackend backend)
    {
        Backend = backend;
    }

    public void Run()
    {
        // Map external standard library functions to a csharp implementation
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
            
            MapFunction<DebugInt>(
                engine,
                new FunctionRef
                {
                    TargetMethod = ConcreteTypeRef.From(QualifiedName.From("std", "output_int"))
                },
                OutputIntImp
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

            MapFunction<Free>(
                engine,
                new FunctionRef
                {
                    TargetMethod = ConcreteTypeRef.From(QualifiedName.From("std", "free"))
                },
                FreeImp
            );

            MapFunction<Exit>(
                engine,
                new FunctionRef
                {
                    TargetMethod = ConcreteTypeRef.From(QualifiedName.From("std", "exit"))
                },
                ExitImp
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
        // Console.WriteLine($"DebugInt: {val}");
    }
    
    public static void OutputIntImp(int val)
    {
        Console.WriteLine($"OutputInt: {val}");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DebugBool(byte a);

    public static void DebugBoolImp(byte val)
    {
        Console.WriteLine($"DebugBool: {val}");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void* Alloc(nuint size);

    public static int ActiveCount = 0, LastId = 1;
    public static HashSet<UIntPtr> Active = new();
    public static Dictionary<UIntPtr, int> Ids = new();

    public static unsafe void* AllocImp(nuint size)
    {
        var ptr = NativeMemory.Alloc(size);

        var id = LastId++;
        Ids.Add((UIntPtr)ptr, id);

        Console.WriteLine($"[active={++ActiveCount}] Allocating: {size}  [${id}$]");

        Active.Add((UIntPtr)ptr);

        return ptr;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void Free(void* ptr);

    public static unsafe void FreeImp(void* ptr)
    {
        var id = Ids[(UIntPtr)ptr];

        Console.WriteLine($"[active={--ActiveCount}] Freeing [${id}$]");

        if (Active.Remove((UIntPtr)ptr))
        {
            NativeMemory.Free(ptr);
        }
        else
        {
            Console.WriteLine($"DOUBLE FREE DETECTED [${id}$]");
            throw new Exception();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Exit(int code);

    public static void ExitImp(int code)
    {
        Console.WriteLine($"[EXIT] code={code}");
        Environment.Exit(code);
    }
}