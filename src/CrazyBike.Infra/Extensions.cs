using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;

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
        public static string ToWslFullPath(this string fullPath)
        {
            var split = fullPath.Split(":");
            var drive = split[0].ToLower();
            var path = split[1].Replace("\\", "/");
            return $"/mnt/{drive}{path}";
        }
        
        public static string DirToTar(this string sourceDirectory, string tgzFilename)
        {
            Stream outStream = File.Create(tgzFilename);
            Stream gzoStream = new GZipOutputStream(outStream);
            var tarArchive = TarArchive.CreateOutputTarArchive(gzoStream);

            // Note that the RootPath is currently case sensitive and must be forward slashes e.g. "c:/temp"
            // and must not end with a slash, otherwise cuts off first char of filename
            // This is scheduled for fix in next release
            tarArchive.RootPath = sourceDirectory.Replace('\\', '/');
            if (tarArchive.RootPath.EndsWith("/"))
                tarArchive.RootPath = tarArchive.RootPath.Remove(tarArchive.RootPath.Length - 1);

            tarArchive.AddDirectoryFilesToTar(sourceDirectory, true);

            tarArchive.Close();
           
            return tgzFilename;
        }
        static void AddDirectoryFilesToTar(this TarArchive tarArchive, string sourceDirectory, bool recurse)
        {
            // Optionally, write an entry for the directory itself.
            // Specify false for recursion here if we will add the directory's files individually.
            var tarEntry = TarEntry.CreateEntryFromFile(sourceDirectory);
            tarArchive.WriteEntry(tarEntry, false);

            // Write each file to the tar.
            var excludedDirectories = new[] { "bin", "obj", ".idea" };
            var excludedFiles = new[] {".DS_Store", "appsettings.secret.json", "appsettings.development.json", ".override.yml"};
            var files = Directory.GetFiles(sourceDirectory);
            foreach (var file in files)
            {
                if(excludedFiles.Any(x => file.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                    continue;
                
                tarEntry = TarEntry.CreateEntryFromFile(file);
                tarArchive.WriteEntry(tarEntry, true);
            }

            if (!recurse) 
                return;
            
            var directories = Directory.GetDirectories(sourceDirectory);
            foreach (var directory in directories)
            {
                var dirs = directory.Split('/', '\\');
                if(dirs.Intersect(excludedDirectories, StringComparer.OrdinalIgnoreCase).Any())
                    continue;
               
                AddDirectoryFilesToTar(tarArchive, directory, true);    
            }

            
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