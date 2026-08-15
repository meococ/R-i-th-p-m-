using System.IO;
using System.Text;

namespace BIM.DatViet.Infrastructure;

internal static class AbutmentDiagnostics
{
    private static readonly object Sync = new();

    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BIM-DatViet",
        "Logs",
        "abutment-rebar.log");

    internal static void LogMessage(string scope, string message)
    {
        try
        {
            var path = LogPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var entry = new StringBuilder()
                .AppendLine("------------------------------------------------------------")
                .AppendLine($"UTC: {DateTime.UtcNow:O}")
                .AppendLine($"Scope: {scope}")
                .AppendLine(message)
                .ToString();
            lock (Sync) File.AppendAllText(path, entry, Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never become a second failure path inside Revit.
        }
    }

    internal static void LogException(string scope, Exception exception)
    {
        try
        {
            var path = LogPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var entry = new StringBuilder()
                .AppendLine("------------------------------------------------------------")
                .AppendLine($"UTC: {DateTime.UtcNow:O}")
                .AppendLine($"Scope: {scope}")
                .AppendLine(exception.ToString())
                .ToString();
            lock (Sync) File.AppendAllText(path, entry, Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never become a second failure path inside Revit.
        }
    }
}
