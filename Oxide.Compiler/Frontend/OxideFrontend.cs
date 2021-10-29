using System;
using System.Collections.Immutable;
using Antlr4.Runtime;
using Oxide.Compiler.IR;
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

            foreach (var functionDef in fp.Functions.Values)
            {
                var bodyParser = new BodyParser(fp, functionDef.GenericParams);
                bodyParser.ParseBody(functionDef.UnparsedBody);
                functionDef.Blocks = bodyParser.Blocks.Values.ToImmutableList();
                functionDef.Scopes = bodyParser.Scopes.ToImmutableList();
                functionDef.UnparsedBody = null;
            }

            var irWriter = new IrWriter();

            foreach (var functionDef in fp.Functions.Values)
            {
                irWriter.WriteFunction(functionDef);
            }

            Console.WriteLine(irWriter.Generate());
        }
    }
}