using System.Net.Sockets;
using CSVReader;
using Renci.SshNet.Common;

namespace FintechEtl.FunctionApp;

internal static class SftpSessionRecovery
{
    // SSH IsConnected can remain true after its SFTP subsystem channel closes.
    // Restrict this exception classification to the SSH.NET call stack; unrelated
    // application InvalidOperationExceptions must still raise alerts.
    internal static bool IsClosedSession(Exception error) =>
        error is InvalidOperationException && error.Message == "The session is not open."
        && (error.StackTrace?.Contains("Renci.SshNet.", StringComparison.Ordinal) == true
            || error.TargetSite?.DeclaringType?.Namespace?.StartsWith("Renci.SshNet", StringComparison.Ordinal) == true);

    internal static bool IsTransient(Exception error) => IsClosedSession(error)
        || error is SshConnectionException or SshOperationTimeoutException or SocketException or IOException;

    internal static void EnsureReady(Func<bool> connected, Action connect, Action probe, Action disconnect,
        Action<int, Exception> retry = null, Action<int> delay = null)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!connected()) connect();
                probe(); // A real SFTP operation, not just the SSH transport flag.
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                try { disconnect(); } catch { /* The failed connection may already be closed. */ }
                if (attempt == 3)
                    throw new RetryableEtlException("SFTP session could not be restored after three attempts; unfinished input remains for retry.", ex);
                retry?.Invoke(attempt, ex);
                if (delay != null) delay(attempt);
                else Thread.Sleep(TimeSpan.FromSeconds(attempt));
            }
        }
    }
}
