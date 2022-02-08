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
        var (valType, val) = generator.GetConstValue(size, PrimitiveKind.USize);
        generator.StoreSlot(inst.ResultSlot.Value, val, valType);
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

    public static void TypeId(JsBodyGenerator generator, StaticCallInst inst, FunctionRef key)
    {
        var targetType = key.TargetMethod.GenericParams[0];
        var id = generator.Backend.GetTypeInfo(targetType);
        var (valType, val) = generator.GetConstValue((uint)id, PrimitiveKind.USize);
        generator.StoreSlot(inst.ResultSlot.Value, val, valType);
    }

    public static void TypeDrop(JsBodyGenerator generator, StaticCallInst inst, FunctionRef key)
    {
        if (inst.Arguments.Count != 2)
        {
            throw new Exception("Unexpected number of arguments");
        }

        var (_, typeId) = generator.LoadSlot(inst.Arguments[0], $"inst_{inst.Id}_type_id");
        var typeIdVal = $"inst_{inst.Id}_type_id_value";
        generator.Writer.WriteLine($"var {typeIdVal} = OxideMath.toI32({typeId});");

        var (_, valuePtr) = generator.LoadSlot(inst.Arguments[1], $"inst_{inst.Id}_value_ptr");
        generator.Writer.WriteLine($"OxideTypes.typetable[{typeIdVal}].drop_ptr(heap, {valuePtr});");
    }

    public static void AtomicSwap(JsBodyGenerator generator, StaticCallInst inst, FunctionRef key)
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

        var copyName = $"inst_{inst.Id}_loaded";
        generator.Writer.WriteLine($"var {copyName} = {generator.Backend.BuildLoad(targetType, ptrValue)};");


        generator.Writer.WriteLine($"if(OxideMath.toBool(OxideMath.equal({copyName}, {oldValue}))) {{");
        generator.Writer.Indent(1);

        generator.Writer.WriteLine(generator.Backend.BuildStore(targetType, ptrValue, newValue));

        var (trueType, trueValue) = generator.GetConstValue(true, PrimitiveKind.Bool);
        generator.StoreSlot(resultSlot, trueValue, trueType);

        generator.Writer.Indent(-1);
        generator.Writer.WriteLine("} else {");
        generator.Writer.Indent(1);

        var (falseType, falseValue) = generator.GetConstValue(false, PrimitiveKind.Bool);
        generator.StoreSlot(resultSlot, falseValue, falseType);

        generator.Writer.Indent(-1);
        generator.Writer.WriteLine("}");

        generator.MarkActive(resultSlot);
    }

    private static void AtomicOp(JsBodyGenerator generator, StaticCallInst inst, FunctionRef key, string op)
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

        var copyName = $"inst_{inst.Id}_loaded";
        generator.Writer.WriteLine($"var {copyName} = {generator.Backend.BuildLoad(targetType, ptrValue)};");
        generator.StoreSlot(resultSlot, copyName, targetType);
        generator.MarkActive(resultSlot);

        var valueName = $"inst_{inst.Id}_value";
        generator.Writer.WriteLine($"var {valueName} = {op}({copyName}, {deltaValue});");
        generator.Writer.WriteLine(generator.Backend.BuildStore(targetType, ptrValue, valueName));
    }

    public static void AtomicAdd(JsBodyGenerator generator, StaticCallInst inst, FunctionRef key)
    {
        AtomicOp(generator, inst, key, "OxideMath.add");
    }

    public static void AtomicSub(JsBodyGenerator generator, StaticCallInst inst, FunctionRef key)
    {
        AtomicOp(generator, inst, key, "OxideMath.sub");
    }
}