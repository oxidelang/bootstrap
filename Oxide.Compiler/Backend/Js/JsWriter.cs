using System.Linq;
using System.Text;

namespace Oxide.Compiler.Backend.Js;

public class JsWriter
{
    private int _indent;
    private StringBuilder _sb;

    public JsWriter()
    {
        _indent = 0;
        _sb = new StringBuilder();
    }

    public void Comment(string text)
    {
        BeginLine();
        Write($"// {text}");
        EndLine();
    }

    public void WriteLine(string line)
    {
        BeginLine();
        Write(line);
        EndLine();
    }

    public void BeginLine()
    {
        _sb.Append(string.Concat(Enumerable.Repeat("    ", _indent)));
    }

    public void Write(string line)
    {
        _sb.Append(line);
    }

    public void EndLine()
    {
        _sb.AppendLine();
    }

    public string Generate()
    {
        return _sb.ToString();
    }

    public void Indent(int i)
    {
        _indent += i;
    }
}