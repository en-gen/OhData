using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.AspNetCore.Http;

namespace OhData;

/// <summary>
/// #498: bind-time signature validation shared by the four <c>EntitySetProfile.Bind*</c> methods and
/// by <c>OhDataBuilder.AddFunction</c>/<c>AddAction</c>.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, six call sites, deliberately: each rule below is a statement about how the
/// framework dispatches an operation or about what CSDL permits, and a second transcription of any
/// of them would drift from the dispatcher it is supposed to describe. It is also why the rules live
/// here rather than inside <c>BoundOperationDefinition.From</c>/<c>UnboundOperationDefinition.From</c>:
/// the bound half of <c>From</c> runs inside <c>VisitModelBuilder</c>, whose exceptions are wrapped
/// in a generic "failed to build EDM for profile" message, whereas a throw from the <c>Bind*</c>
/// method itself surfaces from the profile's own constructor with the developer's code on the stack
/// — the same idiom as the pre-existing entity-key signature check beside it.
/// </para>
/// <para>
/// Every message names the operation and the remedy. The failures these replace named neither: a
/// void function died as <c>ArgumentNullException: 'returnType'</c> from inside
/// <c>Microsoft.OData.ModelBuilder</c>, and the other two produced no diagnostic at all.
/// </para>
/// </remarks>
internal static class OperationSignatureValidation
{
    /// <summary>
    /// Validates one operation handler's signature.
    /// </summary>
    /// <param name="method">The handler's method.</param>
    /// <param name="operationName">
    /// The operation's OData name. Usually <c>method.Name</c>, but an unbound operation may have been
    /// renamed through <c>AddFunction(handler, name)</c>, and the message must quote what the
    /// developer will recognise.
    /// </param>
    /// <param name="isAction">
    /// <c>true</c> for actions (POST, §11.5.4); <c>false</c> for functions (GET, §11.5.3). Only the
    /// void-return rule depends on it.
    /// </param>
    /// <param name="subject">
    /// Message prefix identifying the declaration site, e.g. <c>"BindFunction('Ping') on entity set
    /// 'Widgets'"</c> or <c>"AddFunction('Ping')"</c>.
    /// </param>
    /// <param name="actionAlternative">
    /// The name of the sibling method that registers an action (<c>BindAction</c>,
    /// <c>BindEntityAction</c> or <c>AddAction</c>), quoted as the remedy for a void function.
    /// </param>
    internal static void Validate(
        MethodInfo method, string operationName, bool isAction, string subject, string actionAlternative)
    {
        ValidateCancellationTokenPlacement(method, subject);
        ValidateReturnType(method, operationName, isAction, subject, actionAlternative);
    }

    // #498 §2: SplitCancellationToken strips only a TRAILING CancellationToken, matched by exact
    // type -- position and name are irrelevant to it and CancellationToken? is invisible to it --
    // while RegisterEdmOperation filtered CancellationToken out at EVERY position. The two
    // disagreed, and the operation that fell into the gap could never be invoked:
    //
    //   Task<string> Bad(CancellationToken ct, int x)
    //     $metadata declares only `x`, so a metadata-conformant request is 400 MissingParameter 'ct';
    //     the route handler demands `ct` as a query parameter, and NO value satisfies it, because
    //     there is no string -> CancellationToken conversion (so any value is 400 InvalidParameter);
    //     and BuildFunctionQueryParametersMetadata documents `ct` as a required OpenAPI parameter.
    //     Three surfaces, three answers, zero reachable requests.
    //
    // A nullable token is refused for the mirror-image reason: SplitCancellationToken does not
    // recognise it, so it is NOT stripped and becomes a declared OData parameter of type
    // Nullable<CancellationToken> -- which is not an EDM type either.
    private static void ValidateCancellationTokenPlacement(MethodInfo method, string subject)
    {
        ParameterInfo[] parameters = method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            Type parameterType = parameters[i].ParameterType;

            if (parameterType == typeof(CancellationToken?))
            {
                throw new InvalidOperationException(
                    $"{subject}: parameter '{parameters[i].Name}' is a nullable CancellationToken. " +
                    "The framework supplies the request's token itself and recognises only a " +
                    "non-nullable CancellationToken in the final position; a nullable one would be " +
                    "exposed as an OData parameter that no request could ever supply a value for. " +
                    "Declare it as 'CancellationToken' and make it the last parameter, or remove it.");
            }

            if (parameterType == typeof(CancellationToken) && i != parameters.Length - 1)
            {
                throw new InvalidOperationException(
                    $"{subject}: parameter '{parameters[i].Name}' is a CancellationToken but is not " +
                    "the last parameter. The framework strips and supplies a TRAILING " +
                    "CancellationToken only, while the EDM omits one at any position -- so this " +
                    "operation would advertise one parameter list in $metadata and demand another at " +
                    "the route, and no request could satisfy it (there is no conversion from a query " +
                    "value to a CancellationToken). Move the CancellationToken to the end of the " +
                    "parameter list.");
            }
        }
    }

    private static void ValidateReturnType(
        MethodInfo method, string operationName, bool isAction, string subject, string actionAlternative)
    {
        Type rawReturn = method.ReturnType;

        // #498 §1: CSDL requires a function to declare a return type (a function is by definition a
        // request for a value), so a void/Task/ValueTask function cannot be represented at all.
        // RegisterEdmOperation and RegisterUnboundOpReturnType both SKIPPED Returns for such a
        // return, and GetEdmModel() then died with `ArgumentNullException: 'returnType'` from inside
        // Microsoft.OData.ModelBuilder -- no OhData message, no operation name. Refusing is right;
        // refusing WELL is the point. Void ACTIONS are legal and unaffected: they produce 204.
        if (!isAction && AsyncDispatchHelper.IsVoidAsyncReturn(rawReturn))
        {
            throw new InvalidOperationException(
                $"{subject}: an OData function must return a value -- CSDL has no representation for " +
                $"a function with no return type, so '{operationName}' cannot be written into " +
                $"$metadata. Return a value, or register it with {actionAlternative} instead " +
                "(an action may return void/Task/ValueTask and produces 204 No Content).");
        }

        Type returnType = AsyncDispatchHelper.IsVoidAsyncReturn(rawReturn)
            ? typeof(void)
            : AsyncDispatchHelper.UnwrapAsyncReturn(rawReturn);

        // #498 §3: an IResult is the HTTP envelope, which the framework owns. Returning one used to
        // START FINE -- Returns<IResult> maps the interface into the EDM -- and then serialize the
        // result object's own property bag as the 200 body ({"Value":{...},"StatusCode":200}). Silent
        // garbage plus a polluted model, where a one-line bind-time refusal is available.
        if (typeof(IResult).IsAssignableFrom(returnType))
        {
            throw new InvalidOperationException(
                $"{subject}: the handler returns '{returnType.Name}', which implements IResult. " +
                "OhData owns the HTTP envelope for a bound/unbound operation -- it writes the status " +
                "code, the @odata.context and the response shape -- so an IResult would be " +
                "serialized as a DTO (its Value/StatusCode properties would become the response " +
                "body) and its type would be written into $metadata as the operation's return type. " +
                "Return the value itself instead.");
        }
    }
}
