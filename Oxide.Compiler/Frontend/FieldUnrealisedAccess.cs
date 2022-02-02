using System;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Frontend;

public class FieldUnrealisedAccess : UnrealisedAccess
{
    public UnrealisedAccess BaseAccess { get; }

    public string FieldName { get; }

    public TypeRef FieldType { get; }

    public bool FieldMutable { get; }

    public override TypeRef Type => FieldType;

    public FieldUnrealisedAccess(UnrealisedAccess baseAccess, string fieldName, TypeRef fieldType,
        bool fieldMutable)
    {
        BaseAccess = baseAccess;
        FieldName = fieldName;
        FieldType = fieldType;
        FieldMutable = fieldMutable;
    }

    public override SlotDeclaration GenerateMove(BodyParser parser, Block block)
    {
        // TODO: Check if type is "copyable"

        SlotDeclaration baseSlot;
        switch (BaseAccess.Type)
        {
            case BaseTypeRef:
                baseSlot = BaseAccess.GenerateRef(parser, block, false);
                break;
            case BorrowTypeRef borrowTypeRef:
            {
                if (!borrowTypeRef.InnerType.IsBaseType)
                {
                    throw new Exception("Cannot move field from deeply borrowed variable");
                }

                baseSlot = BaseAccess.GenerateMove(parser, block);
                break;
            }
            case PointerTypeRef pointerTypeRef:
            {
                if (!pointerTypeRef.InnerType.IsBaseType)
                {
                    throw new Exception("Cannot move field from deeply borrowed pointer");
                }

                baseSlot = BaseAccess.GenerateMove(parser, block);
                break;
            }
            case ReferenceTypeRef referenceTypeRef:
            {
                if (!referenceTypeRef.StrongRef)
                {
                    throw new Exception("Cannot take ref to weak reference");
                }

                var refSlot = BaseAccess.GenerateMove(parser, block);

                baseSlot = block.Scope.DefineSlot(new SlotDeclaration
                {
                    Id = ++parser.LastSlotId,
                    Name = null,
                    Type = new BorrowTypeRef(referenceTypeRef.InnerType, false),
                    Mutable = true
                });

                block.AddInstruction(new RefBorrowInst
                {
                    Id = ++parser.LastInstId,
                    SourceSlot = refSlot.Id,
                    ResultSlot = baseSlot.Id
                });
                break;
            }
            case DerivedRefTypeRef derivedRefTypeRef:
            {
                if (!derivedRefTypeRef.StrongRef)
                {
                    throw new Exception("Cannot take ref to weak reference");
                }

                var refSlot = BaseAccess.GenerateMove(parser, block);

                baseSlot = block.Scope.DefineSlot(new SlotDeclaration
                {
                    Id = ++parser.LastSlotId,
                    Name = null,
                    Type = new BorrowTypeRef(derivedRefTypeRef.InnerType, false),
                    Mutable = true
                });

                block.AddInstruction(new RefBorrowInst
                {
                    Id = ++parser.LastInstId,
                    SourceSlot = refSlot.Id,
                    ResultSlot = baseSlot.Id
                });
                break;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }

        var varSlot = block.Scope.DefineSlot(new SlotDeclaration
        {
            Id = ++parser.LastSlotId,
            Mutable = false,
            Name = null,
            Type = FieldType
        });

        block.AddInstruction(new FieldMoveInst
        {
            Id = ++parser.LastInstId,
            BaseSlot = baseSlot.Id,
            TargetField = FieldName,
            TargetSlot = varSlot.Id
        });

        return varSlot;
    }

    public override SlotDeclaration GenerateRef(BodyParser parser, Block block, bool mutable)
    {
        SlotDeclaration baseSlot;
        TypeRef varType;
        switch (BaseAccess.Type)
        {
            case BaseTypeRef:
            {
                baseSlot = BaseAccess.GenerateRef(parser, block, mutable);
                varType = new BorrowTypeRef(FieldType, mutable);
                break;
            }
            case BorrowTypeRef borrowTypeRef:
            {
                if (mutable && !borrowTypeRef.MutableRef)
                {
                    throw new Exception("Cannot mutably borrow field from non-mutable borrow");
                }

                if (!borrowTypeRef.InnerType.IsBaseType)
                {
                    throw new Exception("Cannot borrow field from deeply borrowed variable");
                }

                baseSlot = BaseAccess.GenerateMove(parser, block);
                varType = new BorrowTypeRef(FieldType, mutable);
                break;
            }
            case PointerTypeRef pointerTypeRef:
            {
                if (mutable && !pointerTypeRef.MutableRef)
                {
                    throw new Exception("Cannot mutably borrow field from non-mutable pointer");
                }

                if (!pointerTypeRef.InnerType.IsBaseType)
                {
                    throw new Exception("Cannot borrow field from deeply nested pointed variable");
                }

                baseSlot = BaseAccess.GenerateMove(parser, block);
                varType = new PointerTypeRef(FieldType, mutable);
                break;
            }
            case ReferenceTypeRef referenceTypeRef:
            {
                if (!referenceTypeRef.StrongRef)
                {
                    throw new Exception("Cannot take ref to weak reference");
                }

                var refSlot = BaseAccess.GenerateMove(parser, block);

                baseSlot = block.Scope.DefineSlot(new SlotDeclaration
                {
                    Id = ++parser.LastSlotId,
                    Name = null,
                    Type = new BorrowTypeRef(referenceTypeRef.InnerType, false),
                    Mutable = true
                });

                block.AddInstruction(new RefBorrowInst
                {
                    Id = ++parser.LastInstId,
                    SourceSlot = refSlot.Id,
                    ResultSlot = baseSlot.Id
                });
                varType = new BorrowTypeRef(FieldType, false);
                break;
            }

            case DerivedRefTypeRef derivedRefTypeRef:
            {
                if (!derivedRefTypeRef.StrongRef)
                {
                    throw new Exception("Cannot take ref to weak reference");
                }

                var refSlot = BaseAccess.GenerateMove(parser, block);

                baseSlot = block.Scope.DefineSlot(new SlotDeclaration
                {
                    Id = ++parser.LastSlotId,
                    Name = null,
                    Type = new BorrowTypeRef(derivedRefTypeRef.InnerType, false),
                    Mutable = true
                });

                block.AddInstruction(new RefBorrowInst
                {
                    Id = ++parser.LastInstId,
                    SourceSlot = refSlot.Id,
                    ResultSlot = baseSlot.Id
                });
                varType = new BorrowTypeRef(FieldType, false);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }

        var varSlot = block.Scope.DefineSlot(new SlotDeclaration
        {
            Id = ++parser.LastSlotId,
            Mutable = false,
            Name = null,
            Type = varType
        });

        block.AddInstruction(new FieldBorrowInst
        {
            Id = ++parser.LastInstId,
            BaseSlot = baseSlot.Id,
            Mutable = mutable,
            TargetField = FieldName,
            TargetSlot = varSlot.Id
        });

        return varSlot;
    }

    public override SlotDeclaration GenerateDerivedRef(BodyParser parser, Block block)
    {
        var baseRef = BaseAccess.GenerateDerivedRef(parser, block);

        var varSlot = block.Scope.DefineSlot(new SlotDeclaration
        {
            Id = ++parser.LastSlotId,
            Mutable = false,
            Name = null,
            Type = new DerivedRefTypeRef(FieldType, true)
        });

        block.AddInstruction(new RefDeriveInst
        {
            Id = ++parser.LastInstId,
            SourceSlot = baseRef.Id,
            ResultSlot = varSlot.Id,
            FieldName = FieldName
        });

        return varSlot;
    }
}