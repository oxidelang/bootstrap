using System;
using System.Collections.Generic;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;
using Oxide.Compiler.Middleware.Lifetimes;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Backend.Llvm;

public partial class FunctionGenerator
{
    private void CompileLoadEnumInst(LoadEnumInst inst)
    {
        var oxEnum = Store.Lookup<OxEnum>(inst.EnumName);
        if (!oxEnum.Items.TryGetValue(inst.ItemName, out var enumValue))
        {
            throw new Exception($"Invalid enum name {inst.EnumName}");
        }

        // Enums are just primtives, so a constant load
        var constValue = ConvertConstant(oxEnum.UnderlyingType, enumValue);

        StoreSlot(inst.TargetSlot, constValue.value, ConcreteTypeRef.From(inst.EnumName));
        MarkActive(inst.TargetSlot);
    }

    public LLVMValueRef GetBoxValuePtr(LLVMValueRef valueRef, string name)
    {
        var structDef = Store.Lookup<Struct>(QualifiedName.From("std", "Box"));
        var index = structDef.Fields.FindIndex(x => x.Name == "value");

        return Builder.BuildInBoundsGEP(
            valueRef,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong) index)
            },
            name
        );
    }

    public LLVMValueRef GetDerivedBoxValuePtr(LLVMValueRef valueRef, string name)
    {
        var structDef = Store.Lookup<Struct>(QualifiedName.From("std", "DerivedBox"));
        var index = structDef.Fields.FindIndex(x => x.Name == "value_ptr");

        return Builder.BuildExtractValue(
            valueRef,
            (uint) index,
            name
        );
    }

    private void CompileAllocVariantInst(AllocVariantInst inst)
    {
        var variant = Store.Lookup<Variant>(inst.VariantType.Name);
        var variantTypeRef = (ConcreteTypeRef) FunctionContext.ResolveRef(inst.VariantType);

        var variantValue = ZeroInit(variantTypeRef);
        StoreSlot(inst.SlotId, variantValue, variantTypeRef);
        MarkActive(inst.SlotId);

        var (_, baseAddr) = GetSlotRef(inst.SlotId);

        // Create type value
        var typeAddr = Builder.BuildInBoundsGEP(
            baseAddr,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
            },
            $"inst_{inst.Id}_taddr"
        );
        var index = variant.Items.FindIndex(x => x.Name == inst.ItemName);
        Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong) index), typeAddr);

        // Check if the variant item has a value
        if (inst.ItemSlot is not { } slotId) return;

        var variantItemRef = new ConcreteTypeRef(
            new QualifiedName(true, variantTypeRef.Name.Parts.Add(inst.ItemName)),
            variantTypeRef.GenericParams
        );
        var variantItemType = Backend.ConvertType(variantItemRef);

        // Store variant value
        var valueAddr = Builder.BuildInBoundsGEP(
            baseAddr,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
            },
            $"inst_{inst.Id}_vaddr"
        );
        var castedAddr = Builder.BuildBitCast(
            valueAddr,
            LLVMTypeRef.CreatePointer(variantItemType, 0),
            $"inst_{inst.Id}_vaddr_cast"
        );

        var (type, value) = LoadSlot(slotId, $"inst_{inst.Id}_load");
        if (!Equals(type, variantItemRef))
        {
            throw new Exception("Invalid variant item type");
        }

        var slotLifetime = GetLifetime(inst).GetSlot(slotId);
        if (slotLifetime.Status == SlotStatus.Moved)
        {
            MarkMoved(slotId);
        }
        else
        {
            throw new Exception("Unexpected");
        }

        Builder.BuildStore(value, castedAddr);
    }

    private void CompileRefDeriveInst(RefDeriveInst inst)
    {
        var (type, value) = GetSlotRef(inst.SourceSlot);
        var properties = Store.GetCopyProperties(type);
        var slotLifetime = GetLifetime(inst).GetSlot(inst.SourceSlot);

        LLVMValueRef fromValue;
        if (slotLifetime.Status == SlotStatus.Moved)
        {
            (_, fromValue) = LoadSlot(inst.SourceSlot, $"inst_{inst.Id}_move");
            MarkMoved(inst.SourceSlot);
        }
        else if (properties.CanCopy)
        {
            fromValue = GenerateCopy(type, properties, value, $"inst_{inst.Id}_copy");
        }
        else
        {
            throw new Exception("Value is not moveable");
        }

        DropIfActive(inst.ResultSlot, $"inst_{inst.Id}_existing");

        LLVMValueRef boxPtr;
        TypeRef ptrType;
        LLVMValueRef ptrValue;

        if (type is DerivedRefTypeRef derivedRefTypeRef)
        {
            // Derive from an existing derived reference
            var structType = ConcreteTypeRef.From(
                QualifiedName.From("std", "DerivedBox"),
                derivedRefTypeRef.InnerType
            );
            var structDef = Store.Lookup<Struct>(structType.Name);
            var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);

            var boxIndex = structDef.Fields.FindIndex(x => x.Name == "box_ptr");
            boxPtr = Builder.BuildExtractValue(fromValue, (uint) boxIndex, $"inst_{inst.Id}_box_ptr");

            var valueIndex = structDef.Fields.FindIndex(x => x.Name == "value_ptr");
            ptrType = derivedRefTypeRef.InnerType;
            ptrValue = Builder.BuildExtractValue(fromValue, (uint) valueIndex, $"inst_{inst.Id}_value");
        }
        else if (type is ReferenceTypeRef referenceTypeRef)
        {
            // Produce a derived reference from a standard reference
            if (!referenceTypeRef.StrongRef)
            {
                throw new Exception("Cannot derive weak reference");
            }

            boxPtr = fromValue;
            ptrValue = GetBoxValuePtr(boxPtr, $"inst_{inst.Id}_box");
            ptrType = referenceTypeRef.InnerType;
        }
        else
        {
            throw new Exception("Source is not a reference");
        }

        // Update borrow pointer to point to a given field
        if (inst.FieldName != null)
        {
            var structType = (ConcreteTypeRef) ptrType;
            var structDef = Store.Lookup<Struct>(structType.Name);
            var index = structDef.Fields.FindIndex(x => x.Name == inst.FieldName);
            var fieldDef = structDef.Fields[index];
            var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);
            ptrType = structContext.ResolveRef(fieldDef.Type);

            ptrValue = Builder.BuildInBoundsGEP(
                ptrValue,
                new[]
                {
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong) index)
                },
                $"inst_{inst.Id}_faddr"
            );
        }

        // Convert pointers into derived reference
        var targetMethod = ConcreteTypeRef.From(
            QualifiedName.From("std", "derived_create"),
            ptrType
        );
        var funcRef = Backend.GetFunctionRef(new FunctionRef
        {
            TargetMethod = targetMethod
        });

        var boxLlvmType = Backend.ConvertType(
            new PointerTypeRef(
                ConcreteTypeRef.From(
                    QualifiedName.From("std", "Box"),
                    ConcreteTypeRef.From(
                        QualifiedName.From("std", "Void")
                    )
                ),
                true
            )
        );
        boxPtr = Builder.BuildBitCast(boxPtr, boxLlvmType, $"inst_{inst.Id}_cast_box");

        var destValue = Builder.BuildCall(funcRef, new[] {boxPtr, ptrValue}, $"inst_{inst.Id}_derived");
        var returnType = new DerivedRefTypeRef(ptrType, true);

        StoreSlot(inst.ResultSlot, destValue, returnType);
        MarkActive(inst.ResultSlot);
    }

    private void CompileRefBorrow(RefBorrowInst inst)
    {
        var (type, value) = LoadSlot(inst.SourceSlot, $"inst_{inst.Id}_load");

        TypeRef innerType;
        LLVMValueRef valueRef;
        if (type is ReferenceTypeRef referenceTypeRef)
        {
            if (!referenceTypeRef.StrongRef)
            {
                throw new Exception("Cannot borrow weak reference");
            }

            innerType = referenceTypeRef.InnerType;
            valueRef = GetBoxValuePtr(value, $"inst_{inst.Id}_ptr");
        }
        else if (type is DerivedRefTypeRef derivedRefTypeRef)
        {
            if (!derivedRefTypeRef.StrongRef)
            {
                throw new Exception("Cannot borrow weak reference");
            }

            innerType = derivedRefTypeRef.InnerType;
            valueRef = GetDerivedBoxValuePtr(value, $"inst_{inst.Id}_ptr");
        }
        else
        {
            throw new Exception("Source is not a reference");
        }

        var targetType = new BorrowTypeRef(innerType, false);
        var targetLlvmType = Backend.ConvertType(targetType);
        valueRef = Builder.BuildBitCast(valueRef, targetLlvmType, $"inst_{inst.Id}_cast");

        StoreSlot(inst.ResultSlot, valueRef, targetType);
        MarkActive(inst.ResultSlot);
    }

    private void CompileAllocStructInst(AllocStructInst inst)
    {
        var structType = (ConcreteTypeRef) FunctionContext.ResolveRef(inst.StructType);
        var structDef = Store.Lookup<Struct>(structType.Name);
        var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);

        // Load fields
        var targetValues = new Dictionary<string, LLVMValueRef>();
        foreach (var (fname, fvalue) in inst.FieldValues)
        {
            var index = structDef.Fields.FindIndex(x => x.Name == fname);
            var fieldDef = structDef.Fields[index];
            var fieldType = structContext.ResolveRef(fieldDef.Type);
            var (valType, valPtr) = GetSlotRef(fvalue);
            var properties = Store.GetCopyProperties(valType);
            var slotLifetime = GetLifetime(inst).GetSlot(fvalue);

            if (!Equals(fieldType, valType))
            {
                throw new Exception("Value type does not match field type");
            }

            LLVMValueRef val;
            if (slotLifetime.Status == SlotStatus.Moved)
            {
                (_, val) = LoadSlot(fvalue, $"inst_{inst.Id}_field_{fname}_value");
                MarkMoved(fvalue);
            }
            else if (properties.CanCopy)
            {
                val = GenerateCopy(valType, properties, valPtr, $"inst_{inst.Id}_field_{fname}_value");
            }
            else
            {
                throw new Exception("Value is not moveable");
            }

            targetValues.Add(fname, val);
        }

        // Create empty copy of struct
        var finalValue = ZeroInit(structType);

        // Fill in fields
        foreach (var (fname, fvalue) in inst.FieldValues)
        {
            var index = structDef.Fields.FindIndex(x => x.Name == fname);
            finalValue = Builder.BuildInsertValue(
                finalValue,
                targetValues[fname],
                (uint) index,
                $"inst_{inst.Id}_field_{fname}_insert"
            );
        }

        StoreSlot(inst.SlotId, finalValue, structType);
        MarkActive(inst.SlotId);
    }
}