namespace Trellis.Testing.SqlProject.Tests;

using FluentAssertions;

public class SqlExpressionTextTests
{
    private static readonly StringComparer CaseSensitive = StringComparer.Ordinal;
    private static readonly StringComparer CaseInsensitive = StringComparer.OrdinalIgnoreCase;

    [Theory]
    [InlineData("5", "(5)")]
    [InlineData("((5))", "5")]
    [InlineData("(a) + (b)", "(a) + (b)")]
    [InlineData("getdate()", "GETDATE()")]
    [InlineData("CAST(1 AS INT)", "cast(1 as int)")]
    [InlineData("NEXT VALUE FOR [dbo].[Seq]", "next value for dbo.Seq")]
    [InlineData("N'abc'", "N'abc'")]
    [InlineData("1 /* note */ + 2", "1 + 2")]
    public void Equivalent_SameMeaning_AnyCollation_ReturnsTrue(string left, string right)
    {
        SqlExpressionText.Equivalent(left, right, CaseSensitive).Should().BeTrue();
        SqlExpressionText.Equivalent(left, right, CaseInsensitive).Should().BeTrue();
    }

    [Theory]
    [InlineData("'a'", "'A'")]
    [InlineData("(1 + 1)", "(2)")]
    [InlineData("(a) + (b)", "((a) + (b)) + 1")]
    [InlineData("getdate()", "getutcdate()")]
    [InlineData("1", "1.0")]
    public void Equivalent_DifferentMeaning_AnyCollation_ReturnsFalse(string left, string right)
    {
        SqlExpressionText.Equivalent(left, right, CaseSensitive).Should().BeFalse();
        SqlExpressionText.Equivalent(left, right, CaseInsensitive).Should().BeFalse();
    }

    [Theory]
    [InlineData("NEXT VALUE FOR [dbo].[Seq]", "NEXT VALUE FOR [dbo].[seq]")]
    [InlineData("[dbo].[Fn](1)", "[dbo].[FN](1)")]
    [InlineData("dbo.Fn(1)", "DBO.Fn(1)")]
    public void Equivalent_ObjectNamesDifferOnlyByCase_FollowsTheCollation(string left, string right)
    {
        SqlExpressionText.Equivalent(left, right, CaseSensitive).Should().BeFalse();
        SqlExpressionText.Equivalent(left, right, CaseInsensitive).Should().BeTrue();
    }

    [Fact]
    public void Equivalent_ParenthesisInsideACommentInsideTheOuterPair_IsStillTheSameExpression() =>
        SqlExpressionText.Equivalent("(5 /* ) */)", "5", CaseInsensitive).Should().BeTrue();

    [Theory]
    [InlineData("5", "5")]
    [InlineData("(5)", "5")]
    [InlineData("((5))", "5")]
    [InlineData("(5 /* ) */)", "5")]
    [InlineData("((a) + (b))", "(a) + (b)")]
    [InlineData("  ( getdate() )  ", "getdate()")]
    [InlineData("5 +", "5 +")]
    public void Display_ShowsTheExpressionWithoutRedundantOuterParentheses(string expression, string expected) =>
        SqlExpressionText.Display(expression).Should().Be(expected);

    [Fact]
    public void Display_NoExpression_SaysNone() =>
        SqlExpressionText.Display(null).Should().Be("(none)");

    [Theory]
    [InlineData("n'a'", "N'a'")]
    [InlineData("(n'a')", "N'a'")]
    public void Equivalent_UnicodeLiteralPrefixDiffersOnlyByCase_ReturnsTrue(string left, string right)
    {
        SqlExpressionText.Equivalent(left, right, CaseSensitive).Should().BeTrue();
        SqlExpressionText.Equivalent(left, right, CaseInsensitive).Should().BeTrue();
    }

    [Theory]
    [InlineData("N'a'", "N'A'")]
    [InlineData("N'a'", "'a'")]
    public void Equivalent_UnicodeLiteralDiffersInContentOrKind_ReturnsFalse(string left, string right) =>
        SqlExpressionText.Equivalent(left, right, CaseInsensitive).Should().BeFalse();

    [Fact]
    public void Equivalent_CollationNameDiffersOnlyByCase_ReturnsTrueUnderAnyCollation()
    {
        const string left = "'a' COLLATE Latin1_General_100_CS_AS";
        const string right = "'a' COLLATE latin1_general_100_cs_as";

        SqlExpressionText.Equivalent(left, right, CaseSensitive).Should().BeTrue("collation names are case-insensitive");
        SqlExpressionText.Equivalent(left, right, CaseInsensitive).Should().BeTrue();
    }

    [Fact]
    public void Equivalent_DifferentCollations_ReturnsFalse() =>
        SqlExpressionText.Equivalent(
                "'a' COLLATE Latin1_General_100_CS_AS", "'a' COLLATE Latin1_General_100_CI_AS", CaseInsensitive)
            .Should().BeFalse();

    [Fact]
    public void Equivalent_OnlyOneSideHasAnExpression_ReturnsFalse()
    {
        SqlExpressionText.Equivalent("5", null, CaseInsensitive).Should().BeFalse();
        SqlExpressionText.Equivalent(null, "5", CaseInsensitive).Should().BeFalse();
    }

    [Fact]
    public void Equivalent_NeitherSideHasAnExpression_ReturnsTrue() =>
        SqlExpressionText.Equivalent(null, null, CaseInsensitive).Should().BeTrue();

    [Fact]
    public void Equivalent_UnparsableExpressions_FallBackToAnExactComparison()
    {
        SqlExpressionText.Equivalent("5 +", "5 +", CaseInsensitive).Should().BeTrue();
        SqlExpressionText.Equivalent("5 +", "6 +", CaseInsensitive).Should().BeFalse();
        SqlExpressionText.Equivalent("a +", "A +", CaseInsensitive).Should().BeFalse("an unparsed expression is never case-folded");
    }
}
