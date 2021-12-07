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

        public Field Field { get; }

        public override TypeRef Type => Field.Type;

        public FieldUnrealisedAccess(UnrealisedAccess baseAccess, Field field)
        {
            BaseAccess = baseAccess;
            Field = field;
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

            var baseFieldType = Field.Type.GetBaseType();
            QualifiedName structName;
            switch (baseFieldType)
            {
                case ConcreteTypeRef concreteTypeRef:
                    if (concreteTypeRef.GenericParams != null && concreteTypeRef.GenericParams.Length > 0)
                    {
                        throw new NotImplementedException("Generics");
                    }

                    structName = concreteTypeRef.Name;
                    break;
                case DerivedTypeRef derivedTypeRef:
                case ThisTypeRef thisTypeRef:
                case GenericTypeRef genericTypeRef:
                    throw new NotImplementedException();
                default:
                    throw new ArgumentOutOfRangeException(nameof(baseFieldType));
            }


            var fieldType = parser.Lookup(structName);
            if (fieldType == null)
            {
                throw new Exception($"Failed to find {structName}");
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
                        Type = Field.Type
                    });

                    block.AddInstruction(new FieldMoveInst
                    {
                        Id = ++parser.LastInstId,
                        BaseSlot = baseSlot.Id,
                        TargetField = Field.Name,
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
                Type = new BorrowTypeRef(Field.Type, mutable)
            });

            block.AddInstruction(new FieldBorrowInst
            {
                Id = ++parser.LastInstId,
                BaseSlot = baseSlot.Id,
                Mutable = mutable,
                TargetField = Field.Name,
                TargetSlot = varSlot.Id
            });

            return varSlot;
        }
    }
}