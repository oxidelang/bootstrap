using System.Collections.Immutable;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class InstructionEffects
{
    public class ReadData
    {
        public int Slot { get; private set; }

        public bool Moved { get; private set; }

        public string Field { get; private set; }

        public static ReadData Access(int slot, bool moved)
        {
            return new ReadData
            {
                Slot = slot,
                Moved = moved
            };
        }

        public static ReadData AccessField(int slot, bool moved, string field)
        {
            return new ReadData
            {
                Slot = slot,
                Moved = moved,
                Field = field
            };
        }

        public string ToDebugString()
        {
            return $"{Slot}{(Moved ? "M" : "A")}{(Field != null ? $":{Field}" : "")}";
        }
    }

    public class WriteData
    {
        public int Slot { get; private set; }

        public int? MoveSource { get; private set; }

        public string MoveField { get; private set; }

        public int? ReferenceSource { get; private set; }

        public string ReferenceField { get; private set; }

        public bool ReferenceMutable { get; private set; }

        public int? TargetBlock { get; private set; }

        public static WriteData New(int slot, int? targetBlock = null, int? moveSource = null, string moveField = null)
        {
            return new WriteData
            {
                Slot = slot,
                MoveSource = moveSource,
                MoveField = moveField,
                TargetBlock = targetBlock
            };
        }

        public static WriteData Borrow(int tgt, int source, bool mutable, int? targetBlock = null)
        {
            return new WriteData
            {
                Slot = tgt,
                ReferenceSource = source,
                ReferenceMutable = mutable,
                TargetBlock = targetBlock
            };
        }

        public static WriteData Field(int tgt, int source, string field, bool mutable, int? targetBlock = null)
        {
            return new WriteData
            {
                Slot = tgt,
                ReferenceSource = source,
                ReferenceField = field,
                ReferenceMutable = mutable,
                TargetBlock = targetBlock
            };
        }

        public string ToDebugString()
        {
            var ds = Slot.ToString();
            if (ReferenceSource != null)
            {
                ds += $"R<{ReferenceSource}";
                if (ReferenceField != null)
                {
                    ds += $":{ReferenceField}";
                }
            }
            else
            {
                ds += "N";
                if (MoveSource != null)
                {
                    ds += $"<{MoveSource}";
                    if (MoveField != null)
                    {
                        ds += $":{MoveField}";
                    }
                }
            }

            if (TargetBlock != null)
            {
                ds += $">{TargetBlock}";
            }

            return ds;
        }
    }

    public ImmutableArray<ReadData> Reads { get; }

    public ImmutableArray<WriteData> Writes { get; }

    public ImmutableArray<int> Jumps { get; }

    public InstructionEffects(ImmutableArray<ReadData> reads, ImmutableArray<WriteData> writes,
        ImmutableArray<int>? jumps = null)
    {
        Reads = reads;
        Writes = writes;
        Jumps = jumps ?? ImmutableArray<int>.Empty;
    }
}