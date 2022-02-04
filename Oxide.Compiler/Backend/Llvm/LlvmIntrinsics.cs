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

    public static void AtomicSwap(FunctionGenerator generator, StaticCallInst inst, FunctionRef key)
    {
        if (inst.Arguments.Count != 3)
        {
            throw new Exception("Unexpected number of arguments");
        }

        var targetType = key.TargetMethod.GenericParams.Single();

        var (ptrType, ptrValue) = generator.LoadSlot(inst.Arguments[0], $"inst_{inst.Id}_ptr");
        var (oldType, oldValue) = generator.LoadSlot(inst.Arguments[1], $"inst_{inst.Id}_old");
        var (newType, newValue) = generator.LoadSlot(inst.Arguments[2], $"inst_{inst.Id}_new");

        if (
            ptrType is not PointerTypeRef pointerTypeRef ||
            !Equals(pointerTypeRef.InnerType, targetType) ||
            !Equals(oldType, targetType) ||
            !Equals(newType, targetType)
        )
        {
            throw new Exception("Incompatible types");
        }

        var resultSlot = inst.ResultSlot.Value;

        LLVMValueRef result;
        unsafe
        {
            result = LLVM.BuildAtomicCmpXchg(
                generator.Builder,
                ptrValue,
                oldValue,
                newValue,
                LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent,
                LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent,
                0
            );
        }

        var success = generator.Builder.BuildExtractValue(result, (uint)1, $"inst_{inst.Id}_success");

        generator.StoreSlot(resultSlot, success, PrimitiveKind.Bool.GetRef());
        generator.MarkActive(resultSlot);
    }

    public static void AtomicOp(FunctionGenerator generator, StaticCallInst inst, FunctionRef key,
        LLVMAtomicRMWBinOp op)
    {
        if (inst.Arguments.Count != 2)
        {
            throw new Exception("Unexpected number of arguments");
        }

        var targetType = key.TargetMethod.GenericParams.Single();

        var (ptrType, ptrValue) = generator.LoadSlot(inst.Arguments[0], $"inst_{inst.Id}_ptr");
        var (deltaType, deltaValue) = generator.LoadSlot(inst.Arguments[1], $"inst_{inst.Id}_delta");

        if (
            ptrType is not PointerTypeRef pointerTypeRef ||
            !Equals(pointerTypeRef.InnerType, targetType) ||
            !Equals(deltaType, targetType)
        )
        {
            throw new Exception("Incompatible types");
        }

        var resultSlot = inst.ResultSlot.Value;

        var result = generator.Builder.BuildAtomicRMW(
            op,
            ptrValue,
            deltaValue,
            LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent,
            false
        );

        generator.StoreSlot(resultSlot, result, targetType);
        generator.MarkActive(resultSlot);
    }

    public static void AtomicAdd(FunctionGenerator generator, StaticCallInst inst, FunctionRef key)
    {
        AtomicOp(generator, inst, key, LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpAdd);
    }

    public static void AtomicSub(FunctionGenerator generator, StaticCallInst inst, FunctionRef key)
    {
        AtomicOp(generator, inst, key, LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpSub);
    }
}