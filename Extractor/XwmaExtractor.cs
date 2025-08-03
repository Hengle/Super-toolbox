using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class XwmaExtractor : BaseExtractor
    {
        private static readonly byte[] riffHeader = { 0x52, 0x49, 0x46, 0x46 };
        private static readonly byte[] xwmaBlock = { 0x58, 0x57, 0x4D, 0x41, 0x66, 0x6D, 0x74 };

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
                    await ExtractxwmasFromFileAsync(filePath, extractedDir, cancellationToken);
                }
                catch (Exception ex)
                {
                    OnExtractionFailed($"处理文件 {filePath} 时出错: {ex.Message}");
                }
            }

            int actualExtractedFileCount = Directory.EnumerateFiles(extractedDir, "*.xwma", SearchOption.AllDirectories).Count();
            Console.WriteLine($"总共提取了 {ExtractedFileCount} 个文件，实际在Extracted文件夹中的文件数量为: {actualExtractedFileCount}");

            if (ExtractedFileCount != actualExtractedFileCount)
            {
                Console.WriteLine("警告: 统计数量与实际数量不符，可能存在文件操作异常。");
            }
        }

        private async Task ExtractxwmasFromFileAsync(string filePath, string extractedDir, CancellationToken cancellationToken)
        {
            byte[] fileContent = await File.ReadAllBytesAsync(filePath, cancellationToken);
            string baseFilename = Path.GetFileNameWithoutExtension(filePath);

            foreach (byte[] xwmaData in ExtractxwmaData(fileContent))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string extractedFilename = $"{baseFilename}_{ExtractedFileCount}.xwma";
                string extractedPath = Path.Combine(extractedDir, extractedFilename);

                await File.WriteAllBytesAsync(extractedPath, xwmaData, cancellationToken);
                Console.WriteLine($"提取的文件: {extractedPath}");

                OnFileExtracted(extractedPath);
            }
        }

        private static IEnumerable<byte[]> ExtractxwmaData(byte[] fileContent)
        {
            int xwmaDataStart = 0;
            while ((xwmaDataStart = IndexOf(fileContent, riffHeader, xwmaDataStart)) != -1)
            {
                if (xwmaDataStart + 12 > fileContent.Length)
                {
                    Console.WriteLine($"警告: 在位置 {xwmaDataStart} 发现RIFF头，但文件剩余长度不足");
                    xwmaDataStart += 4;
                    continue;
                }

                int fileSize = BitConverter.ToInt32(fileContent, xwmaDataStart + 4);
                fileSize = (fileSize + 1) & ~1;

                if (fileSize <= 0 || xwmaDataStart + 8 + fileSize > fileContent.Length)
                {
                    Console.WriteLine($"警告: 在位置 {xwmaDataStart} 的RIFF头大小无效或超出文件范围");
                    xwmaDataStart += 4;
                    continue;
                }

                int blockStart = xwmaDataStart + 8;
                bool hasxwmaBlock = IndexOf(fileContent, xwmaBlock, blockStart) != -1;

                if (hasxwmaBlock)
                {
                    int actualLength = Math.Min(fileSize + 8, fileContent.Length - xwmaDataStart);
                    byte[] xwmaData = new byte[actualLength];
                    Array.Copy(fileContent, xwmaDataStart, xwmaData, 0, actualLength);
                    yield return xwmaData;
                }

                xwmaDataStart += Math.Max(4, fileSize + 8);
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