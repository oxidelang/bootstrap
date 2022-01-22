using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class AllocStructInst : Instruction
{
    public int SlotId { get; init; }

    public ConcreteTypeRef StructType { get; init; }

    public ImmutableDictionary<string, int> FieldValues { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write(
            $"allocstruct ${SlotId} {StructType} {string.Join(" ", FieldValues.Select(x => $"{x.Key}={x.Value}"))}");
    }

    public override InstructionEffects GetEffects(IrStore store)
    {
        var reads = new List<InstructionEffects.ReadData>();

        foreach (var value in FieldValues.Values)
        {
            reads.Add(InstructionEffects.ReadData.Access(value, true));
        }

        return new InstructionEffects(
            reads.ToImmutableArray(),
            new[]
            {
                InstructionEffects.WriteData.New(SlotId)
            }.ToImmutableArray()
        );
    }
}