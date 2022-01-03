using System;
using System.Collections.Generic;

namespace Oxide.Compiler.IR.Types;

public class Scope
{
    public int Id { get; init; }

    public Scope ParentScope { get; init; }

    public Dictionary<int, SlotDeclaration> Slots { get; }

    public bool Unsafe { get; init; }

    private readonly Dictionary<string, int> _variableMapping;

    public Scope()
    {
        Slots = new Dictionary<int, SlotDeclaration>();
        _variableMapping = new Dictionary<string, int>();
    }

    public SlotDeclaration DefineSlot(SlotDeclaration dec)
    {
        if (dec.ParameterSource.HasValue && ParentScope != null)
        {
            throw new Exception("Parameter variables can only be defined in root scope");
        }

        Slots.Add(dec.Id, dec);
        if (dec.Name != null)
        {
            _variableMapping[dec.Name] = dec.Id;
        }

        return dec;
    }

    public SlotDeclaration ResolveVariable(string name)
    {
        if (_variableMapping.TryGetValue(name, out var decId))
        {
            return Slots[decId];
        }

        return ParentScope?.ResolveVariable(name);
    }

    public bool CanAccessSlot(int slot)
    {
        if (Slots.ContainsKey(slot))
        {
            return true;
        }

        return ParentScope?.CanAccessSlot(slot) ?? false;
    }
}