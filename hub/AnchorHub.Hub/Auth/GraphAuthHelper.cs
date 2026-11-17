using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace AnchorHub.Hub.Auth;

/// <summary>
/// Microsoft identity-platform sign-in built on MSAL. MSAL owns the
/// interactive flow end-to-end:
/// <list type="bullet">
/// <item>spawns the system browser</item>
/// <item>binds a loopback HTTP listener on a free port and registers
///       redirect URI <c>http://localhost</c></item>
/// <item>catches the authorization-code callback</item>
/// <item>exchanges code → tokens with PKCE</item>
/// <item>persists the refresh token in a DPAPI-encrypted cache under
///       <c>%APPDATA%\AnchorHub\msal-cache.bin</c></item>
/// </list>
/// The Azure AD app registration only needs <c>http://localhost</c>
/// listed once as a "Mobile and desktop applications" redirect URI —
/// MSAL picks an ephemeral port at each sign-in and Azure's loopback
/// exception (RFC 8252 §7.3) allows the dynamic port.
/// </summary>
internal static class GraphAuthHelper
{
    private static readonly string[] Scopes =
        ["Notes.Read", "offline_access"];

    public sealed record TokenResult(
        string  AccessToken,
        string? RefreshToken,
        int     ExpiresIn,
        string? IdToken,
        string? Account);

    public static async Task<TokenResult> SignInAsync(
        string clientId,
        string tenant,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId)) throw new ArgumentException("clientId required.", nameof(clientId));
        if (string.IsNullOrEmpty(tenant))   tenant = "common";

        var app = await BuildAppAsync(clientId, tenant);
        var result = await AcquireTokenInteractiveOrSilentAsync(app, ct);
        return Map(result);
    }

    /// <summary>
    /// Silent-only access-token acquire — used by per-action calls
    /// (e.g. graphLookup) so the daemon never pops a browser window
    /// unexpectedly. Returns null when there's no cached account or
    /// the cache is stale; callers should surface a "sign in again"
    /// error rather than retry.
    /// </summary>
    public static async Task<TokenResult?> GetSilentTokenAsync(
        string clientId,
        string tenant,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId)) throw new ArgumentException("clientId required.", nameof(clientId));
        if (string.IsNullOrEmpty(tenant))   tenant = "common";

        var app      = await BuildAppAsync(clientId, tenant);
        var accounts = await app.GetAccountsAsync();
        var account  = accounts.FirstOrDefault();
        if (account is null) return null;

        try
        {
            var result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
            return Map(result);
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    private static TokenResult Map(AuthenticationResult result)
    {

        // expires_in from AuthenticationResult is computed from ExpiresOn.
        var expiresInSeconds = (int)Math.Max(60, (result.ExpiresOn - DateTimeOffset.UtcNow).TotalSeconds);

        return new TokenResult(
            AccessToken:  result.AccessToken,
            // MSAL doesn't expose the refresh token directly — it owns the
            // refresh cycle through its cache. Surfacing null lets the
            // extension know to re-call signIn() (which becomes silent
            // because MSAL's cache already holds the refresh material).
            RefreshToken: null,
            ExpiresIn:    expiresInSeconds,
            IdToken:      result.IdToken,
            Account:      result.Account?.Username);
    }

    private static async Task<IPublicClientApplication> BuildAppAsync(string clientId, string tenant)
    {
        var app = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenant)
            // Loopback redirect — MSAL picks an ephemeral port; Azure's
            // "Mobile and desktop applications" platform must list
            // http://localhost (literal, no port) as a redirect URI.
            .WithRedirectUri("http://localhost")
            .Build();

        await RegisterTokenCacheAsync(app);
        return app;
    }

    private static async Task RegisterTokenCacheAsync(IPublicClientApplication app)
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AnchorHub");
        Directory.CreateDirectory(cacheDir);

        // DPAPI is the default Windows backing for MsalCacheHelper. On
        // Linux the same class falls back to libsecret / a plaintext
        // file with a clear warning — fine for the future Linux daemon.
        var storageProps = new StorageCreationPropertiesBuilder("msal-cache.bin", cacheDir)
            .Build();

        var helper = await MsalCacheHelper.CreateAsync(storageProps);
        helper.RegisterCache(app.UserTokenCache);
    }

    private static async Task<AuthenticationResult> AcquireTokenInteractiveOrSilentAsync(IPublicClientApplication app, CancellationToken ct)
    {
        // Silent path: succeeds when MSAL's cache still has a usable
        // refresh token, which is the common case after the first
        // interactive sign-in.
        var accounts = await app.GetAccountsAsync();
        var account  = accounts.FirstOrDefault();
        if (account is not null)
        {
            try
            {
                return await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
            }
            catch (MsalUiRequiredException)
            {
                // Cache is stale — drop through to interactive.
            }
        }

        return await app
            .AcquireTokenInteractive(Scopes)
            .WithUseEmbeddedWebView(false)   // OS browser so SSO / FIDO2 / etc. work
            .ExecuteAsync(ct);
    }
}
