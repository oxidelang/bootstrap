using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.Parser;

namespace Oxide.Compiler.Frontend
{
    public static class CommonParsers
    {
        public static QualifiedName Parse(this OxideParser.Qualified_nameContext ctx, bool forceAbsolute = false)
        {
            var (isAbs, firstPart) = ctx switch
            {
                OxideParser.Absolute_qualified_nameContext abs => (true, abs.qualified_name_part()),
                OxideParser.Relative_qualified_nameContext rel => (false, rel.qualified_name_part()),
                _ => throw new ArgumentOutOfRangeException(nameof(ctx))
            };

            if (forceAbsolute)
            {
                isAbs = true;
            }

            var parts = new List<string>();
            firstPart.Parse(parts);

            return new QualifiedName(isAbs, parts);
        }

        private static void Parse(this OxideParser.Qualified_name_partContext ctx, List<string> output)
        {
            if (ctx.qualified_name_part() != null)
            {
                ctx.qualified_name_part().Parse(output);
            }

            output.Add(ctx.IDENTIFIER().GetText());
        }

        public static Visibility Parse(this OxideParser.VisibilityContext ctx, Visibility @default = Visibility.Private)
        {
            return ctx switch
            {
                null => @default,
                OxideParser.Public_visibilityContext => Visibility.Public,
                OxideParser.Private_visibilityContext => Visibility.Private,
                _ => throw new ArgumentOutOfRangeException(nameof(ctx))
            };
        }

        public static List<string> Parse(this OxideParser.Generic_defContext ctx)
        {
            return ctx.name().Select(x => x.GetText()).ToList();
        }
    }
}