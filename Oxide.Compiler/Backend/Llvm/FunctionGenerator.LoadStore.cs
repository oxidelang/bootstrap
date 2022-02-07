using System;
using LLVMSharp.Interop;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.Backend.Llvm;

public partial class FunctionGenerator
{
    private void CompileStoreIndirectInst(StoreIndirectInst inst)
    {
        var (tgtType, tgt) = LoadSlot(inst.TargetSlot, $"inst_{inst.Id}_tgt");
        var (valType, valPtr) = GetSlotRef(inst.ValueSlot);
        var properties = Store.GetCopyProperties(valType);
        var slotLifetime = GetLifetime(inst).GetSlot(inst.ValueSlot);

        var dropExisting = false;

        switch (tgtType)
        {
            case BaseTypeRef:
                throw new Exception("Base type is not valid ptr");
                break;
            case BorrowTypeRef borrowTypeRef:
                if (!borrowTypeRef.MutableRef)
                {
                    throw new Exception("Cannot store into a non-mutable borrow");
                }

                if (!Equals(borrowTypeRef.InnerType, valType))
                {
                    throw new Exception("Value type does not match borrowed type");
                }

                dropExisting = true;
                break;
            case PointerTypeRef pointerTypeRef:
                if (!pointerTypeRef.MutableRef)
                {
                    throw new Exception("Cannot store into a non-mutable pointer");
                }

                if (!Equals(pointerTypeRef.InnerType, valType))
                {
                    throw new Exception("Value type does not match pointer type");
                }

                break;
            case ReferenceTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException(nameof(tgtType));
        }

        LLVMValueRef val;
        if (slotLifetime.Status == SlotStatus.Moved)
        {
            (_, val) = LoadSlot(inst.ValueSlot, $"inst_{inst.Id}_value");
            MarkMoved(inst.ValueSlot);
        }
        else if (properties.CanCopy)
        {
            val = GenerateCopy(valType, properties, valPtr, $"inst_{inst.Id}_value");
        }
        else
        {
            throw new Exception("Value is not moveable");
        }

        if (dropExisting)
        {
            var value = Builder.BuildLoad(tgt, $"inst_{inst.Id}_existing_value");
            PerformDrop(value, valType);
        }

        Builder.BuildStore(val, tgt);
    }

    private void CompileLoadIndirectInst(LoadIndirectInst inst)
    {
        var (addrType, addr) = LoadSlot(inst.AddressSlot, $"inst_{inst.Id}_addr");

        TypeRef innerTypeRef;
        switch (addrType)
        {
            case BaseTypeRef:
                throw new Exception("Base type is not valid ptr");
                break;
            case BorrowTypeRef borrowTypeRef:
                innerTypeRef = borrowTypeRef.InnerType;
                break;
            case PointerTypeRef pointerTypeRef:
                innerTypeRef = pointerTypeRef.InnerType;
                break;
            case ReferenceTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException(nameof(addr));
        }

        var properties = Store.GetCopyProperties(innerTypeRef);

        LLVMValueRef destValue;
        if (properties.CanCopy)
        {
            destValue = GenerateCopy(innerTypeRef, properties, addr, $"inst_{inst.Id}");
        }
        else
        {
            throw new NotImplementedException("Moves");
        }

        StoreSlot(inst.TargetSlot, destValue, innerTypeRef);
        MarkActive(inst.TargetSlot);
    }

    private void CompileFieldMoveInst(FieldMoveInst inst)
    {
        var slotDec = GetSlot(inst.BaseSlot);

        LLVMValueRef baseAddr;
        ConcreteTypeRef structType;
        bool isDirect;
        switch (slotDec.Type)
        {
            case BorrowTypeRef borrowTypeRef:
            {
                (_, baseAddr) = LoadSlot(inst.BaseSlot, $"inst_{inst.Id}_base");
                isDirect = false;

                if (borrowTypeRef.InnerType is not ConcreteTypeRef concreteTypeRef)
                {
                    throw new Exception("Cannot borrow field from non borrowed direct type");
                }

                structType = concreteTypeRef;
                break;
            }
            case BaseTypeRef:
                (_, baseAddr) = GetSlotRef(inst.BaseSlot);
                isDirect = true;
                throw new NotImplementedException("direct field moves");
            case PointerTypeRef pointerTypeRef:
            {
                (_, baseAddr) = LoadSlot(inst.BaseSlot, $"inst_{inst.Id}_base");
                isDirect = false;

                if (pointerTypeRef.InnerType is not ConcreteTypeRef concreteTypeRef)
                {
                    throw new Exception("Cannot borrow field from non borrowed direct type");
                }

                structType = concreteTypeRef;
                break;
            }
            case ReferenceTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException(nameof(slotDec.Type));
        }

        var structDef = Store.Lookup<Struct>(structType.Name);
        var index = structDef.Fields.FindIndex(x => x.Name == inst.TargetField);
        var fieldDef = structDef.Fields[index];
        var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);
        var fieldType = structContext.ResolveRef(fieldDef.Type);

        var addr = Builder.BuildInBoundsGEP(
            baseAddr,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index)
            },
            $"inst_{inst.Id}_faddr"
        );

        var properties = Store.GetCopyProperties(fieldType);

        LLVMValueRef destValue;
        if (properties.CanCopy)
        {
            destValue = GenerateCopy(fieldType, properties, addr, $"inst_{inst.Id}");
        }
        else if (!isDirect)
        {
            throw new Exception("Cannot move non-copyable field from a reference");
        }
        else
        {
            throw new NotImplementedException("Field moves");
        }

        StoreSlot(inst.TargetSlot, destValue, fieldType);
        MarkActive(inst.TargetSlot);
    }

    private void CompileFieldBorrowInst(FieldBorrowInst inst)
    {
        var (slotType, slotVal) = LoadSlot(inst.BaseSlot, $"inst_{inst.Id}_base");

        ConcreteTypeRef structType;
        switch (slotType)
        {
            case BorrowTypeRef borrowTypeRef:
            {
                if (borrowTypeRef.InnerType is not ConcreteTypeRef concreteTypeRef)
                {
                    throw new Exception("Cannot borrow field from non borrowed direct type");
                }

                if (!borrowTypeRef.MutableRef && inst.Mutable)
                {
                    throw new Exception("Cannot mutably borrow from non-mutable borrow");
                }

                structType = concreteTypeRef;
                break;
            }
            case BaseTypeRef:
                throw new Exception("Cannot borrow field from base type");
            case PointerTypeRef pointerTypeRef:
            {
                if (pointerTypeRef.InnerType is not ConcreteTypeRef concreteTypeRef)
                {
                    throw new Exception("Cannot borrow field from non borrowed direct type");
                }

                if (!pointerTypeRef.MutableRef && inst.Mutable)
                {
                    throw new Exception("Cannot mutably borrow from non-mutable borrow");
                }

                structType = concreteTypeRef;
                break;
            }
            case ReferenceTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException(nameof(slotType));
        }

        var structDef = Store.Lookup<Struct>(structType.Name);
        var index = structDef.Fields.FindIndex(x => x.Name == inst.TargetField);
        var fieldDef = structDef.Fields[index];
        var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);

        TypeRef targetType = slotType switch
        {
            BorrowTypeRef => new BorrowTypeRef(
                structContext.ResolveRef(fieldDef.Type),
                inst.Mutable
            ),
            BaseTypeRef => throw new Exception("Cannot borrow field from base type"),
            PointerTypeRef => new PointerTypeRef(
                structContext.ResolveRef(fieldDef.Type),
                inst.Mutable
            ),
            ReferenceTypeRef => throw new NotImplementedException(),
            _ => throw new ArgumentOutOfRangeException(nameof(slotType))
        };

        var addr = Builder.BuildInBoundsGEP(
            slotVal,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index)
            },
            $"inst_{inst.Id}_addr"
        );
        StoreSlot(inst.TargetSlot, addr, targetType);
        MarkActive(inst.TargetSlot);
    }

    private void CompileSlotBorrowInst(SlotBorrowInst inst)
    {
        var (slotType, slotRef) = GetSlotRef(inst.BaseSlot);
        StoreSlot(inst.TargetSlot, slotRef, new BorrowTypeRef(slotType, inst.Mutable));
        MarkActive(inst.TargetSlot);
    }
}