using System;
using System.Collections.Generic;
using System.Linq;

namespace BBT.Aether.Users;

/// <summary>
/// Header/claim dictionary helpers for <see cref="ICurrentUser"/>.
/// <para>
/// These work over a plain <see cref="IReadOnlyDictionary{TKey,TValue}"/> rather than an
/// <c>HttpContext</c>, so they are usable from scopes with no ambient HTTP request — background jobs,
/// message consumers, workflow execution resumed out of band. The keys are the ones in
/// <see cref="AetherClaimTypes"/>, which is also what
/// <c>BBT.Aether.AspNetCore.Security.HeaderCurrentUserResolver</c> reads from the request, so a user
/// captured on the way in can be restored later, or forwarded to a downstream service, unchanged.
/// </para>
/// </summary>
public static class CurrentUserHeaderExtensions
{
    /// <summary>
    /// Makes the user described by <paramref name="headers"/> current for the lifetime of the returned
    /// disposable; the previous user is restored on dispose.
    /// </summary>
    /// <param name="currentUser">The current user service.</param>
    /// <param name="headers">
    /// Claim headers keyed by <see cref="AetherClaimTypes"/> values. When null or empty, nothing changes
    /// and a no-op disposable is returned — the ambient user, if any, stays in place.
    /// </param>
    /// <returns>An IDisposable that restores the previous user when disposed.</returns>
    public static IDisposable ChangeFromHeaders(
        this ICurrentUser currentUser,
        IReadOnlyDictionary<string, string?>? headers)
    {
        Check.NotNull(currentUser, nameof(currentUser));

        if (headers is null || headers.Count == 0)
        {
            return NullDisposable.Instance;
        }

        return currentUser.Change(new BasicUserInfo(
            headers.GetValueOrDefault(AetherClaimTypes.UserId),
            headers.GetValueOrDefault(AetherClaimTypes.UserName),
            headers.GetValueOrDefault(AetherClaimTypes.Name),
            headers.GetValueOrDefault(AetherClaimTypes.SurName),
            ParseRolesFromHeader(headers.GetValueOrDefault(AetherClaimTypes.Role)),
            headers.GetValueOrDefault(AetherClaimTypes.ActorUserId),
            headers.GetValueOrDefault(AetherClaimTypes.ActorSub),
            headers.GetValueOrDefault(AetherClaimTypes.ConsentId),
            headers.GetValueOrDefault(AetherClaimTypes.Position)));
    }

    /// <summary>
    /// Builds the claim header dictionary for the current user, to be merged into an outbound request so a
    /// downstream service resolves the same user. Empty values are omitted; <see cref="ICurrentUser.Roles"/>
    /// is joined with commas.
    /// </summary>
    /// <param name="currentUser">The current user.</param>
    /// <returns>A case-insensitive dictionary of claim headers.</returns>
    public static Dictionary<string, string?> ToForwardHeaders(this ICurrentUser currentUser)
    {
        Check.NotNull(currentUser, nameof(currentUser));

        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Add(headers, AetherClaimTypes.UserId, currentUser.Id);
        Add(headers, AetherClaimTypes.UserName, currentUser.UserName);
        Add(headers, AetherClaimTypes.Name, currentUser.Name);
        Add(headers, AetherClaimTypes.SurName, currentUser.Surname);
        if (currentUser.Roles is { Length: > 0 } roles)
        {
            headers[AetherClaimTypes.Role] = string.Join(",", roles);
        }

        Add(headers, AetherClaimTypes.ActorUserId, currentUser.ActorUserId);
        Add(headers, AetherClaimTypes.ActorSub, currentUser.ActorUserName);
        Add(headers, AetherClaimTypes.ConsentId, currentUser.ConsentId);
        Add(headers, AetherClaimTypes.Position, currentUser.Position);
        return headers;
    }

    /// <summary>
    /// Parses a <c>role</c> header value into role names. Comma and space are both accepted as separators,
    /// so <c>"maker,checker"</c>, <c>"maker checker"</c> and <c>"maker , checker"</c> all yield the same two
    /// roles. Returns null when the value carries no role.
    /// </summary>
    /// <param name="roleHeaderValue">The raw header value.</param>
    /// <returns>The role names, or null when there are none.</returns>
    public static string[]? ParseRolesFromHeader(string? roleHeaderValue)
    {
        if (string.IsNullOrWhiteSpace(roleHeaderValue))
        {
            return null;
        }

        var roles = roleHeaderValue
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToArray();

        return roles.Length == 0 ? null : roles;
    }

    private static void Add(Dictionary<string, string?> headers, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            headers[key] = value;
        }
    }
}
