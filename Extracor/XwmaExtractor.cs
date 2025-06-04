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

        private void OnProgressUpdated()
        {
            Console.WriteLine($"提取进度: {ExtractedFileCount}/{TotalFilesToExtract}");
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
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
                    int count = await ExtractxwmasFromFileAsync(filePath, extractedDir, cancellationToken);
                    Interlocked.Add(ref _extractedFileCount, count);
                    OnProgressUpdated();
                }
                catch (Exception ex)
                {
                    OnExtractionFailed($"处理文件 {filePath} 时出错: {ex.Message}");
                }
            }

            OnExtractionCompleted();

            int actualExtractedFileCount = Directory.EnumerateFiles(extractedDir, "*.xwma", SearchOption.AllDirectories).Count();

            Console.WriteLine($"总共提取了 {ExtractedFileCount} 个文件，实际在Extracted文件夹中的文件数量为: {actualExtractedFileCount}");
            if (ExtractedFileCount != actualExtractedFileCount)
            {
                Console.WriteLine("警告: 统计数量与实际数量不符，可能存在文件操作异常。");
            }
        }

        private async Task<int> ExtractxwmasFromFileAsync(string filePath, string extractedDir, CancellationToken cancellationToken)
        {
            byte[] fileContent = await File.ReadAllBytesAsync(filePath, cancellationToken);
            int fileCount = 0;
            string baseFilename = Path.GetFileNameWithoutExtension(filePath);

            foreach (byte[] xwmaData in ExtractxwmaData(fileContent))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string extractedFilename = $"{baseFilename}_{fileCount}.xwma";
                string extractedPath = Path.Combine(extractedDir, extractedFilename);

                await File.WriteAllBytesAsync(extractedPath, xwmaData, cancellationToken);
                Console.WriteLine($"提取的文件: {extractedPath}");

                OnFileExtracted(extractedPath);
                fileCount++;
            }

            return fileCount;
        }

        private static IEnumerable<byte[]> ExtractxwmaData(byte[] fileContent)
        {
            int xwmaDataStart = 0;
            while ((xwmaDataStart = IndexOf(fileContent, riffHeader, xwmaDataStart)) != -1)
            {
                int fileSize = BitConverter.ToInt32(fileContent, xwmaDataStart + 4);
                fileSize = (fileSize + 1) & ~1;

                int blockStart = xwmaDataStart + 8;
                bool hasxwmaBlock = IndexOf(fileContent, xwmaBlock, blockStart) != -1;

                if (hasxwmaBlock)
                {
                    byte[] xwmaData = new byte[fileSize + 8];
                    Array.Copy(fileContent, xwmaDataStart, xwmaData, 0, fileSize + 8);
                    yield return xwmaData;
                }

                xwmaDataStart += fileSize + 8;
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