using System.Collections.Generic;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class InstructionLifetime
{
    public int Id { get; set; }

    public Block Block { get; set; }

    public InstructionLifetime Previous { get; set; }

    public InstructionLifetime Next { get; set; }

    public Instruction Instruction { get; set; }

    public InstructionEffects Effects { get; set; }

    public Dictionary<int, SlotState> Slots { get; }

    public HashSet<int> Overwritten { get; }

    public HashSet<int> Set { get; }

    public HashSet<int> ActiveSlots { get; }

    public InstructionLifetime()
    {
        Slots = new Dictionary<int, SlotState>();
        Overwritten = new HashSet<int>();
        Set = new HashSet<int>();
        ActiveSlots = new HashSet<int>();
    }

    public SlotState GetSlot(int id)
    {
        if (Slots.TryGetValue(id, out var state))
        {
            return state;
        }

        state = new SlotState(id, this);
        Slots.Add(id, state);
        return state;
    }
}