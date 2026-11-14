using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace SignagePlayer;

sealed record HttpResult(int Code, string Body);

/// <summary>Minimal HTTPS client for the device API. Screens only ever make outgoing requests.</summary>
static class Net
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseCookies = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    static Net()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("signage-player/" + Config.Version);
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public static async Task<HttpResult> Call(HttpMethod method, string path, string? token,
        IDictionary<string, string>? headers, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, Config.ServerUrl + path);
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (headers != null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        if (method == HttpMethod.Post) req.Content = new ByteArrayContent(Array.Empty<byte>());
        using var res = await Http.SendAsync(req, ct);
        return new HttpResult((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Downloads to a .part file and renames, so a half-finished download is never shown.</summary>
    public static async Task Download(string path, string token, string dest, CancellationToken ct = default)
    {
        var tmp = dest + ".part";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Config.ServerUrl + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));   // a 60 MB video on a slow line
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if ((int)res.StatusCode != 200) throw new IOException($"HTTP {(int)res.StatusCode} for {path}");
            var expected = res.Content.Headers.ContentLength ?? -1;
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await res.Content.CopyToAsync(fs, cts.Token);
            }
            var got = new FileInfo(tmp).Length;
            if (got == 0 || (expected > 0 && got != expected)) throw new IOException($"Incomplete download for {path}");
            File.Move(tmp, dest, overwrite: true);
        }
        finally
        {
            Store.TryDelete(tmp);
        }
    }
}
