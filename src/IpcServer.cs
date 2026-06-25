namespace Otter;

using System.IO.Pipes;

/// <summary>
/// A tiny per-user named-pipe channel used to ferry the OAuth callback URL from a transient second
/// Otter instance to the running one.
///
/// When the browser follows Slack's <c>otter://callback?...</c> redirect, Windows launches a fresh
/// Otter process with that URL as its argument. The single-instance mutex in <see cref="Program"/>
/// stops it becoming a second tray app; instead it writes the URL here and exits, and the primary
/// instance — which is sitting in <see cref="SlackClient.RunOAuthFlowAsync"/> awaiting it — picks it up.
/// </summary>
static class IpcServer
{
    const string PipeName = "Otter_OAuthCallback";

    /// <summary>Starts the background listener on the primary instance. Each delivered URL is passed
    /// to <paramref name="onUrl"/> (on a background thread).</summary>
    public static void Start(Action<string> onUrl)
    {
        var thread = new Thread(() => Loop(onUrl)) { IsBackground = true, Name = "Otter-IPC" };
        thread.Start();
    }

    static void Loop(Action<string> onUrl)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                server.WaitForConnection();

                using var reader = new StreamReader(server);
                var url = reader.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(url)) onUrl(url.Trim());
            }
            catch { /* a single failed connection shouldn't kill the listener — loop and wait again */ }
        }
    }

    /// <summary>Called by a second instance to hand its callback URL to the primary one. Returns false
    /// if no primary instance is listening (in which case the caller should fall back to a normal launch).</summary>
    public static bool TrySend(string url)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.Write(url);
            writer.Flush();
            return true;
        }
        catch { return false; }
    }
}
