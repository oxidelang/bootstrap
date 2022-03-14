using System;
using System.Linq;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Lifetimes;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Backend.Llvm;

public partial class FunctionGenerator
{
    private void CompileMoveInst(MoveInst inst)
    {
        var (type, value) = GetSlotRef(inst.SrcSlot);
        var properties = Store.GetCopyProperties(type);
        var slotLifetime = GetLifetime(inst).GetSlot(inst.SrcSlot);

        // Move instruction also acts as a copy instruction for copyable types
        // For optimisation purposes, we move if possible, otherwise copy if possible
        LLVMValueRef destValue;
        if (slotLifetime.Status == SlotStatus.Moved)
        {
            (_, destValue) = LoadSlot(inst.SrcSlot, $"inst_{inst.Id}");
            MarkMoved(inst.SrcSlot);
        }
        else if (properties.CanCopy)
        {
            destValue = GenerateCopy(type, properties, value, $"inst_{inst.Id}");
        }
        else
        {
            throw new Exception("Value is not moveable");
        }

        DropIfActive(inst.DestSlot, $"inst_{inst.Id}_existing");
        StoreSlot(inst.DestSlot, destValue, type);
        MarkActive(inst.DestSlot);
    }

    private void CompileConstInst(ConstInst inst)
    {
        var constValue = ConvertConstant(inst.ConstType, inst.Value);
        StoreSlot(inst.TargetSlot, constValue.value, constValue.ty);
        MarkActive(inst.TargetSlot);
    }

    private void CompileArithmeticInstruction(ArithmeticInst inst)
    {
        var name = $"inst_{inst.Id}";
        var (leftType, left) = LoadSlot(inst.LhsValue, $"{name}_left");
        var (rightType, right) = LoadSlot(inst.RhsValue, $"{name}_right");
        LLVMValueRef value;

        if (!Equals(leftType, rightType))
        {
            throw new Exception("Lhs and rhs have different type");
        }

        var integer = IsIntegerBacked(leftType);
        if (!integer)
        {
            throw new NotImplementedException("Arithmetic of non-integers not implemented");
        }

        var signed = IsSignedInteger(leftType);

        switch (inst.Op)
        {
            case ArithmeticInst.Operation.Add:
                value = Builder.BuildAdd(left, right, name);
                break;
            case ArithmeticInst.Operation.Minus:
                value = Builder.BuildSub(left, right, name);
                break;
            case ArithmeticInst.Operation.LogicalAnd:
                value = Builder.BuildAnd(left, right, name);
                break;
            case ArithmeticInst.Operation.LogicalOr:
                value = Builder.BuildOr(left, right, name);
                break;
            case ArithmeticInst.Operation.Mod:
                value = signed ? Builder.BuildSRem(left, right, name) : Builder.BuildURem(left, right, name);
                break;
            case ArithmeticInst.Operation.Multiply:
                value = Builder.BuildMul(left, right, name);
                break;
            case ArithmeticInst.Operation.Divide:
                value = signed ? Builder.BuildSDiv(left, right, name) : Builder.BuildUDiv(left, right, name);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        StoreSlot(inst.ResultSlot, value, leftType);
        MarkActive(inst.ResultSlot);
    }

    private void CompileUnaryInstruction(UnaryInst inst)
    {
        var name = $"inst_{inst.Id}";
        var (valueType, value) = LoadSlot(inst.Value, $"{name}_value");
        LLVMValueRef result;

        var integer = IsIntegerBacked(valueType);
        if (!integer)
        {
            throw new NotImplementedException("Unary operations on non-integers not implemented");
        }

        switch (inst.Op)
        {
            case UnaryInst.Operation.Not:
                result = Builder.BuildNot(value, name);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        StoreSlot(inst.ResultSlot, result, valueType);
        MarkActive(inst.ResultSlot);
    }

    private void CompileComparisonInst(ComparisonInst inst)
    {
        var name = $"inst_{inst.Id}";
        var (leftType, left) = LoadSlot(inst.LhsValue, $"{name}_left");
        var (rightType, right) = LoadSlot(inst.RhsValue, $"{name}_right");
        LLVMValueRef value;

        if (!Equals(leftType, rightType))
        {
            throw new Exception("Lhs and rhs have different type");
        }

        var integer = IsIntegerBacked(leftType);
        if (!integer)
        {
            throw new NotImplementedException("Comparison of non-integers not implemented");
        }

        var signed = IsSignedInteger(leftType);

        switch (inst.Op)
        {
            case ComparisonInst.Operation.Eq:
                value = Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right, name);
                break;
            case ComparisonInst.Operation.NEq:
                value = Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right, name);
                break;
            case ComparisonInst.Operation.GEq:
                value = Builder.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSGE : LLVMIntPredicate.LLVMIntUGE,
                    left,
                    right, name);
                break;
            case ComparisonInst.Operation.LEq:
                value = Builder.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSLE : LLVMIntPredicate.LLVMIntULE,
                    left,
                    right, name);
                break;
            case ComparisonInst.Operation.Gt:
                value = Builder.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSGT : LLVMIntPredicate.LLVMIntUGT,
                    left,
                    right, name);
                break;
            case ComparisonInst.Operation.Lt:
                value = Builder.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSLT : LLVMIntPredicate.LLVMIntULT,
                    left,
                    right, name);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        StoreSlot(inst.ResultSlot, value, PrimitiveKind.Bool.GetRef());
        MarkActive(inst.ResultSlot);
    }

    private void CompileCastInst(CastInst inst)
    {
        var (type, value) = LoadSlot(inst.SourceSlot, $"inst_{inst.Id}_load");
        var slotLifetime = GetLifetime(inst).GetSlot(inst.SourceSlot);
        var targetType = FunctionContext.ResolveRef(inst.TargetType);
        var targetLlvmType = Backend.ConvertType(targetType);

        var (castable, unsafeCast) = Store.CanCastTypes(type, targetType);
        if (!castable)
        {
            throw new Exception($"Cannot cast from {type} to {targetType}");
        }

        if (!CurrentBlock.Scope.Unsafe && unsafeCast)
        {
            throw new Exception($"Cast from {type} to {targetType} is unsafe");
        }

        LLVMValueRef converted;
        var dropIfMoved = false;
        var ignoreMoved = false;

        switch (type)
        {
            case BaseTypeRef baseTypeRef:
                if (Equals(baseTypeRef, targetType))
                {
                    converted = value;
                }
                else if (PrimitiveType.IsPrimitiveInt(type) && PrimitiveType.IsPrimitiveInt(targetType))
                {
                    // Perform extension or truncation as needed
                    var fromKind = PrimitiveType.GetKind(type);
                    var fromWidth = PrimitiveType.GetWidth(fromKind);
                    var toKind = PrimitiveType.GetKind(targetType);
                    var toWidth = PrimitiveType.GetWidth(toKind);

                    if (toWidth == fromWidth)
                    {
                        converted = value;
                    }
                    else if (toWidth < fromWidth)
                    {
                        converted = Builder.BuildTrunc(value, targetLlvmType, $"inst_{inst.Id}_trunc");
                    }
                    else if (PrimitiveType.IsSigned(fromKind))
                    {
                        converted = Builder.BuildSExt(value, targetLlvmType, $"inst_{inst.Id}_sext");
                    }
                    else
                    {
                        converted = Builder.BuildZExt(value, targetLlvmType, $"inst_{inst.Id}_zext");
                    }
                }
                // Edge case to allow casting from std::DerivedBox to derived reference type in the std library
                else if (
                    baseTypeRef is ConcreteTypeRef concreteTypeRef &&
                    targetType is DerivedRefTypeRef derivedRefTypeRef &&
                    Equals(concreteTypeRef.Name, QualifiedName.From("std", "DerivedBox")) &&
                    derivedRefTypeRef.StrongRef &&
                    Equals(derivedRefTypeRef.InnerType, concreteTypeRef.GenericParams.Single())
                )
                {
                    converted = value;
                }
                else
                {
                    throw new NotImplementedException();
                }

                break;
            case BorrowTypeRef:
            {
                if (targetType is not BorrowTypeRef && targetType is not PointerTypeRef)
                {
                    throw new Exception("Incompatible conversion");
                }

                converted = value;
                break;
            }
            case PointerTypeRef:
            {
                if (targetType is not PointerTypeRef && !CurrentBlock.Scope.Unsafe)
                {
                    throw new Exception("Conversion is unsafe");
                }

                converted = value;
                break;
            }
            case ReferenceTypeRef fromRef:
            {
                switch (targetType)
                {
                    case ReferenceTypeRef toRef:
                    {
                        if (!fromRef.StrongRef || toRef.StrongRef)
                        {
                            throw new Exception("Unsupported");
                        }

                        // Casting a reference to a weak reference requires incrementing the weak count
                        var incFunc = Backend.GetFunctionRef(
                            new FunctionRef
                            {
                                TargetMethod = ConcreteTypeRef.From(
                                    QualifiedName.From("std", "box_inc_weak"),
                                    fromRef.InnerType
                                )
                            }
                        );
                        Builder.BuildCall(incFunc, new[] {value});

                        converted = value;
                        ignoreMoved = true;
                        break;
                    }
                    case BorrowTypeRef:
                    case PointerTypeRef:
                        converted = GetBoxValuePtr(value, $"inst_{inst.Id}_ptr");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(targetType));
                }

                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }

        converted = Builder.BuildBitCast(converted, targetLlvmType, $"inst_{inst.Id}_cast");

        if (slotLifetime.Status == SlotStatus.Moved && !ignoreMoved)
        {
            if (dropIfMoved)
            {
                PerformDrop(value, type);
            }

            MarkMoved(inst.SourceSlot);
        }

        StoreSlot(inst.ResultSlot, converted, targetType);
        MarkActive(inst.ResultSlot);
    }
}