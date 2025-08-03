using ICSharpCode.SharpZipLib.Zip;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class LightvnExtractor : BaseExtractor
    {
        private static readonly Dictionary<string, byte[][]> FileTypeSignatures = new Dictionary<string, byte[][]>
        {
            { ".ogg", new[] { new byte[] { 0x4F, 0x67, 0x67, 0x53 } } },
            { ".png", new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47 } } },
            { ".jpg", new[] { new byte[] { 0xFF, 0xD8, 0xFF }, new byte[] { 0xFF, 0xD9 } } },
            { ".mpg", new[] { new byte[] { 0x00, 0x00, 0x01, 0xBA } } },
            { ".webp", new[] {
                new byte[] { 0x52, 0x49, 0x46, 0x46 }, // RIFF header
                new byte[] { 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38 } // VP8 chunk
            } },
            { ".wav", new[] {
                new byte[] { 0x52, 0x49, 0x46, 0x46 }, // RIFF header
                new byte[] { 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74 } // WAVEfmt chunk (at offset 8)
            } },
            { ".txt", new[] { new byte[] { 0x0D, 0x0A }, new byte[] { 0x0D, 0x0A } } },
            { ".mp4", new[] {
                new byte[] { 0x66, 0x74, 0x79, 0x70 }, // ftyp box
                new byte[] { 0x6D, 0x76, 0x68, 0x64 }  // mvhd brand (common in MP4 files)
            } },
        };

        static readonly byte[] PKZIP = { 0x50, 0x4B, 0x03, 0x04 };
        static readonly byte[] KEY = { 0x64, 0x36, 0x63, 0x35, 0x66, 0x4B, 0x49, 0x33, 0x47, 0x67, 0x42, 0x57, 0x70, 0x5A, 0x46, 0x33, 0x54, 0x7A, 0x36, 0x69, 0x61, 0x33, 0x6B, 0x46, 0x30 };
        static readonly byte[] REVERSED_KEY = { 0x30, 0x46, 0x6B, 0x33, 0x61, 0x69, 0x36, 0x7A, 0x54, 0x33, 0x46, 0x5A, 0x70, 0x57, 0x42, 0x67, 0x47, 0x33, 0x49, 0x4B, 0x66, 0x35, 0x63, 0x36, 0x64 };

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            string zipPassword = Encoding.UTF8.GetString(KEY);
            var files = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).ToList();
            TotalFilesToExtract = files.Count;

            string extractedFolder = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedFolder);

            var extractedFiles = new ConcurrentBag<string>();

            try
            {
                await Task.Run(() =>
                {
                    Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 }, filePath =>
                    {
                        ThrowIfCancellationRequested(cancellationToken);
                        try
                        {
                            if (File.Exists(filePath))
                            {
                                if (IsVndat(filePath))
                                {
                                    UnpackVndat(filePath, extractedFolder, zipPassword, extractedFiles);
                                }
                                else if (Path.GetExtension(filePath).Contains("mcdat"))
                                {
                                    Console.WriteLine($"解密文件中 {filePath}...");
                                    string outputPath = GenerateUniquePath(extractedFolder, Path.GetFileNameWithoutExtension(filePath), ".dec");
                                    XOR(filePath, outputPath);
                                    extractedFiles.Add(outputPath);
                                    OnFileExtracted(outputPath);
                                }
                                else if (Path.GetExtension(filePath).Contains("dec"))
                                {
                                    Console.WriteLine($"加密文件中 {filePath}...");
                                    string outputPath = GenerateUniquePath(extractedFolder, Path.GetFileNameWithoutExtension(filePath), ".enc");
                                    XOR(filePath, outputPath);
                                    extractedFiles.Add(outputPath);
                                    OnFileExtracted(outputPath);
                                }
                                else
                                {
                                    Console.WriteLine($"不支持的文件类型! {filePath}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            OnExtractionFailed($"处理文件 {filePath} 时出错: {ex.Message}");
                        }
                    });
                }, cancellationToken);

                Console.WriteLine("开始扫描并重命名文件...");
                ScanAndRenameFiles(extractedFolder);
                Console.WriteLine("文件扫描和重命名完成。");

                Console.WriteLine("开始按文件类型分类...");
                ClassifyFilesByExtension(extractedFolder);
                Console.WriteLine("文件分类完成。");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("提取操作已取消。");
            }

            OnExtractionCompleted();
        }

        private string GenerateUniquePath(string directory, string baseName, string extension)
        {
            string fullPath;
            int counter = 1;

            fullPath = Path.Combine(directory, $"{baseName}{extension}");

            while (File.Exists(fullPath))
            {
                fullPath = Path.Combine(directory, $"{baseName}_{counter}{extension}");
                counter++;
            }

            return fullPath;
        }

        private void UnpackVndat(string vndatFile, string outputFolder, string password, ConcurrentBag<string> extractedFiles)
        {
            bool usePassword = IsPasswordProtectedZip(vndatFile);

            using var zipFile = new ZipFile(vndatFile);

            if (usePassword)
            {
                Console.WriteLine($"{Path.GetFileName(vndatFile)} 受密码保护,使用`{password}` 作为密码。");
                zipFile.Password = password;
            }

            if (zipFile.Count > 0)
            {
                Console.WriteLine($"正在解压 {Path.GetFileName(vndatFile)}...");

                foreach (ZipEntry entry in zipFile)
                {
                    if (!entry.IsDirectory)
                    {
                        try
                        {
                            Console.WriteLine($"正在写入 {entry.Name}...");

                            using Stream inputStream = zipFile.GetInputStream(entry);
                            using MemoryStream ms = new MemoryStream();
                            inputStream.CopyTo(ms);
                            byte[] fileData = ms.ToArray();

                            string fileName = Path.GetFileNameWithoutExtension(entry.Name);

                            string tempPath = GenerateUniquePath(outputFolder, fileName, ".temp");

                            using FileStream outputStream = File.Create(tempPath);
                            outputStream.Write(fileData, 0, fileData.Length);

                            if (extractedFiles != null)
                            {
                                extractedFiles.Add(tempPath);
                                OnFileExtracted(tempPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"写入 {entry.Name} 失败! {ex.Message}");
                        }
                    }
                }

                Console.WriteLine("完成。");
            }

            if (!usePassword)
            {
                string[] files = GetFilesRecursive(outputFolder);

                if (files.Length > 0)
                {
                    foreach (string file in files)
                    {
                        Console.WriteLine($"正在XOR加密{file}...");
                        XOR(file);
                    }

                    Console.WriteLine("完成。");
                }
            }
        }

        private void ScanAndRenameFiles(string directory)
        {
            try
            {
                string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
                int totalFiles = files.Length;
                int processed = 0;

                Console.WriteLine($"找到 {totalFiles} 个文件需要扫描...");

                foreach (string filePath in files)
                {
                    try
                    {
                        byte[] fileData = File.ReadAllBytes(filePath);
                        string detectedExtension = DetectFileExtension(fileData);

                        string currentExtension = Path.GetExtension(filePath);
                        if (currentExtension != detectedExtension)
                        {
                            string newPath = Path.ChangeExtension(filePath, detectedExtension);

                            newPath = MakeUniqueFileName(newPath);

                            File.Move(filePath, newPath);
                            Console.WriteLine($"重命名: {Path.GetFileName(filePath)} -> {Path.GetFileName(newPath)}");
                        }

                        processed++;
                        if (processed % 100 == 0)
                        {
                            Console.WriteLine($"已处理: {processed}/{totalFiles}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"处理文件 {filePath} 时出错: {ex.Message}");
                    }
                }

                Console.WriteLine($"扫描完成: 共处理 {processed} 个文件");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"扫描目录时出错: {ex.Message}");
            }
        }

        private string MakeUniqueFileName(string filePath)
        {
            if (!File.Exists(filePath))
                return filePath;

            string directory = Path.GetDirectoryName(filePath) ?? "";
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
            string extension = Path.GetExtension(filePath);

            int counter = 1;
            string newPath;

            do
            {
                newPath = Path.Combine(directory, $"{fileNameWithoutExt}_{counter}{extension}");
                counter++;
            }
            while (File.Exists(newPath));

            return newPath;
        }

        private string DetectFileExtension(byte[] fileData)
        {
            foreach (var entry in FileTypeSignatures)
            {
                byte[][] signatures = entry.Value;
                bool allSignaturesMatch = true;

                for (int i = 0; i < signatures.Length; i++)
                {
                    byte[] signature = signatures[i];
                    int offset = 0;

                    if (entry.Key == ".wav" && i == 1)
                        offset = 8;

                    if (entry.Key == ".mp4" && i == 0)
                        offset = 4;

                    if (entry.Key == ".mp4" && i > 0)
                        continue;

                    if (fileData.Length < offset + signature.Length ||
                        !ByteArrayStartsWith(fileData, signature, offset))
                    {
                        allSignaturesMatch = false;
                        break;
                    }
                }

                if (allSignaturesMatch)
                    return entry.Key;
            }

            return ".ttf";
        }

        private bool ByteArrayStartsWith(byte[] data, byte[] prefix, int offset = 0)
        {
            for (int i = 0; i < prefix.Length; i++)
            {
                if (data[offset + i] != prefix[i])
                    return false;
            }
            return true;
        }

        private bool ByteArrayEndsWith(byte[] data, byte[] suffix)
        {
            for (int i = 0; i < suffix.Length; i++)
            {
                if (data[data.Length - suffix.Length + i] != suffix[i])
                    return false;
            }
            return true;
        }

        private bool IsVndat(string filePath)
        {
            try
            {
                byte[] fileSignature = new byte[4];

                using FileStream file = File.OpenRead(filePath);
                int bytesRead = file.Read(fileSignature, 0, fileSignature.Length);
                if (bytesRead != fileSignature.Length)
                {
                    return false;
                }

                for (int i = 0; i < fileSignature.Length; i++)
                {
                    if (fileSignature[i] != PKZIP[i])
                        return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"读取 {Path.GetFileName(filePath)} 时出错。 {ex.Message}");
                return false;
            }
        }

        private bool IsPasswordProtectedZip(string filePath)
        {
            try
            {
                using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read);
                using ZipInputStream zipStream = new(fileStream);

                ZipEntry entry;
                while ((entry = zipStream.GetNextEntry()) != null)
                {
                    if (entry.IsCrypted)
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private byte[] XOR(byte[] buffer)
        {
            if (buffer.Length < 100)
            {
                if (buffer.Length <= 0)
                    return buffer;

                // XOR entire bytes
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] ^= REVERSED_KEY[i % KEY.Length];
            }
            else
            {
                // XOR the first 100 bytes
                for (int i = 0; i < 100; i++)
                    buffer[i] ^= KEY[i % KEY.Length];

                // XOR the last 100 bytes
                for (int i = 0; i < 99; i++)
                    buffer[buffer.Length - 99 + i] ^= REVERSED_KEY[i % KEY.Length];
            }

            return buffer;
        }

        private void XOR(string filePath, string? outputFilePath = null)
        {
            try
            {
                byte[] buffer;
                int bufferLength;

                using (FileStream inputStream = File.OpenRead(filePath))
                {
                    buffer = new byte[bufferLength = (int)inputStream.Length];
                    int bytesRead = 0;
                    while (bytesRead < bufferLength)
                    {
                        bytesRead += inputStream.Read(buffer, bytesRead, bufferLength - bytesRead);
                    }
                }

                buffer = XOR(buffer);

                using FileStream outputStream = File.OpenWrite(outputFilePath ?? filePath);
                outputStream.Write(buffer, 0, bufferLength);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"发生错误: {ex.Message}");
            }
        }

        private string[] GetFilesRecursive(string sourceFolder)
        {
            return Directory.GetFiles(sourceFolder, "*.*", SearchOption.AllDirectories);
        }

        private void ClassifyFilesByExtension(string directory)
        {
            try
            {
                string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
                int totalFiles = files.Length;
                int processed = 0;

                Console.WriteLine($"找到 {totalFiles} 个文件需要分类...");

                foreach (string filePath in files)
                {
                    try
                    {
                        string extension = Path.GetExtension(filePath).TrimStart('.').ToLower();

                        if (string.IsNullOrEmpty(extension))
                            extension = "unknown";

                        string subFolder = Path.Combine(directory, extension);
                        Directory.CreateDirectory(subFolder);

                        string fileName = Path.GetFileName(filePath);
                        string destinationPath = Path.Combine(subFolder, fileName);

                        if (File.Exists(destinationPath))
                            destinationPath = MakeUniqueFileName(destinationPath);

                        File.Move(filePath, destinationPath);
                        Console.WriteLine($"移动文件: {Path.GetFileName(filePath)} -> {extension}/{fileName}");

                        processed++;
                        if (processed % 100 == 0)
                        {
                            Console.WriteLine($"已处理: {processed}/{totalFiles}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"处理文件 {filePath} 时出错: {ex.Message}");
                    }
                }

                Console.WriteLine($"文件分类完成: 共处理 {processed} 个文件");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"分类文件时出错: {ex.Message}");
            }
        }
    }
}