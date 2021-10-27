using System;

namespace Oxide.Compiler.IR.Instructions
{
    public class ConstInst : Instruction
    {
        public enum PrimitiveType
        {
            I32
        }

        public PrimitiveType ConstType { get; init; }

        public object Value { get; init; }

        public override bool HasValue => true;

        public override TypeDef ValueType
        {
            get
            {
                return ConstType switch
                {
                    PrimitiveType.I32 => CommonTypes.I32,
                    _ => throw new ArgumentOutOfRangeException()
                };
            }
        }
    }
}