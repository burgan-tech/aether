using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Aether.Users;
using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.Security;

/// <summary>
/// Reads Aether claim headers off an <see cref="HttpRequest"/>.
/// <para>
/// The counterpart of <see cref="CurrentUserHeaderExtensions"/>, which works over a plain dictionary:
/// capture the claim headers here on the way in, then restore the user later — in a background job or a
/// resumed workflow — with <c>ICurrentUser.ChangeFromHeaders</c>.
/// </para>
/// </summary>
public static class HttpRequestCurrentUserExtensions
{
    /// <summary>
    /// Gets a single claim header value, or null when the request does not carry it.
    /// </summary>
    /// <param name="request">The HTTP request.</param>
    /// <param name="claimType">The header name — use an <see cref="AetherClaimTypes"/> value.</param>
    /// <returns>The header value, or null when absent or empty.</returns>
    public static string? GetClaimHeader(this HttpRequest request, string claimType)
    {
        Check.NotNull(request, nameof(request));
        Check.NotNullOrWhiteSpace(claimType, nameof(claimType));

        var value = request.Headers[claimType].FirstOrDefault();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Collects the Aether claim headers the request carries into a dictionary shaped for
    /// <c>ICurrentUser.ChangeFromHeaders</c>. Absent headers are omitted.
    /// </summary>
    /// <param name="request">The HTTP request.</param>
    /// <returns>A case-insensitive dictionary of the claim headers present on the request.</returns>
    public static Dictionary<string, string?> GetCurrentUserHeaders(this HttpRequest request)
    {
        Check.NotNull(request, nameof(request));

        // Read the claim type names on every call: AetherClaimTypes members are settable, so a host may
        // rename a header at startup and a cached list would keep the old name.
        string[] claimTypes =
        [
            AetherClaimTypes.UserId,
            AetherClaimTypes.UserName,
            AetherClaimTypes.Name,
            AetherClaimTypes.SurName,
            AetherClaimTypes.Role,
            AetherClaimTypes.Position,
            AetherClaimTypes.ActorUserId,
            AetherClaimTypes.ActorSub,
            AetherClaimTypes.ConsentId
        ];

        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var claimType in claimTypes)
        {
            var value = request.GetClaimHeader(claimType);
            if (value != null)
            {
                headers[claimType] = value;
            }
        }

        return headers;
    }
}
