using System;
using System.Linq;
using System.Text;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.IR;

/// <summary>
/// Produces a readable representation of a given set of Oxide IR
/// </summary>
public class IrWriter
{
    private int _indentLevel;
    private readonly StringBuilder _dest;

    public IrWriter()
    {
        _indentLevel = 0;
        _dest = new StringBuilder();
    }

    public void WriteStruct(Struct @struct)
    {
        BeginLine();
        Write("struct ");
        WriteVisibility(@struct.Visibility);
        Write(" ");
        WriteQn(@struct.Name);
        if (@struct.GenericParams.Count > 0)
        {
            Write("<");
            Write(string.Join(", ", @struct.GenericParams));
            Write(">");
        }

        Write(" {");
        EndLine();
        _indentLevel++;

        foreach (var field in @struct.Fields)
        {
            BeginLine();
            Write($"field {(field.Mutable ? "mut" : "readonly")} {field.Name} ");
            WriteType(field.Type);
            EndLine();
        }

        _indentLevel--;
        BeginLine();
        Write("}");
        EndLine();
    }

    public void WriteEnum(OxEnum oxEnum)
    {
        BeginLine();
        Write("enum ");
        WriteVisibility(oxEnum.Visibility);
        Write(" ");
        WriteQn(oxEnum.Name);
        Write($" {oxEnum.UnderlyingType}");

        Write(" {");
        EndLine();
        _indentLevel++;

        foreach (var item in oxEnum.Items)
        {
            WriteLine($"{item.Key} = {item.Value}");
        }

        _indentLevel--;
        BeginLine();
        Write("}");
        EndLine();
    }

    public void WriteVariant(Variant variant)
    {
        BeginLine();
        Write("variant ");
        WriteVisibility(@variant.Visibility);
        Write(" ");
        WriteQn(variant.Name);
        if (variant.GenericParams.Count > 0)
        {
            Write("<");
            Write(string.Join(", ", variant.GenericParams));
            Write(">");
        }

        Write(" {");
        EndLine();
        _indentLevel++;

        foreach (var variantItem in variant.Items)
        {
            WriteLine($"{variantItem.Name} {{");
            _indentLevel++;

            if (variantItem.NamedFields)
            {
                WriteLine("flags named");
            }

            if (variantItem.Content != null)
            {
                foreach (var field in variantItem.Content.Fields)
                {
                    BeginLine();
                    Write($"field {(field.Mutable ? "mut" : "readonly")} {field.Name} ");
                    WriteType(field.Type);
                    EndLine();
                }
            }

            _indentLevel--;
            WriteLine("}");
        }

        _indentLevel--;
        BeginLine();
        Write("}");
        EndLine();
    }

    public void WriteInterface(Interface iface)
    {
        BeginLine();
        Write("interface ");
        WriteVisibility(iface.Visibility);
        Write(" ");
        WriteQn(iface.Name);
        if (iface.GenericParams.Count > 0)
        {
            Write("<");
            Write(string.Join(", ", iface.GenericParams));
            Write(">");
        }

        Write(" {");
        EndLine();
        _indentLevel++;

        foreach (var function in iface.Functions)
        {
            WriteFunction(function);
        }

        _indentLevel--;
        BeginLine();
        Write("}");
        EndLine();
    }

    public void WriteImplementation(Implementation imp)
    {
        BeginLine();
        Write("implement");

        if (imp.GenericParams.Length > 0)
        {
            Write("<");
            Write(string.Join(", ", imp.GenericParams));
            Write(">");
        }

        Write(" ");

        if (imp.Interface != null)
        {
            WriteType(imp.Target);
            Write(" for ");
        }

        WriteType(imp.Target);

        Write(" {");
        EndLine();
        _indentLevel++;

        foreach (var function in imp.Functions)
        {
            WriteFunction(function);
        }

        _indentLevel--;
        BeginLine();
        Write("}");
        EndLine();
    }

    public void WriteFunction(Function function)
    {
        BeginLine();
        Write("func ");
        WriteVisibility(function.Visibility);

        if (function.IsExtern)
        {
            Write(" extern");
        }

        Write(" ");
        WriteQn(function.Name);
        Write(" (");

        for (var i = 0; i < function.Parameters.Count; i++)
        {
            if (i != 0)
            {
                Write(", ");
            }

            WriteParameter(function.Parameters[i]);
        }

        Write(") ");

        if (function.ReturnType == null)
        {
            Write("void");
        }
        else
        {
            WriteType(function.ReturnType);
        }

        if (!function.HasBody)
        {
            Write(";");
            EndLine();
            return;
        }

        Write(" {");
        EndLine();
        _indentLevel++;

        WriteLine($"entry = #{function.EntryBlock}");

        foreach (var scope in function.Scopes)
        {
            if (scope.ParentScope == null)
            {
                WriteScope(function, scope);
            }
        }

        _indentLevel--;
        BeginLine();
        Write("}");
        EndLine();
    }

    private void WriteScope(Function function, Scope scope)
    {
        WriteLine($"scope @{scope.Id} {{");
        _indentLevel++;

        if (scope.Unsafe)
        {
            WriteLine("flags unsafe");
        }

        foreach (var slotDec in scope.Slots.Values)
        {
            BeginLine();
            Write($"slot ${slotDec.Id} {(slotDec.Mutable ? "mut" : "readonly")} ");
            Write(slotDec.Name ?? "@internal");
            Write(" ");
            WriteType(slotDec.Type);
            if (slotDec.ParameterSource.HasValue)
            {
                Write($" = param:{slotDec.ParameterSource}");
            }

            EndLine();
        }

        foreach (var block in function.Blocks)
        {
            if (block.Scope == scope)
            {
                WriteBlock(block);
            }
        }

        foreach (var innerScope in function.Scopes)
        {
            if (innerScope.ParentScope == scope)
            {
                WriteScope(function, innerScope);
            }
        }

        _indentLevel--;
        WriteLine("}");
    }

    private void WriteBlock(Block block)
    {
        WriteLine($"block #{block.Id} {{");
        _indentLevel++;

        foreach (var instruction in block.Instructions)
        {
            BeginLine();
            instruction.WriteIr(this);
            EndLine();
        }

        _indentLevel--;
        WriteLine("}");
    }

    public void WriteParameter(Parameter param)
    {
        WriteType(param.Type);
        Write($" {param.Name}");
    }

    public void WriteType(TypeRef type)
    {
        Write(type.ToString());
    }

    public void WriteQn(QualifiedName qn)
    {
        Write(qn.ToString());
    }

    public void WriteVisibility(Visibility vis)
    {
        switch (vis)
        {
            case Visibility.Private:
                Write("private");
                break;
            case Visibility.Public:
                Write("public");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(vis), vis, null);
        }
    }

    public void WriteLine(string line)
    {
        BeginLine();
        Write(line);
        EndLine();
    }

    public void BeginLine()
    {
        _dest.Append(string.Concat(Enumerable.Repeat("    ", _indentLevel)));
    }

    public void Write(string line)
    {
        _dest.Append(line);
    }

    public void EndLine()
    {
        _dest.AppendLine();
    }

    public string Generate()
    {
        return _dest.ToString();
    }
}