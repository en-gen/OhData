using System;
using System.Collections.Generic;
using System.Linq;

namespace OhData.Abstractions;

/// <summary>
/// Single-use accumulator behind <c>EntitySetProfile.ConfigureAuthorization</c>. Collects one
/// <see cref="OperationAuthRule"/> per category selector call; later rules win on overlap (resolved
/// by the factory).
/// </summary>
internal sealed class AuthorizationRuleBuilder : IAuthorizationRuleBuilder
{
    private readonly List<OperationAuthRule> _rules = new();

    public IReadOnlyList<OperationAuthRule> Rules => _rules;

    public IAuthorizationRuleBuilder Read(Action<ICategoryAuthorizationBuilder> configure) =>
        Add(OhDataOperation.Read, null, configure);

    public IAuthorizationRuleBuilder Create(Action<ICategoryAuthorizationBuilder> configure) =>
        Add(OhDataOperation.Create, null, configure);

    public IAuthorizationRuleBuilder Update(Action<ICategoryAuthorizationBuilder> configure) =>
        Add(OhDataOperation.Update, null, configure);

    public IAuthorizationRuleBuilder Delete(Action<ICategoryAuthorizationBuilder> configure) =>
        Add(OhDataOperation.Delete, null, configure);

    public IAuthorizationRuleBuilder Writes(Action<ICategoryAuthorizationBuilder> configure) =>
        Add(OhDataOperation.Write, null, configure);

    public IAuthorizationRuleBuilder All(Action<ICategoryAuthorizationBuilder> configure) =>
        Add(OhDataOperation.All, null, configure);

    public IAuthorizationRuleBuilder Invoke(Action<ICategoryAuthorizationBuilder> configure) =>
        Add(OhDataOperation.Invoke, null, configure);

    public IAuthorizationRuleBuilder Invoke(string boundOperationName, Action<ICategoryAuthorizationBuilder> configure)
    {
        if (string.IsNullOrWhiteSpace(boundOperationName))
            throw new ArgumentException("A bound operation name is required.", nameof(boundOperationName));
        return Add(OhDataOperation.Invoke, boundOperationName, configure);
    }

    private IAuthorizationRuleBuilder Add(
        OhDataOperation operations, string? boundOperationName, Action<ICategoryAuthorizationBuilder> configure)
    {
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));
        var category = new CategoryAuthorizationBuilder();
        configure(category);
        _rules.Add(category.Build(operations, boundOperationName));
        return this;
    }
}

/// <summary>
/// Inner per-category accumulator. Requirements combine with AND; <see cref="AllowAnonymous"/> is
/// exclusive with any <c>Require*</c> call.
/// </summary>
internal sealed class CategoryAuthorizationBuilder : ICategoryAuthorizationBuilder
{
    private readonly List<AuthRequirement> _requirements = new();
    private bool _allowAnonymous;

    public ICategoryAuthorizationBuilder RequireAuthenticatedUser() =>
        Add(new AuthRequirement(AuthRequirementKind.AuthenticatedUser));

    public ICategoryAuthorizationBuilder RequireRole(params string[] roles)
    {
        if (roles is null || roles.Length == 0)
            throw new ArgumentException("At least one role must be specified.", nameof(roles));
        return Add(new AuthRequirement(AuthRequirementKind.Role, Values: Array.AsReadOnly(roles.ToArray())));
    }

    public ICategoryAuthorizationBuilder RequireClaim(string claimType, params string[] allowedValues)
    {
        if (string.IsNullOrWhiteSpace(claimType))
            throw new ArgumentException("A claim type is required.", nameof(claimType));
        IReadOnlyList<string>? values = allowedValues is { Length: > 0 }
            ? Array.AsReadOnly(allowedValues.ToArray())
            : null;
        return Add(new AuthRequirement(AuthRequirementKind.Claim, Name: claimType, Values: values));
    }

    public ICategoryAuthorizationBuilder RequirePolicy(string policyName)
    {
        if (string.IsNullOrWhiteSpace(policyName))
            throw new ArgumentException("A policy name is required.", nameof(policyName));
        return Add(new AuthRequirement(AuthRequirementKind.Policy, Name: policyName));
    }

    public ICategoryAuthorizationBuilder RequireResource() =>
        Add(new AuthRequirement(AuthRequirementKind.Resource));

    public ICategoryAuthorizationBuilder RequireResource(string policyName)
    {
        if (string.IsNullOrWhiteSpace(policyName))
            throw new ArgumentException("A policy name is required.", nameof(policyName));
        return Add(new AuthRequirement(AuthRequirementKind.Resource, Name: policyName));
    }

    public void AllowAnonymous()
    {
        if (_requirements.Count > 0)
        {
            throw new InvalidOperationException(
                "AllowAnonymous() cannot be combined with a Require* call on the same operation category.");
        }

        _allowAnonymous = true;
    }

    private ICategoryAuthorizationBuilder Add(AuthRequirement requirement)
    {
        if (_allowAnonymous)
        {
            throw new InvalidOperationException(
                "A Require* call cannot be combined with AllowAnonymous() on the same operation category.");
        }

        _requirements.Add(requirement);
        return this;
    }

    public OperationAuthRule Build(OhDataOperation operations, string? boundOperationName)
    {
        if (!_allowAnonymous && _requirements.Count == 0)
        {
            throw new InvalidOperationException(
                $"Operation category '{operations}' was configured with no requirements. " +
                "Call AllowAnonymous() or at least one Require* method inside the category lambda.");
        }

        return new OperationAuthRule(
            operations,
            _allowAnonymous,
            _allowAnonymous ? Array.Empty<AuthRequirement>() : _requirements.AsReadOnly(),
            boundOperationName);
    }
}
