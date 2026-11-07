using System.Linq.Expressions;
using FireBlazor.Platform.Wasm;

namespace FireBlazor.Tests.Firestore;

/// <summary>
/// Comparisons against <see cref="Nullable{T}"/> members. The C# compiler lifts the non-null operand
/// with a Convert node (e.g. <c>x.Flag == true</c> on a <c>bool?</c> becomes
/// <c>x.Flag == Convert(true, bool?)</c>), and enum members are compared through a Convert on the
/// member side. Both shapes used to fall through ExtractMemberAndValue: the constant side came back
/// as a null value, so <c>x.Flag == true</c> was silently sent to Firestore as <c>flag == null</c>
/// (IS_NULL), and enum comparisons produced no clause at all.
/// </summary>
public class WhereExpressionVisitorNullableTests
{
    public enum Stage
    {
        Lead = 0,
        Active = 1,
        Closed = 2,
    }

    public class NullableDocument
    {
        public bool? Pending { get; set; }
        public int? Count { get; set; }
        public double? Score { get; set; }
        public Stage? Phase { get; set; }
        public Stage Kind { get; set; }
        public DateTime? When { get; set; }
    }

    private static WhereClause Single(Expression<Func<NullableDocument, bool>> expr)
    {
        var visitor = new WhereExpressionVisitor();
        visitor.Visit(expr);
        return Assert.Single(visitor.Clauses);
    }

    private static void AssertClause(WhereClause clause, string field, string op, object? value)
    {
        Assert.Equal(field, clause.Field);
        Assert.Equal(op, clause.Operator);
        Assert.Equal(value, clause.Value);
    }

    // ---- bool? ----

    [Fact]
    public void NullableBool_EqualsTrue_EmitsEqualsTrue() =>
        AssertClause(Single(x => x.Pending == true), "pending", "==", true);

    [Fact]
    public void NullableBool_EqualsFalse_EmitsEqualsFalse() =>
        AssertClause(Single(x => x.Pending == false), "pending", "==", false);

    [Fact]
    public void NullableBool_TrueEqualsMember_ReversedOperands_EmitsEqualsTrue() =>
        AssertClause(Single(x => true == x.Pending), "pending", "==", true);

    [Fact]
    public void NullableBool_NotEqualsTrue_EmitsNotEqualsTrue() =>
        AssertClause(Single(x => x.Pending != true), "pending", "!=", true);

    [Fact]
    public void NullableBool_EqualsCapturedLocal_EmitsCapturedValue()
    {
        var wanted = true;
        AssertClause(Single(x => x.Pending == wanted), "pending", "==", true);
    }

    [Fact]
    public void NullableBool_CapturedLocalEqualsMember_ReversedOperands_EmitsCapturedValue()
    {
        var wanted = false;
        AssertClause(Single(x => wanted == x.Pending), "pending", "==", false);
    }

    [Fact]
    public void NullableBool_InConjunction_EmitsBothClauses()
    {
        var visitor = new WhereExpressionVisitor();
        Expression<Func<NullableDocument, bool>> expr = x => x.Pending == true && x.Count == 3;
        visitor.Visit(expr);

        Assert.Equal(2, visitor.Clauses.Count);
        AssertClause(visitor.Clauses[0], "pending", "==", true);
        AssertClause(visitor.Clauses[1], "count", "==", 3);
    }

    // ---- int? ----

    [Fact]
    public void NullableInt_Equals_EmitsValue() =>
        AssertClause(Single(x => x.Count == 5), "count", "==", 5);

    [Fact]
    public void NullableInt_NotEquals_EmitsValue() =>
        AssertClause(Single(x => x.Count != 5), "count", "!=", 5);

    [Fact]
    public void NullableInt_GreaterThan_EmitsValue() =>
        AssertClause(Single(x => x.Count > 5), "count", ">", 5);

    [Fact]
    public void NullableInt_GreaterThanOrEqual_EmitsValue() =>
        AssertClause(Single(x => x.Count >= 5), "count", ">=", 5);

    [Fact]
    public void NullableInt_LessThan_EmitsValue() =>
        AssertClause(Single(x => x.Count < 5), "count", "<", 5);

    [Fact]
    public void NullableInt_LessThanOrEqual_EmitsValue() =>
        AssertClause(Single(x => x.Count <= 5), "count", "<=", 5);

    [Fact]
    public void NullableInt_ConstantLessThanMember_ReversedOperands_FlipsOperator() =>
        AssertClause(Single(x => 5 < x.Count), "count", ">", 5);

    [Fact]
    public void NullableInt_ConstantGreaterThanOrEqualMember_ReversedOperands_FlipsOperator() =>
        AssertClause(Single(x => 5 >= x.Count), "count", "<=", 5);

    [Fact]
    public void NullableInt_CapturedLocal_EmitsCapturedValue()
    {
        var min = 7;
        AssertClause(Single(x => x.Count >= min), "count", ">=", 7);
    }

    [Fact]
    public void NullableInt_CapturedLocal_ReversedOperands_FlipsOperator()
    {
        var max = 7;
        AssertClause(Single(x => max > x.Count), "count", "<", 7);
    }

    // ---- double? ----

    [Fact]
    public void NullableDouble_Equals_EmitsValue() =>
        AssertClause(Single(x => x.Score == 1.5), "score", "==", 1.5);

    [Fact]
    public void NullableDouble_LessThanOrEqual_EmitsValue() =>
        AssertClause(Single(x => x.Score <= 1.5), "score", "<=", 1.5);

    [Fact]
    public void NullableDouble_ConstantGreaterThanMember_ReversedOperands_FlipsOperator() =>
        AssertClause(Single(x => 1.5 > x.Score), "score", "<", 1.5);

    [Fact]
    public void NullableDouble_CapturedLocal_EmitsCapturedValue()
    {
        var threshold = 0.25;
        AssertClause(Single(x => x.Score > threshold), "score", ">", 0.25);
    }

    // ---- enum? and enum ----

    [Fact]
    public void NullableEnum_Equals_EmitsEnumValue() =>
        AssertClause(Single(x => x.Phase == Stage.Active), "phase", "==", Stage.Active);

    [Fact]
    public void NullableEnum_NotEquals_EmitsEnumValue() =>
        AssertClause(Single(x => x.Phase != Stage.Closed), "phase", "!=", Stage.Closed);

    [Fact]
    public void NullableEnum_ReversedOperands_EmitsEnumValue() =>
        AssertClause(Single(x => Stage.Lead == x.Phase), "phase", "==", Stage.Lead);

    [Fact]
    public void NullableEnum_CapturedLocal_EmitsEnumValue()
    {
        var wanted = Stage.Closed;
        AssertClause(Single(x => x.Phase == wanted), "phase", "==", Stage.Closed);
    }

    [Fact]
    public void NullableEnum_CapturedLocal_ReversedOperands_EmitsEnumValue()
    {
        var wanted = Stage.Active;
        AssertClause(Single(x => wanted != x.Phase), "phase", "!=", Stage.Active);
    }

    [Fact]
    public void Enum_Equals_EmitsEnumValue() =>
        AssertClause(Single(x => x.Kind == Stage.Active), "kind", "==", Stage.Active);

    [Fact]
    public void Enum_CapturedLocal_ReversedOperands_EmitsEnumValue()
    {
        var wanted = Stage.Closed;
        AssertClause(Single(x => wanted == x.Kind), "kind", "==", Stage.Closed);
    }

    // ---- DateTime? ----

    private static readonly DateTime Cutoff = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public void NullableDateTime_GreaterThanOrEqual_StaticField_EmitsValue() =>
        AssertClause(Single(x => x.When >= Cutoff), "when", ">=", Cutoff);

    [Fact]
    public void NullableDateTime_CapturedLocal_LessThan_EmitsValue()
    {
        var cutoff = Cutoff;
        AssertClause(Single(x => x.When < cutoff), "when", "<", Cutoff);
    }

    [Fact]
    public void NullableDateTime_CapturedLocal_ReversedOperands_FlipsOperator()
    {
        var cutoff = Cutoff;
        AssertClause(Single(x => cutoff <= x.When), "when", ">=", Cutoff);
    }

    [Fact]
    public void NullableDateTime_Equals_EmitsValue()
    {
        var cutoff = Cutoff;
        AssertClause(Single(x => x.When == cutoff), "when", "==", Cutoff);
    }

    // ---- real null comparisons must stay IS_NULL / IS_NOT_NULL ----

    [Fact]
    public void NullableBool_EqualsNull_StaysNullComparison() =>
        AssertClause(Single(x => x.Pending == null), "pending", "==", null);

    [Fact]
    public void NullableBool_NotEqualsNull_StaysNotNullComparison() =>
        AssertClause(Single(x => x.Pending != null), "pending", "!=", null);

    [Fact]
    public void NullableInt_NullEqualsMember_ReversedOperands_StaysNullComparison() =>
        AssertClause(Single(x => null == x.Count), "count", "==", null);

    [Fact]
    public void NullableEnum_EqualsNull_StaysNullComparison() =>
        AssertClause(Single(x => x.Phase == null), "phase", "==", null);

    [Fact]
    public void NullableDateTime_NotEqualsNull_StaysNotNullComparison() =>
        AssertClause(Single(x => x.When != null), "when", "!=", null);

    [Fact]
    public void NullableBool_EqualsCapturedNull_StaysNullComparison()
    {
        bool? wanted = null;
        AssertClause(Single(x => x.Pending == wanted), "pending", "==", null);
    }
}
