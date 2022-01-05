using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public abstract class Instruction
{
    public int Id { get; init; }

    public virtual bool Terminal => false;

    public abstract void WriteIr(IrWriter writer);

    public abstract InstructionEffects GetEffects();
}