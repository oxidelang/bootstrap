using System;
using System.Linq;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Backend.Js;

public class JsIntrinsics
{
    public static void SizeOf(JsBodyGenerator generator, StaticCallInst inst, FunctionRef key)
    {
        var targetType = key.TargetMethod.GenericParams[0];
        var size = generator.Backend.GetSize(targetType);
        generator.StoreSlot(inst.ResultSlot.Value, $"{size}", PrimitiveKind.USize.GetRef());
    }

    public static void Bitcopy(JsBodyGenerator generator, StaticCallInst inst, FunctionRef key)
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

        var copyName = $"inst_{inst.Id}_bitcopy";
        generator.Writer.WriteLine($"var {copyName} = {generator.Backend.BuildLoad(targetType, slotValue)};");
        generator.StoreSlot(resultSlot, copyName, targetType);
        generator.MarkActive(resultSlot);
    }
}