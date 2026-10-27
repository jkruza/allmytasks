using System.Net.Sockets;
using System.Text;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.Google;

/// <summary>Receives the browser's redirect to <c>http://127.0.0.1:{port}/</c> on a loopback listener.</summary>
internal static class LoopbackReceiver
{
    internal const string Page = "<!doctype html><meta charset=\"utf-8\"><title>All My Tasks</title>"
        + "<body style=\"font-family:sans-serif;margin:3em\"><h2>You can close this tab</h2><p>Return to All My Tasks.</p></body>";

    public static async Task<string> WaitForCodeAsync(TcpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var query = await ReadQueryAsync(client, cancellationToken).ConfigureAwait(false);

            // Requests without a result (a favicon, a probe) are answered and ignored; so is anything with the wrong state.
            if (query is null || query.GetValueOrDefault("state") != expectedState)
            {
                await RespondAsync(client, "404 Not Found", string.Empty, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await RespondAsync(client, "200 OK", Page, cancellationToken).ConfigureAwait(false);
            if (query.GetValueOrDefault("code") is { Length: > 0 } code)
            {
                return code;
            }

            var error = query.GetValueOrDefault("error");
            throw new SourceAuthException(error is not null
                ? "Google sign-in was cancelled, declined or blocked (a Workspace administrator can block third-party apps). Edit this source and save it to try again."
                : "Google sign-in did not return a result.")
            {
                Diagnostics = error is { Length: > 0 and <= 64 } && error.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? $"Authorization error: {error}" : null,
            };
        }
    }

    private static async Task<Dictionary<string, string>?> ReadQueryAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var length = 0;
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stream = client.GetStream();
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), readTimeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (buffer.AsSpan(0, length).IndexOf("\r\n"u8) >= 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var line = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n", 2)[0].Split(' ');
        var q = line.Length > 1 ? line[1].IndexOf('?') : -1;
        if (line.Length < 2 || line[0] != "GET" || q < 0)
        {
            return null;
        }

        var result = new Dictionary<string, string>();
        foreach (var pair in line[1][(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
        }

        return result;
    }

    private static async Task RespondAsync(TcpClient client, string status, string body, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n{body}");
            await client.GetStream().WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The browser went away; the result has already been read.
        }
    }
}
