using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;

namespace CrazyBike.Infra
{
    public static class Extensions
    {
        public static string GenerateHash(this string context, IReadOnlyCollection<string> excludedDirectories, IReadOnlyCollection<string> excludedFiles)
        {
            excludedDirectories ??= new List<string>();
            excludedFiles ??= new List<string>();
            
            const string unixLineEnding = "\n";
            var allMd5Bytes = new List<byte>();
            
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
        
        public static string CalculateMD5(this string file)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(file);
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        
        public static string TarDirectory(this string sourceDirectory, string destinationArchiveFilePath,
            IReadOnlyCollection<string> excludedDirectories = default,
            IReadOnlyCollection<string> excludedFiles = default)
        {
            if (string.IsNullOrEmpty(sourceDirectory))
                throw new ArgumentNullException(nameof(sourceDirectory));
            if (string.IsNullOrEmpty(destinationArchiveFilePath))
                throw new ArgumentNullException(nameof(destinationArchiveFilePath));
            excludedDirectories ??= new List<string>();
            excludedFiles ??= new List<string>();
            
            Stream outStream = File.Create(destinationArchiveFilePath);
            Stream gzoStream = new GZipOutputStream(outStream);
            var tarArchive = TarArchive.CreateOutputTarArchive(gzoStream);

            // Note that the RootPath is currently case sensitive and must be forward slashes e.g. "c:/temp"
            // and must not end with a slash, otherwise cuts off first char of filename
            // This is scheduled for fix in next release
            tarArchive.RootPath = sourceDirectory.Replace('\\', '/');
            if (tarArchive.RootPath.EndsWith("/"))
                tarArchive.RootPath = tarArchive.RootPath.Remove(tarArchive.RootPath.Length - 1);

            tarArchive.AddDirectoryToTar(sourceDirectory, true, sourceDirectory, excludedDirectories, excludedFiles);

            tarArchive.Close();
           
            return destinationArchiveFilePath;
        }
        static void AddDirectoryToTar(this TarArchive tarArchive, string sourceDirectory, bool recurse,
            string relativeDirectoryContext,
            IReadOnlyCollection<string> excludedDirectories,
            IReadOnlyCollection<string> excludedFiles)
        {
            var files = Directory.GetFiles(sourceDirectory);
            foreach (var file in files)
            {
                if(excludedFiles.Any(x => file.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                    continue;
                
                var entryName = file.Split(Path.Join(relativeDirectoryContext, OperatingSystem.IsWindows() ? "\\" : "/"))[1];
                var tarEntry = TarEntry.CreateEntryFromFile(file);
                tarEntry.Name = entryName.Replace("\\", "/");
                tarArchive.WriteEntry(tarEntry, false);
            }

            if (!recurse) 
                return;
            
            var directories = Directory.GetDirectories(sourceDirectory);
            foreach (var directory in directories)
            {
                var dirs = directory.Split('/', '\\');
                if(dirs.Intersect(excludedDirectories, StringComparer.OrdinalIgnoreCase).Any())
                    continue;
                
                AddDirectoryToTar(tarArchive, directory, true, relativeDirectoryContext, excludedDirectories, excludedFiles);    
            }
        }
        public static string ZipDirectory(this string sourceDirectory, string destinationArchiveFilePath,
            List<string> excludedDirectories = default,
            List<string> excludedFiles = default
        )
        {
            if (string.IsNullOrEmpty(sourceDirectory))
                throw new ArgumentNullException(nameof(sourceDirectory));
            if (string.IsNullOrEmpty(destinationArchiveFilePath))
                throw new ArgumentNullException(nameof(destinationArchiveFilePath));
            excludedDirectories ??= new List<string>();
            excludedFiles ??= new List<string>();
            
            if(File.Exists(destinationArchiveFilePath))
                File.Delete(destinationArchiveFilePath);
            
            using var zipFileStream = new FileStream(destinationArchiveFilePath, FileMode.Create);
            using var archive = new ZipArchive(zipFileStream, ZipArchiveMode.Create);
            
            archive.AddDirectoryToZip(sourceDirectory, true, sourceDirectory, excludedDirectories, excludedFiles);
            
            return destinationArchiveFilePath;
        }
        static void AddDirectoryToZip(this ZipArchive archive, string sourceDirectory, bool recurse,
            string relativeDirectoryContext,
            IReadOnlyCollection<string> excludedDirectories,
            IReadOnlyCollection<string> excludedFiles)
        {
            var files = Directory.GetFiles(sourceDirectory);
            foreach (var file in files)
            {
                if(excludedFiles.Any(x => file.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var entryName = file.Split(Path.Join(relativeDirectoryContext, OperatingSystem.IsWindows() ? "\\" : "/"))[1];
                archive.CreateEntryFromFile(file, entryName, CompressionLevel.Fastest);
            }

            if (!recurse) 
                return;
            
            var directories = Directory.GetDirectories(sourceDirectory);
            foreach (var directory in directories)
            {
                var dirs = directory.Split('/', '\\');
                if(dirs.Intersect(excludedDirectories, StringComparer.OrdinalIgnoreCase).Any())
                    continue;
               
                AddDirectoryToZip(archive, directory, true, relativeDirectoryContext, excludedDirectories, excludedFiles);    
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