using System;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Frontend
{
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
            switch (BaseAccess.Type)
            {
                case BaseTypeRef:
                    break;
                case BorrowTypeRef borrowTypeRef:
                case PointerTypeRef pointerTypeRef:
                    throw new NotImplementedException();
                case ReferenceTypeRef referenceTypeRef:
                    throw new Exception("Unsupported");
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var baseFieldType = FieldType.GetConcreteBaseType();
            var fieldType = parser.Lookup(baseFieldType.Name);
            if (fieldType == null)
            {
                throw new Exception($"Failed to find {baseFieldType.Name}");
            }

            switch (fieldType)
            {
                case PrimitiveType primitiveType:
                {
                    var baseSlot = BaseAccess.GenerateRef(parser, block, false);

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
                case Interface @interface:
                case Struct @struct:
                case Variant variant:
                    throw new NotImplementedException("Field moves");
                default:
                    throw new ArgumentOutOfRangeException(nameof(fieldType));
            }
        }

        public override SlotDeclaration GenerateRef(BodyParser parser, Block block, bool mutable)
        {
            SlotDeclaration baseSlot;
            switch (BaseAccess.Type)
            {
                case BaseTypeRef:
                {
                    baseSlot = BaseAccess.GenerateRef(parser, block, mutable);
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
                    break;
                }
                case PointerTypeRef pointerTypeRef:
                    throw new NotImplementedException();
                case ReferenceTypeRef referenceTypeRef:
                    throw new Exception("Unsupported");
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var varSlot = block.Scope.DefineSlot(new SlotDeclaration
            {
                Id = ++parser.LastSlotId,
                Mutable = false,
                Name = null,
                Type = new BorrowTypeRef(FieldType, mutable)
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
    }
}