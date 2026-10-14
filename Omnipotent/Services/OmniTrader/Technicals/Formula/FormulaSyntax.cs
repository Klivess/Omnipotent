using System.Globalization;
using System.Text;

namespace Omnipotent.Services.OmniTrader.Technicals.Formula
{
    public sealed class FormulaException : Exception
    {
        public int Position { get; }
        public int Length { get; }
        public FormulaException(string message, int position, int length = 1) : base(message)
        {
            Position = position;
            Length = Math.Max(1, length);
        }
    }

    internal enum TokenKind { Number, String, Ident, Op, LParen, RParen, LBracket, RBracket, Comma, Dot, Question, Colon, Assign, End }

    internal readonly record struct Token(TokenKind Kind, string Text, int Pos, double Number = 0);

    internal static class FormulaLexer
    {
        private static readonly string[] TwoCharOps = { ">=", "<=", "==", "!=", "&&", "||" };

        public static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                int start = i;
                if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                {
                    while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                    if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
                    {
                        i++;
                        if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
                        while (i < text.Length && char.IsDigit(text[i])) i++;
                    }
                    string num = text[start..i];
                    if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        throw new FormulaException($"Bad number '{num}'", start, num.Length);
                    tokens.Add(new Token(TokenKind.Number, num, start, value));
                    continue;
                }
                if (char.IsLetter(c) || c == '_' || c == '$' || c == '@')
                {
                    i++;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    string ident = text[start..i];
                    if (ident.Length > 1 && (ident[0] == '$' || ident[0] == '@')) ident = ident[1..];
                    tokens.Add(new Token(TokenKind.Ident, ident, start));
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    var sb = new StringBuilder();
                    while (i < text.Length && text[i] != quote) sb.Append(text[i++]);
                    if (i >= text.Length) throw new FormulaException("Unterminated string", start, i - start);
                    i++;
                    tokens.Add(new Token(TokenKind.String, sb.ToString(), start));
                    continue;
                }
                if (i + 1 < text.Length && TwoCharOps.Contains(text.Substring(i, 2)))
                {
                    tokens.Add(new Token(TokenKind.Op, text.Substring(i, 2), start));
                    i += 2;
                    continue;
                }
                switch (c)
                {
                    case '(': tokens.Add(new Token(TokenKind.LParen, "(", start)); break;
                    case ')': tokens.Add(new Token(TokenKind.RParen, ")", start)); break;
                    case '[': tokens.Add(new Token(TokenKind.LBracket, "[", start)); break;
                    case ']': tokens.Add(new Token(TokenKind.RBracket, "]", start)); break;
                    case ',': tokens.Add(new Token(TokenKind.Comma, ",", start)); break;
                    case '.': tokens.Add(new Token(TokenKind.Dot, ".", start)); break;
                    case '?': tokens.Add(new Token(TokenKind.Question, "?", start)); break;
                    case ':': tokens.Add(new Token(TokenKind.Colon, ":", start)); break;
                    case '=': tokens.Add(new Token(TokenKind.Assign, "=", start)); break;
                    case '+': case '-': case '*': case '/': case '%': case '^': case '>': case '<': case '!':
                        tokens.Add(new Token(TokenKind.Op, c.ToString(), start)); break;
                    default:
                        throw new FormulaException($"Unexpected character '{c}'", start);
                }
                i++;
            }
            tokens.Add(new Token(TokenKind.End, "", text.Length));
            return tokens;
        }
    }

    // ── AST ──────────────────────────────────────────────────────────────────────

    public abstract record Node(int Pos, int Len);
    public sealed record NumberNode(double Value, int Pos, int Len) : Node(Pos, Len);
    public sealed record StringNode(string Value, int Pos, int Len) : Node(Pos, Len);
    public sealed record IdentNode(string Name, int Pos, int Len) : Node(Pos, Len);
    public sealed record CallArg(string? Name, Node Value);
    public sealed record CallNode(string Name, IReadOnlyList<CallArg> Args, int Pos, int Len) : Node(Pos, Len);
    public sealed record MemberNode(Node Target, string Member, int Pos, int Len) : Node(Pos, Len);
    public sealed record IndexNode(Node Target, Node Offset, int Pos, int Len) : Node(Pos, Len);
    public sealed record UnaryNode(string Op, Node Operand, int Pos, int Len) : Node(Pos, Len);
    public sealed record BinaryNode(string Op, Node Left, Node Right, int Pos, int Len) : Node(Pos, Len);
    public sealed record TernaryNode(Node Condition, Node WhenTrue, Node WhenFalse, int Pos, int Len) : Node(Pos, Len);

    /// <summary>Pratt parser. Precedence low→high: ?:  or  and  not  comparison  + −  * / %  ^  unary −  postfix.</summary>
    internal sealed class FormulaParser
    {
        private readonly List<Token> tokens;
        private int index;

        private FormulaParser(List<Token> tokens) { this.tokens = tokens; }

        public static Node Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormulaException("Empty formula", 0);
            var parser = new FormulaParser(FormulaLexer.Tokenize(text));
            var node = parser.ParseTernary();
            if (parser.Peek.Kind != TokenKind.End)
                throw new FormulaException($"Unexpected '{parser.Peek.Text}'", parser.Peek.Pos, parser.Peek.Text.Length);
            return node;
        }

        private Token Peek => tokens[index];
        private Token Next() => tokens[index++];

        private bool IsOp(params string[] ops) => Peek.Kind == TokenKind.Op && ops.Contains(Peek.Text)
            || Peek.Kind == TokenKind.Ident && ops.Contains(Peek.Text.ToLowerInvariant());

        private Node ParseTernary()
        {
            var cond = ParseOr();
            if (Peek.Kind != TokenKind.Question) return cond;
            Next();
            var a = ParseTernary();
            if (Peek.Kind != TokenKind.Colon) throw new FormulaException("Expected ':' in conditional", Peek.Pos);
            Next();
            var b = ParseTernary();
            return new TernaryNode(cond, a, b, cond.Pos, b.Pos + b.Len - cond.Pos);
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (IsOp("||", "or"))
            {
                Next();
                var right = ParseAnd();
                left = new BinaryNode("or", left, right, left.Pos, right.Pos + right.Len - left.Pos);
            }
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseNot();
            while (IsOp("&&", "and"))
            {
                Next();
                var right = ParseNot();
                left = new BinaryNode("and", left, right, left.Pos, right.Pos + right.Len - left.Pos);
            }
            return left;
        }

        private Node ParseNot()
        {
            if (IsOp("!", "not"))
            {
                var t = Next();
                var operand = ParseNot();
                return new UnaryNode("not", operand, t.Pos, operand.Pos + operand.Len - t.Pos);
            }
            return ParseComparison();
        }

        private Node ParseComparison()
        {
            var left = ParseAdditive();
            while (Peek.Kind == TokenKind.Op && Peek.Text is ">" or ">=" or "<" or "<=" or "==" or "!=")
            {
                string op = Next().Text;
                var right = ParseAdditive();
                left = new BinaryNode(op, left, right, left.Pos, right.Pos + right.Len - left.Pos);
            }
            return left;
        }

        private Node ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (Peek.Kind == TokenKind.Op && Peek.Text is "+" or "-")
            {
                string op = Next().Text;
                var right = ParseMultiplicative();
                left = new BinaryNode(op, left, right, left.Pos, right.Pos + right.Len - left.Pos);
            }
            return left;
        }

        private Node ParseMultiplicative()
        {
            var left = ParsePower();
            while (Peek.Kind == TokenKind.Op && Peek.Text is "*" or "/" or "%")
            {
                string op = Next().Text;
                var right = ParsePower();
                left = new BinaryNode(op, left, right, left.Pos, right.Pos + right.Len - left.Pos);
            }
            return left;
        }

        private Node ParsePower()
        {
            var left = ParseUnary();
            if (Peek.Kind == TokenKind.Op && Peek.Text == "^")
            {
                Next();
                var right = ParsePower(); // right-associative
                return new BinaryNode("^", left, right, left.Pos, right.Pos + right.Len - left.Pos);
            }
            return left;
        }

        private Node ParseUnary()
        {
            if (Peek.Kind == TokenKind.Op && Peek.Text is "-" or "+")
            {
                var t = Next();
                var operand = ParseUnary();
                return t.Text == "-" ? new UnaryNode("neg", operand, t.Pos, operand.Pos + operand.Len - t.Pos) : operand;
            }
            return ParsePostfix(ParsePrimary());
        }

        private Node ParsePostfix(Node node)
        {
            while (true)
            {
                if (Peek.Kind == TokenKind.LBracket)
                {
                    Next();
                    var offset = ParseTernary();
                    if (Peek.Kind != TokenKind.RBracket) throw new FormulaException("Expected ']'", Peek.Pos);
                    var end = Next();
                    node = new IndexNode(node, offset, node.Pos, end.Pos + 1 - node.Pos);
                }
                else if (Peek.Kind == TokenKind.Dot)
                {
                    Next();
                    if (Peek.Kind != TokenKind.Ident) throw new FormulaException("Expected output name after '.'", Peek.Pos);
                    var member = Next();
                    node = new MemberNode(node, member.Text, node.Pos, member.Pos + member.Text.Length - node.Pos);
                }
                else return node;
            }
        }

        private Node ParsePrimary()
        {
            var t = Peek;
            switch (t.Kind)
            {
                case TokenKind.Number:
                    Next();
                    return new NumberNode(t.Number, t.Pos, t.Text.Length);
                case TokenKind.String:
                    Next();
                    return new StringNode(t.Text, t.Pos, t.Text.Length + 2);
                case TokenKind.LParen:
                {
                    Next();
                    var inner = ParseTernary();
                    if (Peek.Kind != TokenKind.RParen) throw new FormulaException("Expected ')'", Peek.Pos);
                    Next();
                    return inner;
                }
                case TokenKind.Ident:
                {
                    Next();
                    if (Peek.Kind != TokenKind.LParen) return new IdentNode(t.Text, t.Pos, t.Text.Length);
                    Next();
                    var args = new List<CallArg>();
                    if (Peek.Kind != TokenKind.RParen)
                    {
                        while (true)
                        {
                            string? name = null;
                            if (Peek.Kind == TokenKind.Ident && tokens[index + 1].Kind == TokenKind.Assign)
                            {
                                name = Next().Text;
                                Next();
                            }
                            args.Add(new CallArg(name, ParseTernary()));
                            if (Peek.Kind == TokenKind.Comma) { Next(); continue; }
                            break;
                        }
                    }
                    if (Peek.Kind != TokenKind.RParen) throw new FormulaException($"Expected ')' to close {t.Text}(", Peek.Pos);
                    var close = Next();
                    return new CallNode(t.Text.ToLowerInvariant(), args, t.Pos, close.Pos + 1 - t.Pos);
                }
                case TokenKind.End:
                    throw new FormulaException("Formula ends early", t.Pos);
                default:
                    throw new FormulaException($"Unexpected '{t.Text}'", t.Pos, t.Text.Length);
            }
        }
    }
}
