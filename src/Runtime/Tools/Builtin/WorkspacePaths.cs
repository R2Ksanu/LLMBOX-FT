using System;
using System.IO;

namespace LlmodelBox.Runtime.Tools.Builtin
{
    // Shared path-safety for filesystem tools. Every path a tool receives is resolved against a fixed
    // workspace root and rejected if it escapes it — the single defense against path traversal
    // (../, absolute paths, symlink-style escapes). Tools that skip this must not touch the disk.
    internal static class WorkspacePaths
    {
        // Resolves a caller-supplied relative path to a full path guaranteed to sit inside root.
        // Throws for empty input or any path that escapes the workspace.
        public static string ResolveInside(string root, string? relative)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("No workspace root configured.");
            if (string.IsNullOrWhiteSpace(relative))
                throw new ArgumentException("A 'path' argument is required.");

            if (relative.Contains(':'))
                throw new UnauthorizedAccessException("Use a relative path without drive prefixes or alternate data streams.");
            string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            // Combine then normalize; Path.Combine ignores fullRoot if 'relative' is absolute, so a
            // full re-check against the root below still catches absolute-path escapes.
            string combined = Path.GetFullPath(Path.Combine(fullRoot, relative));

            string rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            bool inside = combined.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                          || combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
            if (!inside)
                throw new UnauthorizedAccessException($"Path '{relative}' resolves outside the workspace.");

            // Reject links at every existing component, including ancestors of the workspace.
            // This check assumes other processes do not replace components during the operation.
            for (string? current = combined; current != null; current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new UnauthorizedAccessException("Linked files and directories are not allowed by workspace tools.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return combined;
        }
    }
}
