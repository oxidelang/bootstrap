using System;
using System.Linq;
using System.Text;

namespace Oxide.Compiler.IR
{
    public class IrWriter
    {
        private int _indentLevel;
        private readonly StringBuilder _dest;

        public IrWriter()
        {
            _indentLevel = 0;
            _dest = new StringBuilder();
        }

        public void WriteFunction(FunctionDef functionDef)
        {
            BeginLine();
            Write("func ");
            WriteVisibility(functionDef.Visibility);

            if (functionDef.IsExtern)
            {
                Write(" extern");
            }

            Write(" ");
            WriteQn(functionDef.Name);
            Write(" (");

            for (var i = 0; i < functionDef.Parameters.Count; i++)
            {
                if (i != 0)
                {
                    Write(", ");
                }

                WriteParameter(functionDef.Parameters[i]);
            }

            Write(") ");

            if (functionDef.ReturnType == null)
            {
                Write("void");
            }
            else
            {
                WriteType(functionDef.ReturnType);
            }

            if (!functionDef.HasBody)
            {
                Write(";");
                EndLine();
                return;
            }

            Write(" {");
            EndLine();
            _indentLevel++;

            WriteLine($"entry = #{functionDef.EntryBlock}");

            foreach (var scope in functionDef.Scopes)
            {
                if (scope.ParentScope == null)
                {
                    WriteScope(functionDef, scope);
                }
            }

            _indentLevel--;
            BeginLine();
            Write("}");
            EndLine();
        }

        private void WriteScope(FunctionDef functionDef, Scope scope)
        {
            WriteLine($"scope @{scope.Id} {{");
            _indentLevel++;

            foreach (var varDec in scope.Variables.Values)
            {
                BeginLine();
                Write($"var ${varDec.Id} {(varDec.Mutable ? "mut" : "readonly")} ");
                Write(varDec.Name);
                Write(" ");
                WriteType(varDec.Type);
                if (varDec.ParameterSource.HasValue)
                {
                    Write($" = param:{varDec.ParameterSource}");
                }

                EndLine();
            }

            foreach (var innerScope in functionDef.Scopes)
            {
                if (innerScope.ParentScope == scope)
                {
                    WriteScope(functionDef, innerScope);
                }
            }

            foreach (var block in functionDef.Blocks)
            {
                if (block.Scope == scope)
                {
                    WriteBlock(block);
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
                Write($"%{instruction.Id} = ");
                instruction.WriteIr(this);
                EndLine();
            }

            _indentLevel--;
            WriteLine("}");
        }

        public void WriteParameter(ParameterDef param)
        {
            if (param.IsThis)
            {
                Write("this");
            }
            else
            {
                WriteType(param.Type);
                Write($" {param.Name}");
            }
        }

        public void WriteType(TypeDef type)
        {
            Write("[");

            switch (type.Category)
            {
                case TypeCategory.Direct:
                    Write("d");
                    break;
                case TypeCategory.Pointer:
                    Write("p");
                    break;
                case TypeCategory.Reference:
                    Write(type.MutableRef ? "m" : "r");
                    Write("r");
                    break;
                case TypeCategory.StrongReference:
                    Write("s");
                    break;
                case TypeCategory.WeakReference:
                    Write("w");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            switch (type.Source)
            {
                case TypeSource.Concrete:
                    Write("c");
                    break;
                case TypeSource.Generic:
                    Write("g");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            Write("]");
            WriteQn(type.Name);
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
}