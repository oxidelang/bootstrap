using LLVMSharp.Interop;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Backend.Llvm;

public static class LlvmIntrinsics
{
    public static void SizeOf(FunctionGenerator generator, StaticCallInst inst, FunctionRef key)
    {
        var targetType = key.TargetMethod.GenericParams[0];
        var converted = generator.Backend.ConvertType(targetType);

        var nullPtr = LLVMValueRef.CreateConstPointerNull(LLVMTypeRef.CreatePointer(converted, 0));

        var size = generator.Builder.BuildGEP(nullPtr, new[]
        {
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1),
        }, $"inst_{inst.Id}_size");

        var casted = generator.Builder.BuildPtrToInt(
            size,
            generator.Backend.ConvertType(PrimitiveKind.USize.GetRef()),
            $"inst_{inst.Id}_size"
        );

        generator.StoreSlot(inst.ResultSlot.Value, casted, PrimitiveKind.USize.GetRef());
    }
}