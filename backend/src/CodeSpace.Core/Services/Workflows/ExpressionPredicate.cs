using System.Linq.Expressions;

namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Minimal boolean-predicate combinators for composing EF-translatable <c>Where</c> filters — <see cref="Or"/> two
/// predicates (rebinding the second's parameter onto the first's so the result is one single-parameter lambda EF can
/// translate) and <see cref="Not"/> one. The runs filter uses these to fold the decision-EXISTS into the broad
/// NeedsAttention union and to apply the <c>false</c> (negated) form of a bool flag, without duplicating the subquery.
/// </summary>
internal static class ExpressionPredicate
{
    public static Expression<Func<T, bool>> Or<T>(Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ReplaceParameterVisitor(right.Parameters[0], parameter).Visit(right.Body)!;
        return Expression.Lambda<Func<T, bool>>(Expression.OrElse(left.Body, rightBody), parameter);
    }

    public static Expression<Func<T, bool>> Not<T>(Expression<Func<T, bool>> predicate) =>
        Expression.Lambda<Func<T, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    private sealed class ReplaceParameterVisitor : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly ParameterExpression _to;

        public ReplaceParameterVisitor(ParameterExpression from, ParameterExpression to)
        {
            _from = from;
            _to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node) => node == _from ? _to : base.VisitParameter(node);
    }
}
