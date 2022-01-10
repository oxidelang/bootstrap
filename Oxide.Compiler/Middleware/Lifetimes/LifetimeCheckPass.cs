using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class LifetimeCheckPass
{
    private MiddlewareManager Manager { get; }
    private LifetimePass Pass => Manager.Lifetime;

    private Dictionary<int, SlotDeclaration> _slots;

    private FunctionLifetime _functionLifetime;

    public LifetimeCheckPass(MiddlewareManager manager)
    {
        Manager = manager;
    }

    public void Analyse(IrUnit unit)
    {
        Console.WriteLine("Checking lifetimes");

        foreach (var objects in unit.Objects.Values)
        {
            if (objects is Function func)
            {
                ProcessFunction(func);
            }
        }

        foreach (var imps in unit.Implementations.Values)
        {
            foreach (var imp in imps)
            {
                foreach (var func in imp.Functions)
                {
                    ProcessFunction(func);
                }
            }
        }
    }

    private void ProcessFunction(Function func)
    {
        if (func.IsExtern || !func.HasBody)
        {
            return;
        }

        Console.WriteLine($" - Checking function {func.Name}");

        _functionLifetime = Pass.FunctionLifetimes[func];

        // Extract slots
        _slots = new Dictionary<int, SlotDeclaration>();
        foreach (var scope in func.Scopes)
        {
            foreach (var slot in scope.Slots.Values)
            {
                _slots.Add(slot.Id, slot);
            }
        }

        // Check borrows are extended
        foreach (var block in func.Blocks)
        {
            foreach (var inst in block.Instructions)
            {
                var lifetime = GetLifetime(inst);

                foreach (var slotId in lifetime.ActiveSlots)
                {
                    var slot = lifetime.GetSlot(slotId);

                    if (slot.Status == SlotStatus.Error)
                    {
                        throw new Exception($"Error with slot {slotId}: {slot.ErrorMessage}");
                    }

                    // if (slot.Borrowed)
                    // {
                    //     var otherSlot = lifetime.GetSlot(slot.From);
                    //
                    //     if (otherSlot.Status != SlotStatus.Active)
                    //     {
                    //         throw new Exception(
                    //             $"Required slot {slot.From} for {slotId} not active: {otherSlot.Status}"
                    //         );
                    //     }
                    // }

                    foreach (var slotValue in slot.Values)
                    {
                        foreach (var requiredValue in _functionLifetime.ValueRequirements[slotValue])
                        {
                            var otherSlotId = _functionLifetime.ValueMap[requiredValue];
                            if (lifetime.Overwritten.Contains(otherSlotId))
                            {
                                // TODO: More advanced check
                            }
                            else
                            {
                                var otherSlot = lifetime.GetSlot(otherSlotId);
                                if (!otherSlot.Values.Contains(requiredValue))
                                {
                                    throw new Exception(
                                        $"Slot {otherSlotId} did not contain expected value {requiredValue}"
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private InstructionLifetime GetLifetime(Instruction inst)
    {
        return _functionLifetime.InstructionLifetimes[inst.Id];
    }

    private class ValueState
    {
        public bool Borrowed { get; set; }

        public bool BorrowedMutable { get; set; }
    }
}