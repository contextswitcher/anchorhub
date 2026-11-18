using System.Net;
using AnchorHub.Hub.Forwarding;
using AnchorHub.Hub.NativeMessaging;

#if WINDOWS
using AnchorHub.Hub.Cli;
#endif

#pragma warning disable CA1416 // Windows-only entry points are gated below

// --token-file=<path> is shared across --listen and --forward. Read once
// up front so we can fail-fast on a bad path before opening sockets.
var tokenFileArg = args.FirstOrDefault(a => a.StartsWith("--token-file=", StringComparison.Ordinal));
string? authToken = null;
if (tokenFileArg is not null)
{
    var path = tokenFileArg["--token-file=".Length..];
    try
    {
        authToken = File.ReadAllText(path).Trim();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"--token-file: cannot read {path}: {ex.Message}");
        return 1;
    }
    if (string.IsNullOrEmpty(authToken))
    {
        Console.Error.WriteLine($"--token-file: {path} is empty.");
        return 1;
    }
}

// --http-listen=host:port + --fulltext-token-file=<path> enable the
// loopback HTTP server used by JabRef's BrowserExtensionFulltextFetcher.
// The HTTP server runs alongside the native-messaging stdio loop in
// direct mode; it is not started in --listen or --forward modes.
var httpListenArg     = args.FirstOrDefault(a => a.StartsWith("--http-listen=",        StringComparison.Ordinal));
var fulltextTokenArg  = args.FirstOrDefault(a => a.StartsWith("--fulltext-token-file=", StringComparison.Ordinal));

IPEndPoint? httpListenEp = null;
if (httpListenArg is not null)
{
    var spec = httpListenArg["--http-listen=".Length..];
    if (!IPEndPoint.TryParse(spec, out var ep))
    {
        Console.Error.WriteLine($"Invalid --http-listen value: {spec} (expected host:port)");
        return 1;
    }
    httpListenEp = ep;
}

string? fulltextToken         = null;
string? fulltextTokenFilePath = null;
if (fulltextTokenArg is not null)
{
    fulltextTokenFilePath = Path.GetFullPath(fulltextTokenArg["--fulltext-token-file=".Length..]);
    try
    {
        fulltextToken = File.ReadAllText(fulltextTokenFilePath).Trim();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"--fulltext-token-file: cannot read {fulltextTokenFilePath}: {ex.Message}");
        return 1;
    }
    if (string.IsNullOrEmpty(fulltextToken))
    {
        Console.Error.WriteLine($"--fulltext-token-file: {fulltextTokenFilePath} is empty.");
        return 1;
    }
}

// --fulltext-token-file is only meaningful with --http-listen.
if (fulltextToken is not null && httpListenEp is null)
{
    Console.Error.WriteLine("--fulltext-token-file requires --http-listen=host:port.");
    return 1;
}

// Native messaging manifests cannot pass CLI args, so when ankd is
// spawned by the browser the explicit flags are absent. Fall back to a
// well-known default: if `%APPDATA%\AnchorHub\fulltext.token` exists,
// enable the fulltext realm on 127.0.0.1:17893 (overridable via the
// ANKD_HTTP_PORT environment variable). install.ps1 generates the token
// file at install time.
//
// Disabled for now: JabRef fulltext fetching is handled by the JabRef
// browser-extension plugin instead. Re-enable by defining
// JABREF_FULLTEXT_AUTOENABLE (<DefineConstants> in AnchorHub.Hub.csproj).
// Explicit --http-listen + --fulltext-token-file still work.
#if JABREF_FULLTEXT_AUTOENABLE
if (httpListenEp is null && fulltextToken is null)
{
    var defaultTokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AnchorHub", "fulltext.token");
    if (File.Exists(defaultTokenPath))
    {
        try
        {
            var token = File.ReadAllText(defaultTokenPath).Trim();
            if (!string.IsNullOrEmpty(token))
            {
                fulltextToken         = token;
                fulltextTokenFilePath = defaultTokenPath;

                var portEnv = Environment.GetEnvironmentVariable("ANKD_HTTP_PORT");
                int port = 17893;
                if (!string.IsNullOrEmpty(portEnv) && int.TryParse(portEnv, out var parsedPort) && parsedPort is > 0 and < 65_536)
                {
                    port = parsedPort;
                }
                httpListenEp = new IPEndPoint(IPAddress.Loopback, port);
            }
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Default fulltext token unreadable: {defaultTokenPath}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Default fulltext token unreadable: {defaultTokenPath}: {ex.Message}");
        }
    }
}
#else
// Drop a discovery file left by an earlier build so JabRef doesn't try a dead provider.
if (httpListenEp is null) AnchorHub.Hub.Http.JabRefProviderRegistration.Remove();
#endif


// --forward is available on both target frameworks: it has no Windows
// dependency, and Windows users with OneNote in a VM benefit from it
// the same way Linux users do.
var forwardArg = args.FirstOrDefault(a => a.StartsWith("--forward=", StringComparison.Ordinal));
if (forwardArg is not null)
{
    var spec = forwardArg["--forward=".Length..];
    if (!IPEndPoint.TryParse(spec, out var fwdEndpoint))
    {
        Console.Error.WriteLine($"Invalid --forward value: {spec} (expected host:port)");
        return 1;
    }
    return await ForwardingHost.RunAsync(fwdEndpoint, authToken);
}

#if WINDOWS

// --listen (OneNote COM bridge over TCP) is Windows-only: it needs the
// local COM backend.
var listenArg = args.FirstOrDefault(a => a.StartsWith("--listen=", StringComparison.Ordinal));
if (listenArg is not null)
{
    var spec = listenArg["--listen=".Length..];
    if (!IPEndPoint.TryParse(spec, out var listenEndpoint))
    {
        Console.Error.WriteLine($"Invalid --listen value: {spec} (expected host:port)");
        return 1;
    }
    using var listenBackend = new OneNoteClient();
    return await TcpListenerHost.RunAsync(listenEndpoint, listenBackend, authToken);
}

// The lookup / lookup-name / search-wsi CLI commands drive OneNote (and the
// outlook-* ones Outlook) over COM,
// so they are Windows-only too. Anything else (no args, flags only, or an
// unknown first token) falls through to the native-messaging host below.
var knownCmds = new HashSet<string> { "lookup", "lookup-name", "search-wsi", "outlook-capture", "outlook-open", "outlook-todos", "outlook-complete" };
if (args.Length > 0 &&
    !args[0].StartsWith("--", StringComparison.Ordinal) &&
    knownCmds.Contains(args[0]))
{
    try
    {
        return args[0] switch
        {
            "lookup"      => args.Length < 2
                                ? Commands.Unknown("lookup (missing url)")
                                : Commands.Lookup(args[1]),
            "lookup-name" => args.Length < 2
                                ? Commands.Unknown("lookup-name (missing name)")
                                : Commands.LookupName(string.Join(' ', args.Skip(1))),
            "search-wsi"  => args.Length < 2
                                ? Commands.Unknown("search-wsi (missing url)")
                                : Commands.SearchWsi(args[1]),
            "outlook-capture" => Commands.OutlookCapture(),
            "outlook-open" => args.Length < 2
                                ? Commands.Unknown("outlook-open (missing anchor URI)")
                                : Commands.OutlookOpen(args[1]),
            "outlook-todos" => Commands.OutlookTodos(),
            "outlook-complete" => args.Length < 2
                                ? Commands.Unknown("outlook-complete (missing anchor URI)")
                                : Commands.OutlookComplete(args[1]),
            _             => Commands.Unknown(args[0]),
        };
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 2;
    }
}

#endif

// Direct native-messaging mode. On Windows this spawns the OneNote COM
// bridge; on macOS/Linux the backend is null (OneNote actions return
// DISCONNECTED) and the host serves the JabRef fulltext realm plus Graph
// sign-in. The fulltext realm auto-enables from the default token file when
// no explicit --http-listen flag was passed (native-messaging manifests
// cannot carry args).
return await NativeMessagingHost.RunAsync(httpListenEp, fulltextToken, fulltextTokenFilePath);
