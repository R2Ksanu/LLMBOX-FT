using System;
using System.IO;
using System.Text;

namespace LlmodelBox.Common
{
    internal static class AtomicFile
    {
        // Stage beside the destination so replacement stays on the same filesystem.
        public static void WriteAllText(string path, string contents)
        {
            string destination = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(contents);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
