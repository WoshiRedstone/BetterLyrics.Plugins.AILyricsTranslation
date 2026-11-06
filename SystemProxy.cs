using System.Net;

namespace BetterLyrics.Plugins.AILyricsTranslation;

/// <summary>
/// Wraps the machine's proxy so that requests to loopback never go through it.
///
/// Windows answers a loopback HTTP request that it cannot route with the same status a reachable
/// server would send back, and a system proxy that was configured without a local bypass entry
/// (some accelerators overwrite the WinINET override list, dropping "&lt;local&gt;") will happily
/// swallow "http://127.0.0.1:port/..." and reply "404". Users who point ApiUrl at a local mirror,
/// and the test harness that fakes every backend on 127.0.0.1, therefore must talk to the socket
/// directly. Everything else keeps using the configured proxy, which many users need to reach the
/// public API at all.
/// </summary>
internal sealed class LoopbackBypassProxy(IWebProxy inner) : IWebProxy
{
    public ICredentials? Credentials
    {
        get => inner.Credentials;
        set => inner.Credentials = value;
    }

    public Uri? GetProxy(Uri destination) =>
        IsLoopback(destination) ? null : inner.GetProxy(destination);

    public bool IsBypassed(Uri host) =>
        IsLoopback(host) || inner.IsBypassed(host);

    /// <summary>
    /// "127.0.0.0/8", "::1" and "localhost" all report IsLoopback, which is what local mirrors and
    /// the harness use. Host names that merely resolve to loopback ("my-mirror.local") are not
    /// covered: resolving a name here would block the caller and hide a wrong ApiUrl.
    /// </summary>
    internal static bool IsLoopback(Uri uri) => uri.IsLoopback;
}
