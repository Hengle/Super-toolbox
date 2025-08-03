using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class WebpExtractor : BaseExtractor
    {
        private static readonly byte[] riffHeader = { 0x52, 0x49, 0x46, 0x46 };
        private static readonly byte[] webpBlock = { 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38 };

        public override void Extract(string directoryPath)
        {
            ExtractAsync(directoryPath).Wait();
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed($"目录不存在: {directoryPath}");
                return;
            }

            string extractedDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedDir);

            var files = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                .Where(file => !file.StartsWith(extractedDir, StringComparison.OrdinalIgnoreCase));

            TotalFilesToExtract = files.Count();
            Console.WriteLine($"目录中的源文件数量为: {TotalFilesToExtract}");

            foreach (string filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await ExtractWebpsFromFileAsync(filePath, extractedDir, cancellationToken);
                }
                catch (Exception ex)
                {
                    OnExtractionFailed($"处理文件 {filePath} 时出错: {ex.Message}");
                }
            }

            int actualExtractedFileCount = Directory.EnumerateFiles(extractedDir, "*.webp", SearchOption.AllDirectories).Count();
            Console.WriteLine($"统计提取文件数量: {ExtractedFileCount}，实际在Extracted文件夹中的子文件数量为: {actualExtractedFileCount}");

            if (ExtractedFileCount != actualExtractedFileCount)
            {
                Console.WriteLine("警告: 统计数量与实际数量不符，可能存在文件操作异常。");
            }
        }

        private async Task ExtractWebpsFromFileAsync(string filePath, string extractedDir, CancellationToken cancellationToken)
        {
            byte[] fileContent = await File.ReadAllBytesAsync(filePath, cancellationToken);
            string baseFilename = Path.GetFileNameWithoutExtension(filePath);

            foreach (byte[] webpData in ExtractWebpData(fileContent))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string extractedFilename = $"{baseFilename}_{ExtractedFileCount}.webp";
                string extractedPath = Path.Combine(extractedDir, extractedFilename);

                Directory.CreateDirectory(Path.GetDirectoryName(extractedPath) ?? extractedDir);

                await File.WriteAllBytesAsync(extractedPath, webpData, cancellationToken);
                Console.WriteLine($"提取的文件: {extractedPath}");

                OnFileExtracted(extractedPath);
            }
        }

        private static IEnumerable<byte[]> ExtractWebpData(byte[] fileContent)
        {
            int webpDataStart = 0;
            while ((webpDataStart = IndexOf(fileContent, riffHeader, webpDataStart)) != -1)
            {
                if (webpDataStart + 12 > fileContent.Length)
                {
                    Console.WriteLine($"警告: 在位置 {webpDataStart} 发现RIFF头，但文件剩余长度不足");
                    webpDataStart += 4;
                    continue;
                }

                int fileSize = BitConverter.ToInt32(fileContent, webpDataStart + 4);
                fileSize = (fileSize + 1) & ~1;

                if (fileSize <= 0 || webpDataStart + 8 + fileSize > fileContent.Length)
                {
                    Console.WriteLine($"警告: 在位置 {webpDataStart} 的RIFF头大小无效或超出文件范围");
                    webpDataStart += 4;
                    continue;
                }

                int blockStart = webpDataStart + 8;
                bool hasWebpBlock = IndexOf(fileContent, webpBlock, blockStart) != -1;

                if (hasWebpBlock)
                {
                    int actualLength = Math.Min(fileSize + 8, fileContent.Length - webpDataStart);
                    byte[] webpData = new byte[actualLength];
                    Array.Copy(fileContent, webpDataStart, webpData, 0, actualLength);
                    yield return webpData;
                }

                webpDataStart += Math.Max(4, fileSize + 8);
            }
        }

        private static int IndexOf(byte[] source, byte[] pattern, int startIndex)
        {
            for (int i = startIndex; i <= source.Length - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (source[i + j] != pattern[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                {
                    return i;
                }
            }
            return -1;
        }
    }
}