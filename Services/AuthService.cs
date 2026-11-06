using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Serialization;
using CmlLib.Core.Auth.Microsoft.Sessions;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Accounts.JsonStorage;
using XboxAuthNet.Game.OAuth;
using XboxAuthNet.Game.Serialization;
using XboxAuthNet.Game.XboxAuth;

namespace Zyrox.Services;

public record AccountInfo(string Id, string Name);

public class AuthService
{
    private const string Scopes = "XboxLive.signin offline_access";

    private readonly JELoginHandler _handler;
    private readonly JsonXboxGameAccountManager _accounts;

    private static readonly string AzureClientId = "";

    public AuthService()
    {
        _accounts = new JsonXboxGameAccountManager(
            new ProtectedJsonStorage(AppPaths.Accounts),
            JEGameAccount.FromSessionStorage,
            new JsonSerializerOptions(JsonXboxGameAccountManager.DefaultSerializerOption)
            {
                TypeInfoResolver = JsonTypeInfoResolver.Combine(MicrosoftAuthJsonContext.Default, XboxGameJsonContext.Default)
            });

        _accounts.GetAccounts();

        var builder = new JELoginHandlerBuilder()
            .WithAccountManager(_accounts)
            .WithXboxAuthProvider(new BasicXboxProvider(JELoginHandler.RelyingParty));
        if (AzureClientId.Length > 0)
            builder.WithOAuthProvider(new MicrosoftOAuthCodeFlowProvider(new MicrosoftOAuthClientInfo(AzureClientId, Scopes)));
        _handler = builder.Build();
    }

    public static bool HasSavedAccount => File.Exists(AppPaths.Accounts);

    public async Task<MSession> LoginAsync(CancellationToken cancellationToken = default) =>
        AsMicrosoftSession(await _handler.AuthenticateInteractively(cancellationToken));

    public async Task<MSession> RefreshAsync(string? accountId, CancellationToken cancellationToken = default)
    {
        var account = FindAccount(accountId) ?? _accounts.GetDefaultAccount();
        return AsMicrosoftSession(await _handler.AuthenticateSilently(account, cancellationToken));
    }

    public List<AccountInfo> GetAccounts() => _accounts.GetAccounts()
        .OfType<JEGameAccount>()
        .Where(a => !string.IsNullOrEmpty(a.Identifier))
        .Select(a => new AccountInfo(a.Identifier!, a.Profile?.Username ?? a.Identifier!))
        .ToList();

    private IXboxGameAccount? FindAccount(string? accountId) =>
        string.IsNullOrEmpty(accountId) ? null : _accounts.GetAccounts().FirstOrDefault(a => a.Identifier == accountId);

    private static MSession AsMicrosoftSession(MSession session)
    {
        session.UserType = "msa";
        return session;
    }

    public async Task RemoveAccountAsync(string? accountId)
    {
        var account = FindAccount(accountId);
        if (account != null)
            await _handler.Signout(account);
        _accounts.SaveAccounts();
        if (GetAccounts().Count == 0)
            File.Delete(AppPaths.Accounts);
    }

    public static string Describe(Exception ex)
    {
        var text = ex.ToString();
        if (ex is JEAuthException { StatusCode: 403 } || text.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
            return "Your Azure app is not approved for the Minecraft API yet.";
        if (ex is JEAuthException { StatusCode: 404 })
            return "This Microsoft account does not own Minecraft: Java Edition.";
        if (text.Contains("2148916233"))
            return "This Microsoft account has no Xbox profile yet. Sign in once at xbox.com.";
        if (text.Contains("2148916238"))
            return "This is a child account and must be added to an Xbox family first.";
        if (text.Contains("unauthorized_client") || text.Contains("invalid_client") || text.Contains("invalid_request"))
            return "Microsoft rejected the app. Check the client ID, redirect URI and account type in Azure.";
        return ex.Message;
    }
}

internal class ProtectedJsonStorage(string path) : IJsonStorage
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Zyrox.Accounts.v1");

    public JsonNode? ReadAsJsonNode()
    {
        try
        {
            if (File.Exists(path))
            {
                var data = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                return JsonNode.Parse(data) ?? new JsonObject();
            }
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException)
        {
        }
        return new JsonObject();
    }

    public void Write(JsonNode node, JsonSerializerOptions? serializerOptions)
    {
        var data = Encoding.UTF8.GetBytes(node.ToJsonString(serializerOptions));
        AtomicFile.WriteAllBytes(path, ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
    }
}
