using System.Linq.Expressions;
using FireBlazor.Platform.Wasm;

namespace FireBlazor.Tests.Firestore;

public class WhereExpressionVisitorTests
{
    [Fact]
    public void Visit_EqualOperator_ExtractsWhereClause()
    {
        Expression<Func<TestDocument, bool>> expr = x => x.Name == "John";
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("name", visitor.Clauses[0].Field);
        Assert.Equal("==", visitor.Clauses[0].Operator);
        Assert.Equal("John", visitor.Clauses[0].Value);
    }

    [Fact]
    public void Visit_NotEqualOperator_ExtractsWhereClause()
    {
        Expression<Func<TestDocument, bool>> expr = x => x.Name != "John";
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("!=", visitor.Clauses[0].Operator);
    }

    [Fact]
    public void Visit_GreaterThan_ExtractsWhereClause()
    {
        Expression<Func<TestDocument, bool>> expr = x => x.Age > 18;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("age", visitor.Clauses[0].Field);
        Assert.Equal(">", visitor.Clauses[0].Operator);
        Assert.Equal(18, visitor.Clauses[0].Value);
    }

    [Fact]
    public void Visit_GreaterThanOrEqual_ExtractsWhereClause()
    {
        Expression<Func<TestDocument, bool>> expr = x => x.Age >= 21;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal(">=", visitor.Clauses[0].Operator);
    }

    [Fact]
    public void Visit_LessThan_ExtractsWhereClause()
    {
        Expression<Func<TestDocument, bool>> expr = x => x.Age < 65;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("<", visitor.Clauses[0].Operator);
    }

    [Fact]
    public void Visit_LessThanOrEqual_ExtractsWhereClause()
    {
        Expression<Func<TestDocument, bool>> expr = x => x.Age <= 65;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("<=", visitor.Clauses[0].Operator);
    }

    [Fact]
    public void Visit_AndAlso_ExtractsMultipleClauses()
    {
        Expression<Func<TestDocument, bool>> expr = x => x.Name == "John" && x.Age > 18;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Equal(2, visitor.Clauses.Count);
    }

    [Fact]
    public void Visit_ReversedComparison_HandlesCorrectly()
    {
        Expression<Func<TestDocument, bool>> expr = x => 18 < x.Age;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("age", visitor.Clauses[0].Field);
        Assert.Equal(">", visitor.Clauses[0].Operator);
        Assert.Equal(18, visitor.Clauses[0].Value);
    }

    [Fact]
    public void Visit_ContainsOnArrayProperty_ExtractsArrayContains()
    {
        Expression<Func<TestDocument, bool>> expr = x => x.Tags.Contains("featured");
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("tags", visitor.Clauses[0].Field);
        Assert.Equal("array-contains", visitor.Clauses[0].Operator);
        Assert.Equal("featured", visitor.Clauses[0].Value);
    }

    [Fact]
    public void Visit_ArrayContainsWithVariable_ExtractsValue()
    {
        var tagToFind = "popular";
        Expression<Func<TestDocument, bool>> expr = x => x.Tags.Contains(tagToFind);
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("array-contains", visitor.Clauses[0].Operator);
        Assert.Equal("popular", visitor.Clauses[0].Value);
    }

    [Fact]
    public void Visit_InOperator_ExtractsInClause()
    {
        var validStatuses = new[] { "active", "pending" };
        Expression<Func<TestDocument, bool>> expr = x => validStatuses.Contains(x.Status);
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("status", visitor.Clauses[0].Field);
        Assert.Equal("in", visitor.Clauses[0].Operator);
        Assert.Equal(validStatuses, visitor.Clauses[0].Value);
    }

    [Fact]
    public void Visit_NotInOperator_ExtractsNotInClause()
    {
        var excludedStatuses = new[] { "deleted", "archived" };
        Expression<Func<TestDocument, bool>> expr = x => !excludedStatuses.Contains(x.Status);
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("status", visitor.Clauses[0].Field);
        Assert.Equal("not-in", visitor.Clauses[0].Operator);
        Assert.Equal(excludedStatuses, visitor.Clauses[0].Value);
    }

    [Fact]
    public void Visit_CapturedVariable_ExtractsValue()
    {
        var minAge = 21;
        Expression<Func<TestDocument, bool>> expr = x => x.Age >= minAge;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal(21, visitor.Clauses[0].Value);
    }

    [Fact]
    public void Visit_PropertyOfCapturedObject_ExtractsValue()
    {
        var criteria = new { MinAge = 18 };
        Expression<Func<TestDocument, bool>> expr = x => x.Age >= criteria.MinAge;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal(18, visitor.Clauses[0].Value);
    }

    // --- Bare boolean members (root-cause fix) ---
    // Before the fix, Not(member) and a bare positive member produced NO where clause on WASM, so
    // the filter silently vanished and the query returned rows it should have excluded. FakeFirestore
    // compiles the real predicate, so it never reproduced this — only the JS-SDK translator did.

    [Fact]
    public void Visit_BareBooleanNegation_ExtractsEqualsFalse()
    {
        Expression<Func<TestDocument, bool>> expr = x => !x.Flag;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("flag", visitor.Clauses[0].Field);
        Assert.Equal("==", visitor.Clauses[0].Operator);
        Assert.Equal(false, visitor.Clauses[0].Value);
    }

    [Fact]
    public void Visit_BarePositiveBoolean_ExtractsEqualsTrue()
    {
        Expression<Func<TestDocument, bool>> expr = x => x.Flag;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("flag", visitor.Clauses[0].Field);
        Assert.Equal("==", visitor.Clauses[0].Operator);
        Assert.Equal(true, visitor.Clauses[0].Value);
    }

    [Fact]
    public void Visit_AndAlsoWithBareNegation_ExtractsTwoClauses()
    {
        Expression<Func<TestDocument, bool>> expr = x => x.Name == "a" && !x.Flag;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Equal(2, visitor.Clauses.Count);
        Assert.Equal("name", visitor.Clauses[0].Field);
        Assert.Equal("==", visitor.Clauses[0].Operator);
        Assert.Equal("a", visitor.Clauses[0].Value);
        Assert.Equal("flag", visitor.Clauses[1].Field);
        Assert.Equal("==", visitor.Clauses[1].Operator);
        Assert.Equal(false, visitor.Clauses[1].Value);
    }

    [Fact]
    public void Visit_AndAlsoWithBarePositive_ExtractsTwoClauses()
    {
        Expression<Func<TestDocument, bool>> expr = x => x.Name == "a" && x.Flag;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Equal(2, visitor.Clauses.Count);
        Assert.Equal("name", visitor.Clauses[0].Field);
        Assert.Equal("flag", visitor.Clauses[1].Field);
        Assert.Equal("==", visitor.Clauses[1].Operator);
        Assert.Equal(true, visitor.Clauses[1].Value);
    }

    [Fact]
    public void Visit_BooleanEqualsFalse_StillExtractsSingleClause()
    {
        // The per-query workaround (`x.Flag == false`) must keep translating to exactly one clause,
        // and must NOT double up now that a VisitMember override exists.
        Expression<Func<TestDocument, bool>> expr = x => x.Flag == false;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("flag", visitor.Clauses[0].Field);
        Assert.Equal("==", visitor.Clauses[0].Operator);
        Assert.Equal(false, visitor.Clauses[0].Value);
    }

    [Fact]
    public void Visit_CapturedBooleanOperand_DoesNotProduceClause()
    {
        // A captured bool is a client-side constant, not a document field, so it must not become a
        // where clause — only the real field comparison should survive.
        var enabled = true;
        Expression<Func<TestDocument, bool>> expr = x => x.Name == "a" && enabled;
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("name", visitor.Clauses[0].Field);
    }

    [Fact]
    public void Visit_BooleanMemberAsContainsArgument_DoesNotDoubleClause()
    {
        // boolFlags.Contains(x.Flag) is an in-query on a bool field. VisitMethodCall must not recurse
        // into the argument, or x.Flag would also add a spurious ("flag","==",true) clause.
        var boolFlags = new[] { true };
        Expression<Func<TestDocument, bool>> expr = x => boolFlags.Contains(x.Flag);
        var visitor = new WhereExpressionVisitor();

        visitor.Visit(expr);

        Assert.Single(visitor.Clauses);
        Assert.Equal("flag", visitor.Clauses[0].Field);
        Assert.Equal("in", visitor.Clauses[0].Operator);
    }
}
