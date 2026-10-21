using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Blazorly.Harness.Web;

/// <summary>
/// Token gate for the console: every boot mints a fresh token (unless --token pins one)
/// and the startup URL carries it as ?token=. Loopback requests bypass the gate, so a
/// default localhost bind behaves exactly as before; anything arriving over the network
/// must present the token once (URL query, auth cookie, or Bearer header).
/// </summary>
public sealed class UiAccessGate
{
    public const string QueryParameter = "token";
    public const string CookieName = "blazorly_auth";
    public const string HeaderScheme = "Bearer";

    public string Token { get; }

    /// <summary>True when the token was pinned via --token rather than minted fresh.</summary>
    public bool Pinned { get; }

    public UiAccessGate(string? pinned)
    {
        if (string.IsNullOrWhiteSpace(pinned))
        {
            Token = Generate();
            Pinned = false;
        }
        else
        {
            Token = pinned.Trim();
            Pinned = true;
        }
    }

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return WebEncoders.Base64UrlEncode(bytes);
    }

    public bool IsAuthorized(HttpContext context)
    {
        // Localhost binds stay frictionless (and SSH tunnels count as local, which is
        // the point of the tunnel). Everything else must show the token.
        if (context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)) return true;
        if (context.Request.Query.TryGetValue(QueryParameter, out var query) && Matches(query)) return true;
        if (context.Request.Cookies.TryGetValue(CookieName, out var cookie) && Matches(cookie)) return true;
        var authorization = context.Request.Headers.Authorization.ToString();
        return authorization.StartsWith(HeaderScheme + " ", StringComparison.OrdinalIgnoreCase)
            && Matches(authorization[(HeaderScheme.Length + 1)..].Trim());
    }

    /// <summary>Session cookie so refreshes and in-app navigation survive without the query.</summary>
    public void IssueCookie(HttpContext context) => context.Response.Cookies.Append(CookieName, Token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Secure = context.Request.IsHttps,
        Path = "/",
    });

    public static string LockedPage { get; } =
        """
        <!doctype html><html><body style="font-family:sans-serif;max-width:42em;margin:4em auto;padding:0 1em">
        <h1>Blazorly console locked</h1>
        <p>Open the URL printed when the server started &mdash; it carries <code>?token=&hellip;</code>.
        Tokens rotate on every restart unless pinned with <code>--token</code>.</p>
        </body></html>
        """;

    private bool Matches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var presented = Encoding.UTF8.GetBytes(candidate.Trim());
        var expected = Encoding.UTF8.GetBytes(Token);
        return presented.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
