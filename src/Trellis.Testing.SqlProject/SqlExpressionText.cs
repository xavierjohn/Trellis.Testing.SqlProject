namespace Trellis.Testing.SqlProject;

using Microsoft.SqlServer.TransactSql.ScriptDom;

internal static class SqlExpressionText
{
    /// <summary>
    /// The expression as written, minus redundant outer parentheses; <c>(none)</c> when there is none.
    /// </summary>
    internal static string Display(string? expression)
    {
        if (expression is null)
            return "(none)";

        var parsed = Parse(expression);
        return parsed is null ? expression.Trim() : TokenText(WithoutOuterParentheses(parsed)).Trim();
    }

    /// <summary>
    /// Whether two expressions mean the same thing, comparing what SQL Server compares.
    /// </summary>
    /// <remarks>
    /// Case folding the text would be wrong in both directions. Keywords, built-in function names and
    /// built-in types are case-insensitive whatever the collation, but the names of objects such as
    /// sequences follow the project's collation, and string literals are always exact. The expression is
    /// therefore parsed, and each part compared under its own rule. Quoting and redundant outer
    /// parentheses are not part of the meaning.
    /// </remarks>
    internal static bool Equivalent(string? left, string? right, IEqualityComparer<string> identifiers)
    {
        if (left is null || right is null)
            return left is null && right is null;

        var leftAtoms = Atoms(left);
        var rightAtoms = Atoms(right);
        if (leftAtoms is null || rightAtoms is null)
            return string.Equals(Display(left), Display(right), StringComparison.Ordinal);

        return leftAtoms.Count == rightAtoms.Count
            && leftAtoms.Zip(rightAtoms, (l, r) => l.Matches(r, identifiers)).All(matches => matches);
    }

    // Returns null when the text does not parse as a scalar expression, so callers can fall back to
    // an exact comparison: a difference reported wrongly is visible, one hidden is not.
    private static ScalarExpression? Parse(string expression)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.ParseExpression(new StringReader(expression), out var errors);
        return errors.Count > 0 ? null : fragment;
    }

    // DEFAULT 5 and DEFAULT (5) are the same default. Unwrapping in the syntax tree, not the text, means
    // a parenthesis inside a comment or literal can never be mistaken for the closing one, and
    // (a) + (b) is correctly left alone because it is not one parenthesized expression.
    private static ScalarExpression WithoutOuterParentheses(ScalarExpression expression)
    {
        while (expression is ParenthesisExpression parenthesis)
            expression = parenthesis.Expression;

        return expression;
    }

    private static string TokenText(ScalarExpression expression) =>
        string.Concat(expression.ScriptTokenStream
            .Skip(expression.FirstTokenIndex)
            .Take(expression.LastTokenIndex - expression.FirstTokenIndex + 1)
            .Select(token => token.Text));

    private static List<Atom>? Atoms(string expression)
    {
        var parsed = Parse(expression);
        if (parsed is null)
            return null;

        var collector = new ObjectNameCollector();
        parsed.Accept(collector);
        var objectNames = collector.ObjectNames;

        var inner = WithoutOuterParentheses(parsed);
        var atoms = new List<Atom>();
        for (var index = inner.FirstTokenIndex; index <= inner.LastTokenIndex; index++)
        {
            var token = parsed.ScriptTokenStream[index];
            if (token.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment
                or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile)
                continue;

            if (objectNames.TryGetValue(index, out var name))
                atoms.Add(new Atom(AtomKind.ObjectName, name));
            else if (token.TokenType is TSqlTokenType.AsciiStringLiteral)
                atoms.Add(new Atom(AtomKind.Literal, token.Text));
            else if (token.TokenType is TSqlTokenType.UnicodeStringLiteral)
                atoms.Add(new Atom(AtomKind.Literal, "N" + token.Text[1..])); // the N prefix is case-insensitive; the quoted contents are not
            else
                atoms.Add(new Atom(AtomKind.Word, token.Text));
        }

        return atoms;
    }

    private enum AtomKind
    {
        // Keywords, built-in functions and types, operators, numbers: case-insensitive.
        Word,

        // String literals: exact.
        Literal,

        // Names of objects such as sequences and schema-qualified functions: the project's collation.
        ObjectName,
    }

    private readonly record struct Atom(AtomKind Kind, string Text)
    {
        internal bool Matches(Atom other, IEqualityComparer<string> identifiers) =>
            Kind == other.Kind && Kind switch
            {
                AtomKind.ObjectName => identifiers.Equals(Text, other.Text),
                AtomKind.Literal => string.Equals(Text, other.Text, StringComparison.Ordinal),
                _ => string.Equals(Text, other.Text, StringComparison.OrdinalIgnoreCase),
            };
    }

    // Every identifier is an object name except the built-in function and type names and the collation
    // named by a COLLATE clause, which SQL Server resolves case-insensitively. Unquoted and quoted
    // spellings both yield the bare name.
    private sealed class ObjectNameCollector : TSqlFragmentVisitor
    {
        private readonly Dictionary<int, string> _identifiers = [];
        private readonly HashSet<int> _caseInsensitive = [];

        internal Dictionary<int, string> ObjectNames =>
            _identifiers.Where(pair => !_caseInsensitive.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);

        public override void ExplicitVisit(Identifier node) => _identifiers[node.FirstTokenIndex] = node.Value;

        public override void Visit(TSqlFragment fragment)
        {
            if (fragment is PrimaryExpression { Collation: { } collation })
                _caseInsensitive.Add(collation.FirstTokenIndex);

            base.Visit(fragment);
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is null)
                _caseInsensitive.Add(node.FunctionName.FirstTokenIndex);

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SqlDataTypeReference node)
        {
            foreach (var identifier in node.Name.Identifiers)
                _caseInsensitive.Add(identifier.FirstTokenIndex);

            base.ExplicitVisit(node);
        }
    }
}
