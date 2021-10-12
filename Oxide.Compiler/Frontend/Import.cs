using System.Linq;
using Oxide.Compiler.IR;

namespace Oxide.Compiler.Frontend
{
    public class Import
    {
        public QualifiedName Source { get; }

        public string Target { get; }

        public Import(QualifiedName source, string target)
        {
            Source = source;
            Target = target ?? source.Parts.Last();
        }
    }
}