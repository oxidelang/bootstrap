using System;

namespace Oxide.Compiler.IR.Instructions
{
    public class ConstInst : Instruction
    {
        public PrimitiveKind ConstType { get; init; }

        public object Value { get; init; }

        public override bool HasValue => true;

        public override TypeRef ValueType
        {
            get
            {
                return ConstType switch
                {
                    PrimitiveKind.I32 => PrimitiveType.I32Ref,
                    PrimitiveKind.Bool => PrimitiveType.BoolRef,
                    _ => throw new ArgumentOutOfRangeException()
                };
            }
        }

        public override void WriteIr(IrWriter writer)
        {
            var type = ConstType switch
            {
                PrimitiveKind.I32 => "i32",
                PrimitiveKind.Bool => "bool",
                _ => throw new ArgumentOutOfRangeException()
            };

            writer.Write($"const {type} {Value}");
        }
    }
}