using System;
using LLVMSharp.Interop;

namespace Oxide.Compiler.Backend.Llvm;

public static class LlvmAttributes
{
    public enum Target : uint
    {
        Function = UInt32.MaxValue,
    }

    public enum AttrKind : uint
    {
        AlwaysInline = 1
    }

    public static LLVMAttributeRef CreateEnumAttribute(this LLVMContextRef contextRef, AttrKind attr, ulong value = 0)
    {
        unsafe
        {
            return LLVM.CreateEnumAttribute(contextRef, (uint)attr, value);
        }
    }

    public static void AddAttribute(this LLVMValueRef valueRef, Target target, LLVMAttributeRef attrRef)
    {
        unsafe
        {
            LLVM.AddAttributeAtIndex(valueRef, (uint)target, attrRef);
        }
    }
}