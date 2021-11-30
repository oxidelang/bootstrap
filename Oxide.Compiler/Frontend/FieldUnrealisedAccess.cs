using System;
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
            if (Field.Type.Source != TypeSource.Concrete)
            {
                throw new NotImplementedException("Non concrete types not implemented");
            }

            if (Field.Type.GenericParams != null && Field.Type.GenericParams.Length > 0)
            {
                throw new NotImplementedException("Generics");
            }

            var fieldType = parser.Lookup(Field.Type.Name);
            if (fieldType == null)
            {
                throw new Exception($"Failed to find {Field.Type.Name}");
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
            var baseSlot = BaseAccess.GenerateRef(parser, block, mutable);

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