using System.IO;
using System.Text;

namespace ConnectionClue.App;

internal static class ReportFile
{
    public static async Task WriteAsync(string path, string content, Encoding encoding, CancellationToken cancellationToken)
    {
        string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetRandomFileName());
        var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        try
        {
            await using (file)
            await using (var writer = new StreamWriter(file, encoding, leaveOpen: true))
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
