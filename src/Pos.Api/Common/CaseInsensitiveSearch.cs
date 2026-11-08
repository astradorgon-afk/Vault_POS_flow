using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Pos.Api.Common;

/// <summary>
/// Lets one search predicate written with PostgreSQL's <c>ILIKE</c> run on
/// either database provider.
/// </summary>
/// <remarks>
/// <c>EF.Functions.ILike</c> exists only in the Npgsql provider. On SQLite (the
/// integration-test host and the SQLite deployment mode) EF Core cannot
/// translate it, so the whole query throws and the endpoint answers 500.
/// SQLite's <c>LIKE</c> already ignores ASCII case, so the predicate is
/// rewritten to <c>EF.Functions.Like</c> there, with the same pattern and escape
/// character. On PostgreSQL the predicate is returned untouched, so its SQL does
/// not change.
/// </remarks>
internal static class CaseInsensitiveSearch
{
    private static readonly MethodInfo LikeWithEscape = typeof(DbFunctionsExtensions).GetMethod(
        nameof(DbFunctionsExtensions.Like),
        [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    /// <summary>Returns <paramref name="predicate"/>, with ILike as Like when <paramref name="sqlite"/> is true.</summary>
    public static Expression<Func<T, bool>> ForProvider<T>(this Expression<Func<T, bool>> predicate, bool sqlite)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return sqlite ? (Expression<Func<T, bool>>)new ILikeToLike().Visit(predicate) : predicate;
    }

    private sealed class ILikeToLike : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == "ILike" &&
                node.Method.DeclaringType?.Name == "NpgsqlDbFunctionsExtensions" &&
                node.Arguments.Count == 4)
            {
                return Expression.Call(LikeWithEscape, [.. node.Arguments.Select(Visit).Cast<Expression>()]);
            }

            return base.VisitMethodCall(node);
        }
    }
}
