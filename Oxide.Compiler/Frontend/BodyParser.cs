using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.Parser;
using Object = System.Object;

namespace Oxide.Compiler.Frontend
{
    public class BodyParser
    {
        private readonly IrStore _store;
        private readonly IrUnit _unit;
        private readonly FileParser _fileParser;
        private readonly Function _function;

        public List<Scope> Scopes { get; private set; }
        private int _lastScopeId;
        private Scope CurrentScope { get; set; }

        private int _lastVariableId;

        public Dictionary<int, Block> Blocks { get; private set; }
        private int _lastBlockId;
        private int _currentBlockId;
        private Block CurrentBlock => Blocks[_currentBlockId];

        private int _lastInstId;

        public BodyParser(IrStore store, IrUnit unit, FileParser fileParser, Function function)
        {
            _store = store;
            _unit = unit;
            _function = function;
            _fileParser = fileParser;
            Scopes = new List<Scope>();
            Blocks = new Dictionary<int, Block>();
            _lastScopeId = 0;
            _lastBlockId = 0;
            _lastVariableId = 0;
        }

        public int ParseBody(OxideParser.BlockContext ctx)
        {
            var scope = PushScope();

            for (var i = 0; i < _function.Parameters.Count; i++)
            {
                var paramDef = _function.Parameters[i];
                if (paramDef.IsThis)
                {
                    throw new NotImplementedException("This params are not supported");
                }

                scope.DefineVariable(new VariableDeclaration
                {
                    Id = ++_lastVariableId,
                    Name = paramDef.Name,
                    Type = paramDef.Type,
                    Mutable = false,
                    ParameterSource = i
                });
            }

            var block = NewBlock(CurrentScope);
            MakeCurrent(block);

            // TODO: Init context
            var finalOp = ParseBlock(ctx);

            // TODO: Improve
            if (!CurrentBlock.HasTerminated)
            {
                // TODO: Attempt to return value from finalOp

                if (_function.ReturnType != null)
                {
                    throw new Exception("Function does not return value");
                }

                CurrentBlock.AddInstruction(new ReturnInst
                {
                    Id = ++_lastInstId
                });
            }

            return block.Id;
        }

        private Instruction ParseBlock(OxideParser.BlockContext ctx)
        {
            if (ctx.statement() != null)
            {
                foreach (var statement in ctx.statement())
                {
                    ParseStatement(statement);
                }
            }

            return ctx.expression() != null ? ParseExpression(ctx.expression()) : null;
        }

        private void ParseStatement(OxideParser.StatementContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Empty_statementContext:
                    // Ignore empty statement
                    break;
                case OxideParser.Block_expression_statementContext blockExpressionStatementContext:
                    ParseBlockExpression(blockExpressionStatementContext.block_expression());
                    break;
                case OxideParser.Expression_statementContext expressionStatementContext:
                    ParseExpression(expressionStatementContext.expression());
                    break;
                case OxideParser.Variable_statement_topContext variableStatementTopContext:
                    ParseVariableStatement(variableStatementTopContext.variable_statement());
                    break;
                case OxideParser.Assign_statement_topContext assignStatementTopContext:
                    ParseAssignStatement(assignStatementTopContext.assign_statement());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseAssignStatement(OxideParser.Assign_statementContext assignStatementContext)
        {
            var exp = ParseExpression(assignStatementContext.expression());
            if (!exp.HasValue)
            {
                throw new Exception($"No value returned by {exp.Id}");
            }

            if (exp.ValueType.Source != TypeSource.Concrete)
            {
                throw new NotImplementedException("Non concrete types not implemented");
            }

            if (exp.ValueType.GenericParams != null && exp.ValueType.GenericParams.Length > 0)
            {
                throw new NotImplementedException("Generics");
            }

            switch (assignStatementContext.assign_target())
            {
                case OxideParser.Field_assign_targetContext fieldAssignTargetContext:
                {
                    var fieldName = fieldAssignTargetContext.name().GetText();
                    var tgt = ParseBaseExpression(fieldAssignTargetContext.base_expression());
                    if (!tgt.HasValue)
                    {
                        throw new Exception($"Cannot access {fieldName} on value with no type");
                    }

                    if (tgt.ValueType.Source != TypeSource.Concrete)
                    {
                        throw new NotImplementedException("Non concrete types not implemented");
                    }

                    if (tgt.ValueType.GenericParams != null && tgt.ValueType.GenericParams.Length > 0)
                    {
                        throw new NotImplementedException("Generics");
                    }

                    var structDef = Lookup<Struct>(tgt.ValueType.Name);
                    if (structDef == null)
                    {
                        throw new Exception($"Failed to find {tgt.ValueType.Name}");
                    }

                    var fieldDef = structDef.Fields.Single(x => x.Name == fieldName);

                    if (!exp.ValueType.Equals(fieldDef.Type))
                    {
                        throw new Exception($"Cannot assign {exp.ValueType} to {fieldDef.Type}");
                    }

                    CurrentBlock.AddInstruction(new StoreFieldInst
                    {
                        Id = ++_lastInstId,
                        TargetId = tgt.Id,
                        TargetField = fieldName,
                        TargetType = structDef.Name,
                        ValueId = exp.Id
                    });
                    break;
                }
                case OxideParser.Qualified_assign_targetContext qualifiedAssignTargetContext:
                {
                    var varDec = ResolveQn(qualifiedAssignTargetContext.qualified_name(),
                        qualifiedAssignTargetContext.type_generic_params());

                    if (!exp.ValueType.Equals(varDec.Type))
                    {
                        throw new Exception($"Cannot assign {exp.ValueType} to {varDec.Type}");
                    }

                    CurrentBlock.AddInstruction(new StoreLocalInst
                    {
                        Id = ++_lastInstId,
                        LocalId = varDec.Id,
                        ValueId = exp.Id
                    });
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private Instruction ParseBlockExpression(OxideParser.Block_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Block_block_expressionContext child:
                {
                    if (child.UNSAFE() != null)
                    {
                        throw new NotImplementedException("Unsafe blocks");
                    }

                    var originalScope = CurrentScope;
                    var originalBlock = CurrentBlock;

                    PushScope();
                    var block = NewBlock(CurrentScope);
                    MakeCurrent(block);

                    originalBlock.AddInstruction(new JumpInst
                    {
                        Id = ++_lastInstId,
                        TargetBlock = block.Id
                    });

                    var finalOp = ParseBlock(child.block());

                    var finalBlock = CurrentBlock;
                    if (finalBlock.HasTerminated)
                    {
                        throw new NotImplementedException("TODO");
                    }

                    var returnBlock = NewBlock(originalScope);
                    MakeCurrent(returnBlock);

                    if (finalOp.HasValue)
                    {
                        var resultDec = originalScope.DefineVariable(new VariableDeclaration
                        {
                            Id = ++_lastVariableId,
                            Name = null,
                            Type = finalOp.ValueType,
                            Mutable = false
                        });

                        finalBlock.AddInstruction(new StoreLocalInst
                        {
                            Id = ++_lastInstId,
                            LocalId = resultDec.Id,
                            ValueId = finalOp.Id
                        });
                        finalBlock.AddInstruction(new JumpInst
                        {
                            Id = ++_lastInstId,
                            TargetBlock = returnBlock.Id
                        });
                        return returnBlock.AddInstruction(new LoadLocalInst
                        {
                            Id = ++_lastInstId,
                            LocalId = resultDec.Id,
                            LocalType = resultDec.Type,
                        });
                    }

                    finalBlock.AddInstruction(new JumpInst
                    {
                        Id = ++_lastInstId,
                        TargetBlock = returnBlock.Id
                    });
                    return null;
                }
                case OxideParser.If_block_expressionContext child:
                    return ParseIfExpression(child.if_expression());
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseIfExpression(OxideParser.If_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Simple_if_expressionContext child:
                {
                    var condInst = ParseExpression(child.expression());

                    var originalScope = CurrentScope;
                    var originalBlock = CurrentBlock;

                    // Configure jump paths
                    var returnBlock = NewBlock(originalScope);
                    PushScope();
                    var trueBlock = NewBlock(CurrentScope);
                    RestoreScope(originalScope);

                    var hasElse = child.else_block != null || child.else_if != null;
                    var falseBlock = hasElse ? NewBlock(CurrentScope) : null;
                    originalBlock.AddInstruction(new JumpInst
                    {
                        Id = ++_lastInstId,
                        ConditionValue = condInst.Id,
                        TargetBlock = trueBlock.Id,
                        ElseBlock = hasElse ? falseBlock.Id : returnBlock.Id
                    });

                    MakeCurrent(trueBlock);
                    var trueOp = ParseBlock(child.body);
                    var trueFinalBlock = CurrentBlock;
                    if (trueFinalBlock.HasTerminated)
                    {
                        throw new NotImplementedException("TODO");
                    }

                    Instruction falseOp = null;
                    Block falseFinalBlock = null;
                    if (hasElse)
                    {
                        MakeCurrent(falseBlock);
                        falseOp = child.else_block != null
                            ? ParseBlock(child.else_block)
                            : ParseIfExpression(child.else_if);
                        falseFinalBlock = CurrentBlock;
                        if (falseFinalBlock.HasTerminated)
                        {
                            throw new NotImplementedException("TODO");
                        }
                    }

                    MakeCurrent(returnBlock);

                    if (hasElse && trueOp.HasValue && falseOp.HasValue)
                    {
                        if (!Equals(trueOp.ValueType, falseOp.ValueType))
                        {
                            throw new NotImplementedException("Incompatible if block true and false path values");
                        }

                        var resultDec = originalScope.DefineVariable(new VariableDeclaration
                        {
                            Id = ++_lastVariableId,
                            Name = null,
                            Type = trueOp.ValueType,
                            Mutable = false
                        });

                        trueFinalBlock.AddInstruction(new StoreLocalInst
                        {
                            Id = ++_lastInstId,
                            LocalId = resultDec.Id,
                            ValueId = trueOp.Id
                        });
                        trueFinalBlock.AddInstruction(new JumpInst
                        {
                            Id = ++_lastInstId,
                            TargetBlock = returnBlock.Id
                        });

                        falseFinalBlock.AddInstruction(new StoreLocalInst
                        {
                            Id = ++_lastInstId,
                            LocalId = resultDec.Id,
                            ValueId = falseOp.Id
                        });
                        falseFinalBlock.AddInstruction(new JumpInst
                        {
                            Id = ++_lastInstId,
                            TargetBlock = returnBlock.Id
                        });

                        return returnBlock.AddInstruction(new LoadLocalInst
                        {
                            Id = ++_lastInstId,
                            LocalId = resultDec.Id,
                            LocalType = resultDec.Type,
                        });
                    }

                    trueFinalBlock.AddInstruction(new JumpInst
                    {
                        Id = ++_lastInstId,
                        TargetBlock = returnBlock.Id
                    });
                    if (hasElse)
                    {
                        falseFinalBlock.AddInstruction(new JumpInst
                        {
                            Id = ++_lastInstId,
                            TargetBlock = returnBlock.Id
                        });
                    }

                    return null;
                }
                case OxideParser.Let_if_expressionContext:
                    throw new NotImplementedException("If let expressions");
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseVariableStatement(OxideParser.Variable_statementContext ctx)
        {
            var name = ctx.name().GetText();
            var type = ctx.type() != null ? ParseType(ctx.type()) : null;
            var mutable = ctx.MUT() != null;
            int? valueId = null;

            if (ctx.expression() != null)
            {
                var inst = ParseExpression(ctx.expression());

                if (!inst.HasValue)
                {
                    throw new Exception($"No value returned by {inst.Id}");
                }

                if (type == null)
                {
                    type = inst.ValueType;
                }
                else
                {
                    throw new NotImplementedException("Type compatability checking not implemented");
                }

                valueId = inst.Id;
            }
            else if (ctx.type() == null)
            {
                throw new Exception("Variable declarations without an expression must have a type");
            }

            if (type == null)
            {
                throw new Exception($"Unable to resolve type for variable {name}");
            }

            var varDec = CurrentScope.DefineVariable(new VariableDeclaration
            {
                Id = ++_lastVariableId,
                Name = name,
                Type = type,
                Mutable = mutable
            });

            if (valueId.HasValue)
            {
                CurrentBlock.AddInstruction(new StoreLocalInst
                {
                    Id = ++_lastInstId,
                    LocalId = varDec.Id,
                    ValueId = valueId.Value
                });
            }
        }

        private Instruction ParseExpression(OxideParser.ExpressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_expressionContext passExpressionContext:
                    return ParseOrExpression(passExpressionContext.or_expression());
                case OxideParser.Return_expressionContext returnExpressionContext:
                {
                    var result = returnExpressionContext.or_expression() != null
                        ? (int?)ParseOrExpression(returnExpressionContext.or_expression()).Id
                        : null;
                    return CurrentBlock.AddInstruction(new ReturnInst
                    {
                        Id = ++_lastInstId,
                        ResultValue = result
                    });
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseOrExpression(OxideParser.Or_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_or_expressionContext passOrExpressionContext:
                    return ParseAndExpression(passOrExpressionContext.and_expression());
                case OxideParser.Op_or_expressionContext opOrExpressionContext:
                    throw new NotImplementedException("Or expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseAndExpression(OxideParser.And_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_and_expressionContext passAndExpressionContext:
                    return ParseIncOrExpression(passAndExpressionContext.inc_or_expression());
                case OxideParser.Op_and_expressionContext opAndExpressionContext:
                    throw new NotImplementedException("And expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseIncOrExpression(OxideParser.Inc_or_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_inc_or_expressionContext passIncOrExpressionContext:
                    return ParseExOrExpression(passIncOrExpressionContext.ex_or_expression());
                case OxideParser.Op_inc_or_expressionContext opIncOrExpressionContext:
                    throw new NotImplementedException("Inc or expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseExOrExpression(OxideParser.Ex_or_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_ex_or_expressionContext passExOrExpressionContext:
                    return ParseBitAndExpression(passExOrExpressionContext.bit_and_expression());
                case OxideParser.Op_ex_or_expressionContext opExOrExpressionContext:
                    throw new NotImplementedException("Ex or expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseBitAndExpression(OxideParser.Bit_and_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_bit_and_expressionContext passBitAndExpressionContext:
                    return ParseEqualExpression(passBitAndExpressionContext.equal_expression());
                case OxideParser.Op_bit_and_expressionContext opBitAndExpressionContext:
                    throw new NotImplementedException("Bit and expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseEqualExpression(OxideParser.Equal_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_equal_expressionContext passEqualExpressionContext:
                    return ParseComparisonExpression(passEqualExpressionContext.comparison_expression());
                case OxideParser.Eq_equal_expressionContext child:
                {
                    var left = ParseEqualExpression(child.equal_expression());
                    var right = ParseComparisonExpression(child.comparison_expression());
                    if (!left.ValueType.Equals(right.ValueType))
                    {
                        throw new NotImplementedException("Comparison of different types not implemented");
                    }

                    return CurrentBlock.AddInstruction(new ComparisonInst
                    {
                        Id = ++_lastInstId,
                        LhsValue = left.Id,
                        RhsValue = right.Id,
                        Op = ComparisonInst.Operation.Eq
                    });
                }
                case OxideParser.Ne_equal_expressionContext child:
                {
                    var left = ParseEqualExpression(child.equal_expression());
                    var right = ParseComparisonExpression(child.comparison_expression());
                    if (!left.ValueType.Equals(right.ValueType))
                    {
                        throw new NotImplementedException("Comparison of different types not implemented");
                    }

                    return CurrentBlock.AddInstruction(new ComparisonInst
                    {
                        Id = ++_lastInstId,
                        LhsValue = left.Id,
                        RhsValue = right.Id,
                        Op = ComparisonInst.Operation.NEq
                    });
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseComparisonExpression(OxideParser.Comparison_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_comparison_expressionContext passComparisonExpressionContext:
                    return ParseCastExpression(passComparisonExpressionContext.cast_expression());
                case OxideParser.Geq_comparison_expressionContext child:
                {
                    var left = ParseComparisonExpression(child.comparison_expression());
                    var right = ParseCastExpression(child.cast_expression());
                    if (!left.ValueType.Equals(right.ValueType))
                    {
                        throw new NotImplementedException("Comparison of different types not implemented");
                    }

                    return CurrentBlock.AddInstruction(new ComparisonInst
                    {
                        Id = ++_lastInstId,
                        LhsValue = left.Id,
                        RhsValue = right.Id,
                        Op = ComparisonInst.Operation.GEq
                    });
                }
                case OxideParser.Gt_comparison_expressionContext child:
                {
                    var left = ParseComparisonExpression(child.comparison_expression());
                    var right = ParseCastExpression(child.cast_expression());
                    if (!left.ValueType.Equals(right.ValueType))
                    {
                        throw new NotImplementedException("Comparison of different types not implemented");
                    }

                    return CurrentBlock.AddInstruction(new ComparisonInst
                    {
                        Id = ++_lastInstId,
                        LhsValue = left.Id,
                        RhsValue = right.Id,
                        Op = ComparisonInst.Operation.Gt
                    });
                }
                case OxideParser.Leq_comparison_expressionContext child:
                {
                    var left = ParseComparisonExpression(child.comparison_expression());
                    var right = ParseCastExpression(child.cast_expression());
                    if (!left.ValueType.Equals(right.ValueType))
                    {
                        throw new NotImplementedException("Comparison of different types not implemented");
                    }

                    return CurrentBlock.AddInstruction(new ComparisonInst
                    {
                        Id = ++_lastInstId,
                        LhsValue = left.Id,
                        RhsValue = right.Id,
                        Op = ComparisonInst.Operation.LEq
                    });
                }
                case OxideParser.Lt_comparison_expressionContext child:
                {
                    var left = ParseComparisonExpression(child.comparison_expression());
                    var right = ParseCastExpression(child.cast_expression());
                    if (!left.ValueType.Equals(right.ValueType))
                    {
                        throw new NotImplementedException("Comparison of different types not implemented");
                    }

                    return CurrentBlock.AddInstruction(new ComparisonInst
                    {
                        Id = ++_lastInstId,
                        LhsValue = left.Id,
                        RhsValue = right.Id,
                        Op = ComparisonInst.Operation.Lt
                    });
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseCastExpression(OxideParser.Cast_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_cast_expressionContext passCastExpressionContext:
                    return ParseShiftExpression(passCastExpressionContext.shift_expression());
                case OxideParser.Op_cast_expressionContext opCastExpressionContext:
                    throw new NotImplementedException("cast expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseShiftExpression(OxideParser.Shift_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_shift_expressionContext passShiftExpressionContext:
                    return ParseAddExpression(passShiftExpressionContext.add_expression());
                case OxideParser.Left_shift_expressionContext leftShiftExpressionContext:
                    throw new NotImplementedException("left shift expression");
                    break;
                case OxideParser.Right_shift_expressionContext rightShiftExpressionContext:
                    throw new NotImplementedException("right shift expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseAddExpression(OxideParser.Add_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_add_expressionContext passAddExpressionContext:
                    return ParseMultiplyExpression(passAddExpressionContext.multiply_expression());
                case OxideParser.Minus_add_expressionContext minusAddExpressionContext:
                {
                    var left = ParseAddExpression(minusAddExpressionContext.add_expression());
                    var right = ParseMultiplyExpression(minusAddExpressionContext.multiply_expression());
                    if (!left.ValueType.Equals(right.ValueType))
                    {
                        throw new NotImplementedException("Arithmetic of different types not implemented");
                    }

                    return CurrentBlock.AddInstruction(new ArithmeticInst
                    {
                        Id = ++_lastInstId,
                        LhsValue = left.Id,
                        RhsValue = right.Id,
                        OutputType = left.ValueType,
                        Op = ArithmeticInst.Operation.Minus
                    });
                }
                case OxideParser.Plus_add_expressionContext plusAddExpressionContext:
                {
                    var left = ParseAddExpression(plusAddExpressionContext.add_expression());
                    var right = ParseMultiplyExpression(plusAddExpressionContext.multiply_expression());
                    if (!left.ValueType.Equals(right.ValueType))
                    {
                        throw new NotImplementedException("Arithmetic of different types not implemented");
                    }

                    return CurrentBlock.AddInstruction(new ArithmeticInst
                    {
                        Id = ++_lastInstId,
                        LhsValue = left.Id,
                        RhsValue = right.Id,
                        OutputType = left.ValueType,
                        Op = ArithmeticInst.Operation.Add
                    });
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseMultiplyExpression(OxideParser.Multiply_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_multiply_expressionContext passMultiplyExpressionContext:
                    return ParseUnaryExpression(passMultiplyExpressionContext.unary_expression());
                case OxideParser.Div_multiply_expressionContext divMultiplyExpressionContext:
                    throw new NotImplementedException("Div expression");
                    break;
                case OxideParser.Mod_multiply_expressionContext modMultiplyExpressionContext:
                    throw new NotImplementedException("Mod expression");
                    break;
                case OxideParser.Mul_multiply_expressionContext mulMultiplyExpressionContext:
                    throw new NotImplementedException("Multiply expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseUnaryExpression(OxideParser.Unary_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_unary_expressionContext passUnaryExpressionContext:
                    return ParseBaseExpression(passUnaryExpressionContext.base_expression());
                case OxideParser.Box_unary_expressionContext boxUnaryExpressionContext:
                    throw new NotImplementedException("Box expression");
                    break;
                case OxideParser.Minus_unary_expressionContext minusUnaryExpressionContext:
                    throw new NotImplementedException("Minus expression");
                    break;
                case OxideParser.Not_unary_expressionContext notUnaryExpressionContext:
                    throw new NotImplementedException("Not expression");
                    break;
                case OxideParser.Ref_unary_expressionContext refUnaryExpressionContext:
                    throw new NotImplementedException("Ref expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseBaseExpression(OxideParser.Base_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Literal_base_expressionContext literalBaseExpressionContext:
                    return ParseLiteral(literalBaseExpressionContext.literal());
                case OxideParser.Method_call_base_expressionContext methodCallBaseExpressionContext:
                    throw new NotImplementedException("Method call expression");
                    break;
                case OxideParser.Access_base_expressionContext accessBaseExpressionContext:
                    return ParseAccessExpression(accessBaseExpressionContext);
                case OxideParser.Block_base_expressionContext blockBaseExpressionContext:
                    return ParseBlockExpression(blockBaseExpressionContext.block_expression());
                case OxideParser.Bracket_base_expressionContext bracketBaseExpressionContext:
                    return ParseExpression(bracketBaseExpressionContext.expression());
                case OxideParser.Function_call_base_expressionContext functionCallBaseExpressionContext:
                    return ParseFunctionCall(functionCallBaseExpressionContext);
                case OxideParser.Qualified_base_expressionContext qualifiedBaseExpressionContext:
                    return ParseQnExpression(qualifiedBaseExpressionContext);
                case OxideParser.Struct_base_expressionContext structBaseExpressionContext:
                    return ParseStructInitialiser(structBaseExpressionContext.struct_initialiser());
                case OxideParser.This_base_expressionContext thisBaseExpressionContext:
                    throw new NotImplementedException("This expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Instruction ParseAccessExpression(OxideParser.Access_base_expressionContext ctx)
        {
            var fieldName = ctx.name().GetText();

            var exp = ParseBaseExpression(ctx.base_expression());
            if (!exp.HasValue)
            {
                throw new Exception($"Cannot access {fieldName} on value with no type");
            }

            if (exp.ValueType.Source != TypeSource.Concrete)
            {
                throw new NotImplementedException("Non concrete types not implemented");
            }

            if (exp.ValueType.GenericParams != null && exp.ValueType.GenericParams.Length > 0)
            {
                throw new NotImplementedException("Generics");
            }

            var structDef = Lookup<Struct>(exp.ValueType.Name);
            if (structDef == null)
            {
                throw new Exception($"Failed to find {exp.ValueType.Name}");
            }

            var fieldDef = structDef.Fields.Single(x => x.Name == fieldName);

            return CurrentBlock.AddInstruction(new LoadFieldInst
            {
                Id = ++_lastInstId,
                TargetId = exp.Id,
                TargetField = fieldName,
                TargetType = structDef.Name,
                TargetFieldType = fieldDef.Type
            });
        }

        private Instruction ParseStructInitialiser(OxideParser.Struct_initialiserContext ctx)
        {
            if (ctx.type_generic_params() != null)
            {
                throw new NotImplementedException("Generic struct params");
            }

            var (structSource, structName) = ResolveQnWithGenerics(ctx.qualified_name().Parse());
            if (structSource != TypeSource.Concrete)
            {
                throw new NotImplementedException("Only concrete structs implemented");
            }

            var structDef = Lookup<Struct>(structName);

            var varDec = CurrentScope.DefineVariable(new VariableDeclaration
            {
                Id = ++_lastVariableId,
                Name = null,
                Type = new TypeRef
                {
                    Category = TypeCategory.Direct,
                    MutableRef = false,
                    GenericParams = ImmutableArray<TypeRef>.Empty,
                    Source = structSource,
                    Name = structName
                },
                Mutable = true
            });

            CurrentBlock.AddInstruction(new AllocStructInst
            {
                Id = ++_lastInstId,
                LocalId = varDec.Id,
                StructName = structName
            });

            var loadInst = CurrentBlock.AddInstruction(new LoadLocalInst
            {
                Id = ++_lastInstId,
                LocalId = varDec.Id,
                LocalType = varDec.Type
            });

            foreach (var fieldInit in ctx.field_initialiser())
            {
                switch (fieldInit)
                {
                    case OxideParser.Label_field_initialiserContext labelFieldInitialiserContext:
                    {
                        var fieldName = labelFieldInitialiserContext.label().Parse();

                        var fieldValue = ParseExpression(labelFieldInitialiserContext.expression());
                        if (!fieldValue.HasValue)
                        {
                            throw new Exception($"Cannot assign {fieldName} to no value");
                        }

                        var fieldDef = structDef.Fields.Single(x => x.Name == fieldName);
                        if (!fieldDef.Type.Equals(fieldValue.ValueType))
                        {
                            throw new Exception($"Incompatible types for {fieldName}");
                        }

                        CurrentBlock.AddInstruction(new StoreFieldInst
                        {
                            Id = ++_lastInstId,
                            ValueId = fieldValue.Id,
                            TargetId = loadInst.Id,
                            TargetField = fieldName,
                            TargetType = structDef.Name,
                        });
                        break;
                    }
                    case OxideParser.Var_field_initialiserContext varFieldInitialiserContext:
                    {
                        var fieldName = varFieldInitialiserContext.name().GetText();
                        var sourceDec = CurrentScope.ResolveVariable(fieldName);
                        if (sourceDec == null)
                        {
                            throw new Exception($"Unknown local {fieldName}");
                        }

                        var fieldDef = structDef.Fields.Single(x => x.Name == fieldName);
                        if (!fieldDef.Type.Equals(sourceDec.Type))
                        {
                            throw new Exception($"Incompatible types for {fieldName}");
                        }

                        var loadSource = CurrentBlock.AddInstruction(new LoadLocalInst
                        {
                            Id = ++_lastInstId,
                            LocalId = sourceDec.Id,
                            LocalType = sourceDec.Type
                        });

                        CurrentBlock.AddInstruction(new StoreFieldInst
                        {
                            Id = ++_lastInstId,
                            ValueId = loadSource.Id,
                            TargetId = loadInst.Id,
                            TargetField = fieldName,
                            TargetType = structDef.Name,
                        });
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(fieldInit));
                }

                // TODO: Check the user provided each field at least once and no more
            }

            return loadInst;
        }

        private Instruction ParseFunctionCall(OxideParser.Function_call_base_expressionContext ctx)
        {
            if (ctx.qn_generics != null)
            {
                throw new NotImplementedException("Generic params on QN in function call expressions not implemented");
            }

            var qns = ctx.qualified_name();
            if (qns.Length != 1)
            {
                throw new NotImplementedException("Generic param derived function calls not implemented");
            }

            if (ctx.method_generics != null)
            {
                throw new NotImplementedException("Generic method params in function call expressions not implemented");
            }

            var qn1 = qns[0].Parse();
            var resolvedName = ResolveQN(qn1);
            var functionDef = Lookup<Function>(resolvedName);
            if (functionDef == null)
            {
                throw new Exception($"Unable to find unit for {resolvedName}");
            }

            var argumentCtxs = ctx.arguments() != null
                ? ctx.arguments().argument()
                : Array.Empty<OxideParser.ArgumentContext>();

            if (argumentCtxs.Length != functionDef.Parameters.Count)
            {
                throw new Exception($"{functionDef.Name} takes {functionDef.Parameters.Count} parameters");
            }

            var argIds = new List<int>();
            for (var i = 0; i < argumentCtxs.Length; i++)
            {
                var argument = argumentCtxs[i];
                var parameter = functionDef.Parameters[i];

                if (argument.label() != null)
                {
                    throw new NotImplementedException("Argument labels are not implemented");
                }

                var inst = ParseExpression(argument.expression());
                if (!inst.HasValue)
                {
                    throw new Exception("Argument does not return a value");
                }

                if (!inst.ValueType.Equals(parameter.Type))
                {
                    throw new Exception($"Parameter type mismatch {inst.ValueType} != {parameter.Type}");
                }

                argIds.Add(inst.Id);
            }

            if (functionDef.ReturnType != null)
            {
                var resultDec = CurrentScope.DefineVariable(new VariableDeclaration
                {
                    Id = ++_lastVariableId,
                    Name = null,
                    Type = functionDef.ReturnType,
                    Mutable = false
                });

                CurrentBlock.AddInstruction(new StaticCallInst
                {
                    Id = ++_lastInstId,
                    TargetMethod = functionDef.Name,
                    Arguments = argIds.ToImmutableList(),
                    ResultLocal = resultDec.Id
                });

                return CurrentBlock.AddInstruction(new LoadLocalInst
                {
                    Id = ++_lastInstId,
                    LocalId = resultDec.Id,
                    LocalType = resultDec.Type,
                });
            }

            return CurrentBlock.AddInstruction(new StaticCallInst
            {
                Id = ++_lastInstId,
                TargetMethod = functionDef.Name,
                Arguments = argIds.ToImmutableList(),
            });
        }

        private Instruction ParseQnExpression(OxideParser.Qualified_base_expressionContext ctx)
        {
            var varDec = ResolveQn(ctx.qualified_name(), ctx.type_generic_params());
            return CurrentBlock.AddInstruction(new LoadLocalInst
            {
                Id = ++_lastInstId,
                LocalId = varDec.Id,
                LocalType = varDec.Type
            });
        }

        private VariableDeclaration ResolveQn(OxideParser.Qualified_nameContext[] qns,
            OxideParser.Type_generic_paramsContext typeGenericParams)
        {
            if (typeGenericParams != null)
            {
                throw new NotImplementedException("Generic params on QN expressions not implemented");
            }

            if (qns.Length != 1)
            {
                throw new NotImplementedException("Generic param derived QN not implemented");
            }

            var qn1 = qns[0].Parse();
            if (qn1.Parts.Length > 1)
            {
                throw new NotImplementedException("Non-local QN expressions not implemented");
            }

            var varName = qn1.Parts[0];
            var varDec = CurrentScope.ResolveVariable(varName);
            if (varDec == null)
            {
                throw new Exception($"Unknown variable {varName}");
            }

            return varDec;
        }

        private Instruction ParseLiteral(OxideParser.LiteralContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Binary_literalContext binaryLiteralContext:
                    throw new NotImplementedException("Binary literal");
                    break;
                case OxideParser.Hex_literalContext hexLiteralContext:
                    throw new NotImplementedException("Hex literal");
                    break;
                case OxideParser.Int_literalContext intLiteralContext:
                {
                    var val = int.Parse(intLiteralContext.GetText());
                    return CurrentBlock.AddInstruction(new ConstInst
                    {
                        Id = ++_lastInstId,
                        ConstType = PrimitiveKind.I32,
                        Value = val
                    });
                }
                case OxideParser.Outer_bool_literalContext outerBoolLiteralContext:
                    switch (outerBoolLiteralContext.boolean_literal())
                    {
                        case OxideParser.True_boolean_literalContext:
                            return CurrentBlock.AddInstruction(new ConstInst
                            {
                                Id = ++_lastInstId,
                                ConstType = PrimitiveKind.Bool,
                                Value = true
                            });
                        case OxideParser.False_boolean_literalContext:
                            return CurrentBlock.AddInstruction(new ConstInst
                            {
                                Id = ++_lastInstId,
                                ConstType = PrimitiveKind.Bool,
                                Value = false
                            });
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                case OxideParser.String_literalContext stringLiteralContext:
                    throw new NotImplementedException("String literal");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private Scope PushScope()
        {
            var scope = new Scope
            {
                Id = ++_lastScopeId,
                ParentScope = CurrentScope
            };

            CurrentScope = scope;
            Scopes.Add(scope);

            return scope;
        }

        private void RestoreScope(Scope target)
        {
            CurrentScope = target;
        }

        private Block NewBlock(Scope scope)
        {
            var block = new Block
            {
                Id = ++_lastBlockId,
                Scope = scope
            };
            Blocks.Add(block.Id, block);
            return block;
        }

        private void MakeCurrent(Block block)
        {
            RestoreScope(block.Scope);
            _currentBlockId = block.Id;
        }

        private TypeRef ParseType(OxideParser.TypeContext ctx)
        {
            return _fileParser.ParseType(ctx, _function.GenericParams);
        }

        private QualifiedName ResolveQN(QualifiedName qn)
        {
            return _fileParser.ResolveQN(qn);
        }

        private (TypeSource source, QualifiedName qn) ResolveQnWithGenerics(QualifiedName qn)
        {
            return _fileParser.ResolveQnWithGenerics(qn, _function.GenericParams);
        }

        private OxObj Lookup(QualifiedName qn)
        {
            return _unit.Lookup(qn) ?? _store.Lookup(qn);
        }

        private T Lookup<T>(QualifiedName qn) where T : OxObj
        {
            return _unit.Lookup<T>(qn) ?? _store.Lookup<T>(qn);
        }
    }
}