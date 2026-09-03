using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.OData.Deltas;

namespace OhData;

/// <summary>
/// Declares how exceptions escaping this profile's handlers become client errors (#581).
/// </summary>
/// <remarks>
/// The framework knows nothing about any data-access library — there is no compile-time EF Core
/// dependency anywhere in this package — so the adopter names the exception type and the framework
/// only maps it.
/// </remarks>
public interface IExceptionMappingBuilder<TModel>
    where TModel : class
{
    /// <summary>Maps <typeparamref name="TException"/> to a rejection, ignoring request context.</summary>
    IExceptionMappingBuilder<TModel> Map<TException>(Func<TException, OhDataResult> map)
        where TException : Exception;

    /// <summary>
    /// Maps <typeparamref name="TException"/> to a rejection, given what the framework knows about
    /// the request at the point of the throw.
    /// </summary>
    IExceptionMappingBuilder<TModel> Map<TException>(
        Func<OhDataExceptionContext<TModel>, TException, OhDataResult> map)
        where TException : Exception;
}

internal sealed class ExceptionMappingBuilder<TModel> : IExceptionMappingBuilder<TModel>
    where TModel : class
{
    internal List<ExceptionMappingRule<TModel>> Rules { get; } = new();

    public IExceptionMappingBuilder<TModel> Map<TException>(Func<TException, OhDataResult> map)
        where TException : Exception
    {
        if (map is null) throw new ArgumentNullException(nameof(map));
        return Add<TException>((_, ex) => map((TException)ex));
    }

    public IExceptionMappingBuilder<TModel> Map<TException>(
        Func<OhDataExceptionContext<TModel>, TException, OhDataResult> map)
        where TException : Exception
    {
        if (map is null) throw new ArgumentNullException(nameof(map));
        return Add<TException>((ctx, ex) => map(ctx, (TException)ex));
    }

    private IExceptionMappingBuilder<TModel> Add<TException>(
        Func<OhDataExceptionContext<TModel>, Exception, OhDataResult> map)
        where TException : Exception
    {
        Type exceptionType = typeof(TException);

        // #494 is this defect one layer down: the $expand pushdown caught InvalidOperationException
        // and answered 400, and SqlClient reports connection-pool exhaustion as exactly that. A
        // mapping wide enough to catch everything reports infrastructure faults as client errors and
        // tells retry logic the opposite of the truth. Exception itself is never a defensible
        // choice, so it is refused; narrower-but-still-broad types are the adopter's judgment, and
        // docs/error-handling.md carries the rule.
        if (exceptionType == typeof(Exception))
        {
            throw new ArgumentException(
                "OhData: Map<Exception>() would convert every server fault into a client error, " +
                "including infrastructure failures a client cannot act on. Name the specific " +
                "exception type the handler raises.");
        }

        if (Rules.Any(existing => existing.ExceptionType == exceptionType))
        {
            throw new InvalidOperationException(
                $"OhData: an exception mapping for '{exceptionType.Name}' is already declared on " +
                "this profile. Declare it once.");
        }

        Rules.Add(new ExceptionMappingRule<TModel>(exceptionType, map));
        return this;
    }
}

internal sealed class ExceptionMappingRule<TModel>
    where TModel : class
{
    internal ExceptionMappingRule(
        Type exceptionType, Func<OhDataExceptionContext<TModel>, Exception, OhDataResult> map)
    {
        ExceptionType = exceptionType;
        Map = map;
        Depth = InheritanceDepth(exceptionType);
    }

    internal Type ExceptionType { get; }
    internal Func<OhDataExceptionContext<TModel>, Exception, OhDataResult> Map { get; }

    /// <summary>Distance from <see cref="Exception"/>, so the most-derived match can win.</summary>
    internal int Depth { get; }

    private static int InheritanceDepth(Type type)
    {
        int depth = 0;
        for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType) depth++;
        return depth;
    }
}

/// <summary>
/// The pieces of request state a seam contributes to <see cref="OhDataExceptionContext{TModel}"/>,
/// carried without the model's type so <c>IEntitySetEndpointSource</c> stays non-generic — the same
/// erasure the <c>Invoke*Async</c> members use.
/// </summary>
internal readonly struct ExceptionSeamData
{
    internal ExceptionSeamData(
        OhDataOperation operation,
        string? queryString = null,
        object? key = null,
        object? model = null,
        object? delta = null,
        string? navigation = null)
    {
        Operation = operation;
        QueryString = queryString;
        Key = key;
        Model = model;
        Delta = delta;
        Navigation = navigation;
    }

    internal OhDataOperation Operation { get; }
    internal string? QueryString { get; }
    internal object? Key { get; }
    internal object? Model { get; }
    internal object? Delta { get; }
    internal string? Navigation { get; }
}

internal static class ExceptionMappingResolver
{
    /// <summary>
    /// The most-derived rule matching <paramref name="ex"/>, or <c>null</c>. Most-derived rather
    /// than registration-order so that declaring a base and a derived mapping behaves the way a
    /// <c>catch</c> ladder reads, whichever order they were written in.
    /// </summary>
    internal static ExceptionMappingRule<TModel>? Resolve<TModel>(
        IReadOnlyList<ExceptionMappingRule<TModel>> rules, Exception ex)
        where TModel : class
    {
        // MaxBy keeps the FIRST of equal-depth matches, as the hand-rolled loop this replaced did.
        // Two rules cannot have the same type (refused at declaration), so a tie means two unrelated
        // branches of the hierarchy both matched -- declaration order is as good an answer as any.
        return rules
            .Where(rule => rule.ExceptionType.IsInstanceOfType(ex))
            .MaxBy(rule => rule.Depth);
    }

    internal static OhDataExceptionContext<TModel> BuildContext<TModel>(
        string entitySetName, in ExceptionSeamData data)
        where TModel : class =>
        new(entitySetName,
            data.Operation,
            data.QueryString,
            data.Key,
            data.Model is TModel typed ? typed : default,
            data.Delta as Delta<TModel>,
            data.Navigation);
}
