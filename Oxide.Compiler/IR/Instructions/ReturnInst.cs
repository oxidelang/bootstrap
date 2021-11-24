using System;

namespace Oxide.Compiler.IR.Instructions
{
    public class ReturnInst : Instruction
    {
        public override bool HasValue => false;
        public override TypeRef ValueType => throw new InvalidOperationException("No value");
        public int? ResultValue { get; init; }

        public override bool Terminal => true;

        public override void WriteIr(IrWriter writer)
        {
            writer.Write(ResultValue.HasValue ? $"return %{ResultValue}" : "return void");
        }
    }
}