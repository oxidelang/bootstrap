using System;
using System.Linq;
using LLVMSharp.Interop;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
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

    public static void Bitcopy(FunctionGenerator generator, StaticCallInst inst, FunctionRef key)
    {
        var targetType = key.TargetMethod.GenericParams.Single();
        var ptrSlot = inst.Arguments.Single();
        var resultSlot = inst.ResultSlot.Value;

        var (slotType, slotValue) = generator.LoadSlot(ptrSlot, $"inst_{inst.Id}_load");
        switch (slotType)
        {
            case BaseTypeRef:
            case ReferenceTypeRef:
            case BorrowTypeRef:
                throw new Exception("Not a ptr");
            case PointerTypeRef pointerTypeRef:
                if (!Equals(pointerTypeRef.InnerType, targetType))
                {
                    throw new Exception("Incompatible types");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(slotType));
        }

        var loaded = generator.Builder.BuildLoad(slotValue, $"inst_{inst.Id}_bitcopy");
        generator.StoreSlot(resultSlot, loaded, targetType);
        generator.MarkActive(resultSlot);
    }

    public static void TypeId(FunctionGenerator generator, StaticCallInst inst, FunctionRef key)
    {
        var targetType = key.TargetMethod.GenericParams[0];
        var typePtr = generator.Backend.GetTypeInfo(targetType);
        var casted = generator.Builder.BuildPtrToInt(
            typePtr,
            generator.Backend.ConvertType(PrimitiveKind.USize.GetRef()),
            $"inst_{inst.Id}_size"
        );

        generator.StoreSlot(inst.ResultSlot.Value, casted, PrimitiveKind.USize.GetRef());
    }

    public static void TypeDrop(FunctionGenerator generator, StaticCallInst inst, FunctionRef key)
    {
        if (inst.Arguments.Count != 2)
        {
            throw new Exception("Unexpected number of arguments");
        }

        var (_, typeId) = generator.LoadSlot(inst.Arguments[0], $"inst_{inst.Id}_type_id");

        var typePtr = generator.Builder.BuildIntToPtr(
            typeId,
            LLVMTypeRef.CreatePointer(generator.Backend.TypeInfoType, 0),
            $"inst_{inst.Id}_type"
        );

        var (_, valuePtr) = generator.LoadSlot(inst.Arguments[1], $"inst_{inst.Id}_value");

        var dropPtr = generator.Builder.BuildInBoundsGEP(
            typePtr,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
            },
            $"inst_{inst.Id}_drop_ptr"
        );
        var dropFunc = generator.Builder.BuildLoad(dropPtr, $"inst_{inst.Id}_drop");

        generator.Builder.BuildCall(dropFunc, new[] { valuePtr });
    }
}