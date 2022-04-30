using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CrazyBike.Infra
{
    public static class Extensions
    {
        public static string GenerateHash(this string context)
        {
            const string unixLineEnding = "\n";
            var allMd5Bytes = new List<byte>();
            var excludedDirectories = new[] { "bin", "obj", ".idea" };
            var excludedFiles = new[] {".DS_Store", "appsettings.secret.json", "appsettings.development.json", ".override.yml"};
            var files = Directory.GetFiles(context, "*", SearchOption.AllDirectories);
            foreach (var fileName in files)
            {
                if(excludedFiles.Any(x => fileName.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                    continue;
                
                var fileInfo = new FileInfo(fileName);
                var dir = fileInfo.Directory;
                if (dir != null)
                {
                    var dirs = dir.FullName.Split('/', '\\');
                    if(dirs.Intersect(excludedDirectories, StringComparer.OrdinalIgnoreCase).Any())
                        continue;
                }

                var lines = File.ReadAllLines(fileName).Select(l => l.NormalizeLineEndings(unixLineEnding));
                var md5Bytes = System.Text.Encoding.UTF8.GetBytes(string.Join(unixLineEnding, lines));
                using var md5 = MD5.Create();
                //using var stream = File.OpenRead(fileName);
                //var md5Bytes = md5.ComputeHash(stream);
                allMd5Bytes.AddRange(md5Bytes);
            }

            using var hash = MD5.Create();
            var md5AllBytes = hash.ComputeHash(allMd5Bytes.ToArray());
            var result = BytesToHash(md5AllBytes);
            
            return result;
        }
        static string BytesToHash(this IEnumerable<byte> md5Bytes) => string.Join("", md5Bytes.Select(ba => ba.ToString("x2")));
        
        static string NormalizeLineEndings(this string line, string targetLineEnding = null)
        {
            if (string.IsNullOrEmpty(line))
                return line;

            targetLineEnding ??= Environment.NewLine;

            const string unixLineEnding = "\n";
            const string windowsLineEnding = "\r\n";
            const string macLineEnding = "\r";

            if (targetLineEnding != unixLineEnding && targetLineEnding != windowsLineEnding &&
                targetLineEnding != macLineEnding)
            {
                throw new ArgumentOutOfRangeException(nameof(targetLineEnding),
                    "Unknown target line ending character(s).");
            }

            line = line
                .Replace(windowsLineEnding, unixLineEnding)
                .Replace(macLineEnding, unixLineEnding);

            if (targetLineEnding != unixLineEnding)
            {
                line = line.Replace(unixLineEnding, targetLineEnding);
            }

            return line;
        }
    }

    public static class OperatingSystem
    {
        public static bool IsWindows() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static bool IsMacOS() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        public static bool IsLinux() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    }
}