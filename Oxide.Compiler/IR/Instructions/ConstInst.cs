using System;

namespace Oxide.Compiler.IR.Instructions
{
    public class ConstInst : Instruction
    {
        public enum ConstPrimitiveType
        {
            I32,
            Bool
        }

        public ConstPrimitiveType ConstType { get; init; }

        public object Value { get; init; }

        public override bool HasValue => true;

        public override TypeRef ValueType
        {
            get
            {
                return ConstType switch
                {
                    ConstPrimitiveType.I32 => PrimitiveType.I32Ref,
                    ConstPrimitiveType.Bool => PrimitiveType.BoolRef,
                    _ => throw new ArgumentOutOfRangeException()
                };
            }
        }

        public override void WriteIr(IrWriter writer)
        {
            var type = ConstType switch
            {
                ConstPrimitiveType.I32 => "i32",
                ConstPrimitiveType.Bool => "bool",
                _ => throw new ArgumentOutOfRangeException()
            };

            writer.Write($"const {type} {Value}");
        }
    }
}