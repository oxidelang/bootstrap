using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.Parser;

namespace Oxide.Compiler.Frontend
{
    public class BodyParser
    {
        private readonly FileParser _fileParser;
        private readonly ImmutableList<string> _genericTypes;

        private readonly List<Scope> _scopes;
        private int _lastScopeId;
        private Scope CurrentScope => _scopes[^1];

        private int _lastVariableId;

        private readonly Dictionary<int, Block> _blocks;
        private int _lastBlockId;
        private int _currentBlockId;
        private Block CurrentBlock => _blocks[_currentBlockId];

        private int _lastInstId;

        public BodyParser(FileParser fileParser, ImmutableList<string> genericTypes)
        {
            _genericTypes = genericTypes;
            _fileParser = fileParser;
            _scopes = new List<Scope>();
            _blocks = new Dictionary<int, Block>();
            _lastScopeId = 0;
            _lastBlockId = 0;
            _lastVariableId = 0;
        }

        public void ParseBody(OxideParser.BlockContext ctx)
        {
            var scope = PushScope();

            var block = NewBlock(CurrentScope);
            MakeCurrent(block);

            // TODO: Init context
            ParseBlock(ctx);
        }

        private void ParseBlock(OxideParser.BlockContext ctx)
        {
            if (ctx.statement() != null)
            {
                foreach (var statement in ctx.statement())
                {
                    ParseStatement(statement);
                }
            }

            if (ctx.expression() != null)
            {
                ParseExpression(ctx.expression());
            }
        }

        private void ParseStatement(OxideParser.StatementContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Empty_statementContext:
                    // Ignore empty statement
                    break;
                case OxideParser.Block_expression_statementContext blockExpressionStatementContext:
                    throw new NotImplementedException("Block expression statement");
                    break;
                case OxideParser.Expression_statementContext expressionStatementContext:
                    throw new NotImplementedException("Expression statement");
                    break;
                case OxideParser.Variable_statement_topContext variableStatementTopContext:
                {
                    ParseVariableStatement(variableStatementTopContext.variable_statement());
                    throw new NotImplementedException("Variable statement");
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseVariableStatement(OxideParser.Variable_statementContext ctx)
        {
            var name = ctx.name().GetText();
            var type = ctx.type() != null ? ParseType(ctx.type()) : null;
            var mutable = ctx.MUT() != null;

            if (ctx.expression() != null)
            {
                ParseExpression(ctx.expression());
            }
            else if (ctx.type() == null)
            {
                throw new Exception("Variable declarations without an expression must have a type");
            }

            if (type == null)
            {
                throw new Exception($"Unable to resolve type for variable {name}");
            }

            CurrentScope.DefineVariable(new VariableDeclaration
            {
                Id = ++_lastVariableId,
                Name = name,
                Type = type,
                Mutable = mutable
            });
        }

        private void ParseExpression(OxideParser.ExpressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_expressionContext passExpressionContext:
                    ParseOrExpression(passExpressionContext.or_expression());
                    break;
                case OxideParser.Assign_expression_topContext assignExpressionTopContext:
                    throw new NotImplementedException("Assign expression");
                    break;
                case OxideParser.Return_expressionContext returnExpressionContext:
                    throw new NotImplementedException("Return expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseOrExpression(OxideParser.Or_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_or_expressionContext passOrExpressionContext:
                    ParseAndExpression(passOrExpressionContext.and_expression());
                    break;
                case OxideParser.Op_or_expressionContext opOrExpressionContext:
                    throw new NotImplementedException("Or expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseAndExpression(OxideParser.And_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_and_expressionContext passAndExpressionContext:
                    ParseIncOrExpression(passAndExpressionContext.inc_or_expression());
                    break;
                case OxideParser.Op_and_expressionContext opAndExpressionContext:
                    throw new NotImplementedException("And expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseIncOrExpression(OxideParser.Inc_or_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_inc_or_expressionContext passIncOrExpressionContext:
                    ParseExOrExpression(passIncOrExpressionContext.ex_or_expression());
                    break;
                case OxideParser.Op_inc_or_expressionContext opIncOrExpressionContext:
                    throw new NotImplementedException("Inc or expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseExOrExpression(OxideParser.Ex_or_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_ex_or_expressionContext passExOrExpressionContext:
                    ParseBitAndExpression(passExOrExpressionContext.bit_and_expression());
                    break;
                case OxideParser.Op_ex_or_expressionContext opExOrExpressionContext:
                    throw new NotImplementedException("Ex or expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseBitAndExpression(OxideParser.Bit_and_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_bit_and_expressionContext passBitAndExpressionContext:
                    ParseEqualExpression(passBitAndExpressionContext.equal_expression());
                    break;
                case OxideParser.Op_bit_and_expressionContext opBitAndExpressionContext:
                    throw new NotImplementedException("Bit and expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseEqualExpression(OxideParser.Equal_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_equal_expressionContext passEqualExpressionContext:
                    ParseComparisonExpression(passEqualExpressionContext.comparison_expression());
                    break;
                case OxideParser.Eq_equal_expressionContext eqEqualExpressionContext:
                    throw new NotImplementedException("Equal expression");
                    break;
                case OxideParser.Ne_equal_expressionContext neEqualExpressionContext:
                    throw new NotImplementedException("Not equal expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseComparisonExpression(OxideParser.Comparison_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_comparison_expressionContext passComparisonExpressionContext:
                    ParseCastExpression(passComparisonExpressionContext.cast_expression());
                    break;
                case OxideParser.Geq_comparison_expressionContext geqComparisonExpressionContext:
                    throw new NotImplementedException("GEQ expression");
                    break;
                case OxideParser.Gt_comparison_expressionContext gtComparisonExpressionContext:
                    throw new NotImplementedException("GT expression");
                    break;
                case OxideParser.Leq_comparison_expressionContext leqComparisonExpressionContext:
                    throw new NotImplementedException("LEQ expression");
                    break;
                case OxideParser.Lt_comparison_expressionContext ltComparisonExpressionContext:
                    throw new NotImplementedException("LT expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseCastExpression(OxideParser.Cast_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_cast_expressionContext passCastExpressionContext:
                    ParseShiftExpression(passCastExpressionContext.shift_expression());
                    break;
                case OxideParser.Op_cast_expressionContext opCastExpressionContext:
                    throw new NotImplementedException("cast expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseShiftExpression(OxideParser.Shift_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_shift_expressionContext passShiftExpressionContext:
                    ParseAddExpression(passShiftExpressionContext.add_expression());
                    break;
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

        private void ParseAddExpression(OxideParser.Add_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_add_expressionContext passAddExpressionContext:
                    ParseMultiplyExpression(passAddExpressionContext.multiply_expression());
                    break;
                case OxideParser.Minus_add_expressionContext minusAddExpressionContext:
                    throw new NotImplementedException("Minus expression");
                    break;
                case OxideParser.Plus_add_expressionContext plusAddExpressionContext:
                    throw new NotImplementedException("Add expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseMultiplyExpression(OxideParser.Multiply_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_multiply_expressionContext passMultiplyExpressionContext:
                    ParseUnaryExpression(passMultiplyExpressionContext.unary_expression());
                    break;
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

        private void ParseUnaryExpression(OxideParser.Unary_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Pass_unary_expressionContext passUnaryExpressionContext:
                    ParseBaseExpression(passUnaryExpressionContext.base_expression());
                    break;
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

        private void ParseBaseExpression(OxideParser.Base_expressionContext ctx)
        {
            switch (ctx)
            {
                case OxideParser.Literal_base_expressionContext literalBaseExpressionContext:
                    ParseLiteral(literalBaseExpressionContext.literal());
                    break;
                case OxideParser.Access_base_expressionContext accessBaseExpressionContext:
                    throw new NotImplementedException("Access expression");
                    break;
                case OxideParser.Block_base_expressionContext blockBaseExpressionContext:
                    throw new NotImplementedException("Block expression");
                    break;
                case OxideParser.Bracket_base_expressionContext bracketBaseExpressionContext:
                    throw new NotImplementedException("Bracket expression");
                    break;
                case OxideParser.Invoke_base_expressionContext invokeBaseExpressionContext:
                    throw new NotImplementedException("Invoke expression");
                    break;
                case OxideParser.Qualified_base_expressionContext qualifiedBaseExpressionContext:
                    throw new NotImplementedException("Qualified name expression");
                    break;
                case OxideParser.Struct_base_expressionContext structBaseExpressionContext:
                    throw new NotImplementedException("Struct expression");
                    break;
                case OxideParser.This_base_expressionContext thisBaseExpressionContext:
                    throw new NotImplementedException("This expression");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        private void ParseLiteral(OxideParser.LiteralContext ctx)
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
                    CurrentBlock.AddInstruction(new ConstInst
                    {
                        Id = ++_lastInstId,
                        Type = ConstInst.PrimitiveType.I32,
                        Value = val
                    });
                    break;
                }
                case OxideParser.Outer_bool_literalContext outerBoolLiteralContext:
                    switch (outerBoolLiteralContext.boolean_literal())
                    {
                        case OxideParser.True_boolean_literalContext:
                            break;
                        case OxideParser.False_boolean_literalContext:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    throw new NotImplementedException("Bool literal");
                    break;
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
                ParentScope = _scopes.Count > 0 ? CurrentScope : null
            };

            _scopes.Add(scope);

            return scope;
        }

        private Block NewBlock(Scope scope)
        {
            var block = new Block
            {
                Id = ++_lastBlockId,
                Scope = scope
            };
            _blocks.Add(block.Id, block);
            return block;
        }

        private void MakeCurrent(Block block)
        {
            _currentBlockId = block.Id;
        }

        private TypeDef ParseType(OxideParser.TypeContext ctx)
        {
            return _fileParser.ParseType(ctx, _genericTypes);
        }
    }
}