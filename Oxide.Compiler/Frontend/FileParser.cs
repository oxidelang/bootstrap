using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.Parser;

namespace Oxide.Compiler.Frontend
{
    public class FileParser
    {
        public QualifiedName Package { get; private set; }
        public List<Import> Imports { get; private set; }
        public Dictionary<QualifiedName, StructDef> Structs { get; private set; }
        public Dictionary<QualifiedName, VariantDef> Variants { get; private set; }
        public Dictionary<QualifiedName, InterfaceDef> Interfaces { get; private set; }
        public Dictionary<QualifiedName, FunctionDef> Functions { get; private set; }
        public Dictionary<QualifiedName, OxideParser.BlockContext> UnparsedBodies { get; private set; }

        public void Parse(OxideParser.Compilation_unitContext cu)
        {
            Package = cu.package().qualified_name().Parse(true);

            Imports = new List<Import>();
            Imports.Add(new Import(CommonTypes.I32.Name, "i32"));
            Imports.Add(new Import(CommonTypes.Bool.Name, "bool"));

            foreach (var importStmt in cu.import_stmt())
            {
                Imports.Add(new Import(
                    importStmt.qualified_name().Parse(true),
                    importStmt.name()?.GetText()
                ));
            }

            // Split file
            var structDefs = new List<OxideParser.Struct_defContext>();
            var variantsDefs = new List<OxideParser.Variant_defContext>();
            var funcsDefs = new List<OxideParser.Func_defContext>();
            var ifaceDefs = new List<OxideParser.Iface_defContext>();
            var implBlocks = new List<OxideParser.Impl_stmtContext>();
            var aliasDefs = new List<OxideParser.Alias_defContext>();
            foreach (var tl in cu.top_level())
            {
                switch (tl)
                {
                    case OxideParser.Alias_top_levelContext aliasTopLevel:
                        aliasDefs.Add(aliasTopLevel.alias_def());
                        break;
                    case OxideParser.Struct_top_levelContext structTopLevel:
                        structDefs.Add(structTopLevel.struct_def());
                        break;
                    case OxideParser.Variant_top_levelContext variantTopLevel:
                        variantsDefs.Add(variantTopLevel.variant_def());
                        break;
                    case OxideParser.Func_top_levelContext fnTopLevel:
                        funcsDefs.Add(fnTopLevel.func_def());
                        break;
                    case OxideParser.Iface_top_levelContext ifaceTopLevel:
                        ifaceDefs.Add(ifaceTopLevel.iface_def());
                        break;
                    case OxideParser.Impl_top_levelContext implTopLevel:
                        implBlocks.Add(implTopLevel.impl_stmt());
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(tl));
                }
            }

            // Parse alias
            foreach (var ctx in aliasDefs)
            {
                throw new NotImplementedException("Aliases are not implemented");
            }

            // Parse structs
            Structs = new Dictionary<QualifiedName, StructDef>();
            foreach (var ctx in structDefs)
            {
                ParseStruct(ctx);
            }

            // Parse variant types
            Variants = new Dictionary<QualifiedName, VariantDef>();
            foreach (var ctx in variantsDefs)
            {
                ParseVariant(ctx);
            }

            // Parse interfaces
            Interfaces = new Dictionary<QualifiedName, InterfaceDef>();
            foreach (var ctx in ifaceDefs)
            {
                throw new NotImplementedException("Interfaces");
            }

            // Parse impls
            foreach (var ctx in implBlocks)
            {
                throw new NotImplementedException("Impl block");
            }

            // Parse top level funcs
            Functions = new Dictionary<QualifiedName, FunctionDef>();
            UnparsedBodies = new Dictionary<QualifiedName, OxideParser.BlockContext>();
            foreach (var ctx in funcsDefs)
            {
                ParseFunc(ctx);
            }
        }

        private void ParseFunc(OxideParser.Func_defContext ctx)
        {
            var vis = ctx.visibility().Parse();
            var funcName = ctx.name().GetText();
            var genericParams = ctx.generic_def().Parse().ToImmutableList();
            var parameters = ParseParameters(ctx.parameter(), false, genericParams);
            var returnType = ctx.type() != null ? ParseType(ctx.type(), genericParams) : null;
            OxideParser.BlockContext body = null;

            switch (ctx.func_body())
            {
                case OxideParser.Block_func_bodyContext blockFuncBodyContext:
                    body = blockFuncBodyContext.block();
                    break;
                case OxideParser.Empty_func_bodyContext:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var qn = new QualifiedName(true, Package.Parts.Add(funcName));
            Functions.Add(qn, new FunctionDef
            {
                Name = qn,
                Visibility = vis,
                Parameters = parameters.ToImmutableList(),
                GenericParams = genericParams.ToImmutableList(),
                ReturnType = returnType,
                IsExtern = ctx.EXTERN() != null,
                HasBody = body != null,
            });
            if (body != null)
            {
                UnparsedBodies.Add(qn, body);
            }
        }

        private List<ParameterDef> ParseParameters(OxideParser.ParameterContext[] paramCtxs, bool allowThis,
            ImmutableList<string> genericTypes)
        {
            var parameters = new List<ParameterDef>();

            for (var i = 0; i < paramCtxs.Length; i++)
            {
                var ctx = paramCtxs[i];
                switch (ctx)
                {
                    case OxideParser.Standard_parameterContext standardParameterContext:
                    {
                        parameters.Add(new ParameterDef
                        {
                            Name = standardParameterContext.name().GetText(),
                            IsThis = false,
                            Type = ParseType(standardParameterContext.type(), genericTypes)
                        });
                        break;
                    }
                    case OxideParser.This_parameterContext thisParameterContext:
                    {
                        if (!allowThis)
                        {
                            throw new Exception("This parameters are not allowed in this context");
                        }

                        if (i != 0)
                        {
                            throw new Exception("This parameters can only occupy the first parameter slot");
                        }

                        var (category, mutable) = thisParameterContext.type_flags().Parse();

                        parameters.Add(new ParameterDef
                        {
                            Name = "this",
                            IsThis = true,
                            Type = new TypeDef
                            {
                                Category = category,
                                MutableRef = mutable,
                            },
                        });
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(ctx));
                }
            }

            return parameters;
        }

        private void ParseStruct(OxideParser.Struct_defContext ctx)
        {
            var vis = ctx.visibility().Parse();
            var structName = new QualifiedName(true, Package.Parts.Add(ctx.name().GetText()));
            var genericParams = ctx.generic_def()?.Parse() ?? new List<string>();
            var fields = new List<FieldDef>();

            foreach (var fieldDef in ctx.field_def())
            {
                var type = ParseType(fieldDef.type(), genericParams.ToImmutableList());
                fields.Add(new FieldDef
                {
                    Name = fieldDef.name().GetText(),
                    Visibility = fieldDef.visibility().Parse(),
                    Type = type,
                    Mutable = fieldDef.MUT() != null,
                });
            }

            Structs.Add(
                structName,
                new StructDef(structName, vis, genericParams.ToImmutableList(), fields.ToImmutableList())
            );
        }

        private void ParseVariant(OxideParser.Variant_defContext ctx)
        {
            var vis = ctx.visibility().Parse();
            var variantName = new QualifiedName(true, Package.Parts.Add(ctx.name().GetText()));
            var genericParams = ctx.generic_def()?.Parse() ?? new List<string>();
            var items = new Dictionary<string, VariantItemDef>();

            foreach (var itemDef in ctx.variant_item_def())
            {
                switch (itemDef)
                {
                    case OxideParser.Simple_variant_item_defContext simpleDef:
                    {
                        var itemName = simpleDef.name().GetText();
                        items.Add(itemName, new VariantItemDef
                        {
                            Name = itemName,
                            Content = null
                        });
                        break;
                    }
                    case OxideParser.Struct_variant_item_defContext structDef:
                    {
                        var itemName = structDef.name().GetText();
                        var itemFields = new List<FieldDef>();

                        foreach (var fieldDef in structDef.field_def())
                        {
                            var type = ParseType(fieldDef.type(), genericParams.ToImmutableList());
                            itemFields.Add(new FieldDef
                            {
                                Name = fieldDef.name().GetText(),
                                Visibility = fieldDef.visibility().Parse(Visibility.Public),
                                Type = type,
                                Mutable = fieldDef.MUT() != null,
                            });
                        }

                        items.Add(itemName, new VariantItemDef
                        {
                            Name = itemName,
                            Content = new StructDef(
                                new QualifiedName(true, variantName.Parts.Add(itemName)),
                                Visibility.Public,
                                genericParams.ToImmutableList(),
                                itemFields.ToImmutableList()
                            ),
                        });
                        break;
                    }
                    case OxideParser.Tuple_variant_item_defContext tupleDef:
                    {
                        var itemName = tupleDef.name().GetText();
                        var itemFields = new List<FieldDef>();

                        var itemId = 0;
                        foreach (var tupleCtx in tupleDef.tuple_def().tuple_item_def())
                        {
                            var type = ParseType(tupleCtx.type(), genericParams.ToImmutableList());
                            itemFields.Add(new FieldDef
                            {
                                Name = "item" + itemId++,
                                Mutable = tupleCtx.MUT() != null,
                                Type = type,
                                Visibility = Visibility.Public
                            });
                        }

                        items.Add(itemName, new VariantItemDef
                        {
                            Name = itemName,
                            Content = new StructDef(
                                new QualifiedName(true, variantName.Parts.Add(itemName)),
                                Visibility.Public,
                                genericParams.ToImmutableList(),
                                itemFields.ToImmutableList()
                            ),
                        });
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(itemDef));
                }
            }

            Variants.Add(
                variantName,
                new VariantDef
                {
                    Name = variantName,
                    Visibility = vis,
                    GenericParams = genericParams.ToImmutableList(),
                    Items = items.ToImmutableDictionary()
                }
            );
        }

        private void ParseInterface(OxideParser.Iface_defContext ctx)
        {
            var vis = ctx.visibility().Parse();
            var structName = new QualifiedName(true, Package.Parts.Add(ctx.name().GetText()));
            var genericParams = ctx.generic_def()?.Parse() ?? new List<string>();
        }

        public TypeDef ParseType(OxideParser.TypeContext ctx, ImmutableList<string> genericTypes)
        {
            var (category, mutable) = ctx.type_flags().Parse();

            var genericParams = new List<TypeDef>();
            if (ctx.type_generic_params() != null)
            {
                genericParams.AddRange(ctx.type_generic_params().type().Select(x => ParseType(x, genericTypes)));
            }

            var rawQn = ctx.qualified_name().Parse();

            TypeSource source;
            QualifiedName qn;
            if (!rawQn.IsAbsolute && genericTypes.Contains(rawQn.Parts[0]))
            {
                source = TypeSource.Generic;
                qn = rawQn;
            }
            else
            {
                source = TypeSource.Concrete;
                qn = ResolveQN(rawQn);
            }

            return new TypeDef
            {
                Category = category,
                MutableRef = mutable,
                GenericParams = genericParams.ToImmutableArray(),
                Source = source,
                Name = qn
            };
        }

        public QualifiedName ResolveQN(QualifiedName qn)
        {
            if (qn.IsAbsolute)
            {
                return qn;
            }

            foreach (var import in Imports.AsEnumerable().Reverse())
            {
                if (import.Target == qn.Parts[0])
                {
                    return new QualifiedName(true, import.Source.Parts.AddRange(qn.Parts.Skip(1)));
                }
            }

            // Assume current package
            return new QualifiedName(true, Package.Parts.AddRange(qn.Parts));
        }
    }
}