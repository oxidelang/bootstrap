using System;
using Antlr4.Runtime;
using Oxide.Compiler.Parser;

namespace Oxide.Compiler.Frontend
{
    public class OxideFrontend
    {
        public void ParseFile(string file)
        {
            var lexer = new OxideLexer(new AntlrFileStream(file));
            var parser = new OxideParser(new CommonTokenStream(lexer));

            var fp = new FileParser();
            fp.Parse(parser.compilation_unit());
        }
    }
}