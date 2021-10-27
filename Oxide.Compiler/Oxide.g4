grammar Oxide;

WS:                 [ \t\r\n\u000C]+ -> channel(HIDDEN);
COMMENT:            '/*' .*? '*/'    -> channel(HIDDEN);
LINE_COMMENT:       '//' ~[\r\n]*    -> channel(HIDDEN);

PACKAGE:    'package';
IMPORT:     'import';
PUBLIC:     'public';
PRIVATE:    'private';
MUT:        'mut';
STRUCT:     'struct';
ENUM:       'enum';
VARIANT:    'variant';
IMPL:       'impl';
IFACE:      'interface';
WHERE:      'where';
FOR:        'for';
VAR:        'var';
FUNC:       'func';
UNSAFE:     'unsafe';
BOX:        'box';
ALIAS:      'alias';

REF:        'ref';
WEAK:       'weak';
DERIVED:    '~';

THIS_FIELD: 'this';

RETURN: 'return';
IF:     'if';
ELSE:   'else';

LBRACK: '(';
RBRACK: ')';
LARROW: '<';
RARROW: '>';
LBRACE: '{';
RBRACE: '}';
COLON:  ':';
DCOLON: '::';
SEMI:   ';';
ARROW:  '->';
AMP:    '&';
PERIOD: '.';
COMMA:  ',';

PLUS:       '+';
MINUS:      '-';
EQUAL:      '=';
NOTEQ:      '!=';
EQUALTO:    '==';
STAR:       '*';
DIV:        '/';
MOD:        '%';
NOT:        '!';
AS:         'as';

OR_OP:      '||';
AND_OP:     '&&';
INC_OR_OP:  '|';
EX_OR_OP:   '^';
LEQ_OP:     '<=';
GEQ_OP:     '>=';

IDENTIFIER:     Letter LetterOrDigit*;
INT_NUMBER:     '-'? Digits;
HEX_NUMBER:     '0' [xX] HexDigit ((HexDigit | '_')* HexDigit)? [lL]?;
BINARY_NUMBER:  '0' [bB] BinaryDigit ((BinaryDigit | '_')* BinaryDigit)? [lL]?;
CHAR_LITERAL:   '\'' (~['\\\r\n] | EscapeSequence) '\'';
STRING_LITERAL: '"' (~["\\\r\n] | EscapeSequence)* '"';
TRUE:           'true';
FALSE:          'false';

fragment EscapeSequence
    : '\\' [btnfr"'\\]
    | '\\' 'u'+ HexDigit HexDigit HexDigit HexDigit
    ;

fragment HexDigits
    : HexDigit ((HexDigit | '_')* HexDigit)?
    ;
fragment HexDigit
    : [0-9a-fA-F]
    ;

fragment BinaryDigit
    : [01]
    ;

fragment Digits
    : Digit ((Digit | '_')* Digit)?
    ;

fragment Digit
    : [0-9]
    ;

fragment LetterOrDigit
    : Letter
    | [0-9]
    ;

fragment Letter
    : [a-zA-Z$_]
    ;

compilation_unit
    : package import_stmt* top_level* EOF
    ;

package
    : PACKAGE qualified_name SEMI
    ;

qualified_name
    : qualified_name_part #relative_qualified_name
    | DCOLON qualified_name_part #absolute_qualified_name
    ;

qualified_name_part
    : IDENTIFIER
    | qualified_name_part DCOLON IDENTIFIER 
    ;

top_level
    : struct_def #struct_top_level
    | variant_def #variant_top_level
    | impl_stmt #impl_top_level
    | func_def #func_top_level
    | iface_def #iface_top_level
    | alias_def #alias_top_level
    ;

import_stmt
    : IMPORT qualified_name (AS name)? SEMI
    ;

struct_def
    : visibility? STRUCT name generic_def? LBRACE field_def* RBRACE
    ;

generic_def
    : LARROW name (COMMA name)* RARROW
    ;

field_def
    : visibility? MUT? name COLON type COMMA
    ;

variant_def
    : visibility? VARIANT name generic_def? LBRACE variant_item_def* RBRACE
    ;

variant_item_def
    : visibility? name COMMA #simple_variant_item_def
    | visibility? name LBRACE field_def* RBRACE COMMA #struct_variant_item_def
    | visibility? name tuple_def COMMA #tuple_variant_item_def
    ;

tuple_def
    : LBRACK tuple_item_def (COMMA tuple_item_def)* RBRACK
    ;

tuple_item_def
    : MUT? type
    ;

impl_stmt
    : IMPL (qualified_name FOR)? type where? (SEMI | impl_body)
    ;

impl_body
    : LBRACE func_def* RBRACE
    ;

where
    : WHERE where_clause (COMMA where_clause)*
    ;

where_clause
    : name COLON type (PLUS type)*
    ;

iface_def
    : visibility? IFACE name generic_def? LBRACE func_def* RBRACE
    ;

alias_def
    : visibility? ALIAS name generic_def? EQUAL type SEMI
    ;

func_def
    : visibility? FUNC name generic_def? LBRACK (parameter (COMMA parameter)*)? RBRACK (COLON type)? func_body
    ;

parameter
    : name COLON type #standard_parameter
    | type_flags THIS_FIELD #this_parameter
    ;

func_body
    : block #block_func_body
    | SEMI #empty_func_body
    ;

block
    : LBRACE statement* expression?  RBRACE
    ;

statement
    : SEMI #empty_statement
    | expression SEMI #expression_statement
    | block_expression #block_expression_statement
    | variable_statement #variable_statement_top
    ;

expression
    : or_expression #pass_expression
    | RETURN or_expression #return_expression
    | base_expression EQUAL or_expression #assign_expression_top
    ;

or_expression
	: and_expression #pass_or_expression
	| or_expression OR_OP and_expression #op_or_expression
	;

and_expression
	: inc_or_expression #pass_and_expression
	| and_expression AND_OP inc_or_expression #op_and_expression
	;

inc_or_expression
	: ex_or_expression #pass_inc_or_expression
	| inc_or_expression INC_OR_OP ex_or_expression #op_inc_or_expression 
	;

ex_or_expression
	: bit_and_expression #pass_ex_or_expression
	| ex_or_expression EX_OR_OP bit_and_expression #op_ex_or_expression 
	;

bit_and_expression
	: equal_expression #pass_bit_and_expression
	| bit_and_expression AMP equal_expression #op_bit_and_expression
	;

equal_expression
	: comparison_expression #pass_equal_expression
	| equal_expression EQUALTO comparison_expression #eq_equal_expression
	| equal_expression NOTEQ comparison_expression #ne_equal_expression
	;

comparison_expression
	: cast_expression #pass_comparison_expression
	| comparison_expression LARROW cast_expression #lt_comparison_expression
	| comparison_expression RARROW cast_expression #gt_comparison_expression
	| comparison_expression LEQ_OP cast_expression #leq_comparison_expression
	| comparison_expression GEQ_OP cast_expression #geq_comparison_expression
	;

cast_expression
    : shift_expression #pass_cast_expression
    | cast_expression AS type #op_cast_expression
    ;

shift_expression
	: add_expression #pass_shift_expression
	| shift_expression left_shift add_expression #left_shift_expression
	| shift_expression right_shift add_expression #right_shift_expression
	;

add_expression
	: multiply_expression #pass_add_expression
	| add_expression PLUS multiply_expression #plus_add_expression
	| add_expression MINUS multiply_expression #minus_add_expression
	;

multiply_expression
	: unary_expression #pass_multiply_expression
	| multiply_expression STAR unary_expression #mul_multiply_expression
	| multiply_expression DIV unary_expression #div_multiply_expression
	| multiply_expression MOD unary_expression #mod_multiply_expression
	;

unary_expression
	: base_expression #pass_unary_expression
	| MINUS unary_expression #minus_unary_expression
	| NOT unary_expression #not_unary_expression
	| AMP unary_expression #ref_unary_expression
//	| LBRACK type RBRACK unary_expression #cast_unary_expression
	| BOX unary_expression #box_unary_expression
	;

base_expression
    : DERIVED? LBRACK expression RBRACK #bracket_base_expression
    | literal #literal_base_expression
    | DERIVED? THIS_FIELD #this_base_expression
    | DERIVED? qualified_name (type_generic_params DCOLON qualified_name)? #qualified_base_expression
    | struct_initialiser #struct_base_expression
    | block_expression #block_base_expression
    | base_expression PERIOD name #access_base_expression
    | base_expression type_generic_params? LBRACK arguments? RBRACK #invoke_base_expression
    ;

block_expression
    : UNSAFE? block #block_block_expression
    | if_expression #if_block_expression
    ;

if_expression
    : IF expression block (ELSE (block | if_expression))? #simple_if_expression
    | IF VAR expression block (ELSE (block | if_expression))? #let_if_expression
    ;

arguments
	: argument (COMMA argument)* COMMA?
	;

argument
	: label? expression
	;

label
    : name COLON
    ;

struct_initialiser
    : name type_generic_params? LBRACE field_initialiser* RBRACE
    ;

field_initialiser
    : label expression COMMA
    | name COMMA
    ;

variable_statement
    : VAR MUT? name (COLON type)? (EQUAL expression)? SEMI
    ;

name
    : IDENTIFIER
    ;

type
    : type_flags qualified_name type_generic_params?
    ;

type_generic_params
    : LARROW type (COMMA type)* RARROW
    ;

type_flags
    : UNSAFE? (REF | DERIVED | WEAK) #ref_type_flags
    | UNSAFE? AMP MUT? #local_type_flags
    | UNSAFE? STAR MUT? #ptr_type_flags
    | UNSAFE? #direct_type_flags
    ;

visibility
    : PUBLIC #public_visibility
    | PRIVATE #private_visibility
    ;

literal
    : boolean_literal #outer_bool_literal
    | INT_NUMBER #int_literal
    | HEX_NUMBER #hex_literal
    | BINARY_NUMBER #binary_literal
    | STRING_LITERAL #string_literal
    ;

boolean_literal
	: TRUE #true_boolean_literal
	| FALSE #false_boolean_literal
	;

// Ensure we don't conflict with generics
left_shift
	: f=LARROW s=LARROW {$f.index + 1 == $s.index}?
	;

right_shift
	: f=RARROW s=RARROW {$f.index + 1 == $s.index}?
	;