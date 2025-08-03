using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class RifxExtractor : BaseExtractor
    {
        private static readonly byte[] RIFXHeader = { 0x52, 0x49, 0x46, 0x58 };
        private static readonly byte[] wemBlock = { 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74 };

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
                    await ExtractwemsFromFileAsync(filePath, extractedDir, cancellationToken);
                    Console.WriteLine($"处理文件 {ExtractedFileCount + 1}/{TotalFilesToExtract}: {Path.GetFileName(filePath)}");
                }
                catch (Exception ex)
                {
                    OnExtractionFailed($"处理文件 {filePath} 时出错: {ex.Message}");
                }
            }

            OnExtractionCompleted();

            int actualExtractedFileCount = Directory.EnumerateFiles(extractedDir, "*.wem", SearchOption.AllDirectories).Count();
            Console.WriteLine($"总共提取了 {ExtractedFileCount} 个文件，实际在Extracted文件夹中的文件数量为: {actualExtractedFileCount}");
            if (ExtractedFileCount != actualExtractedFileCount)
            {
                Console.WriteLine("警告: 统计数量与实际数量不符，可能存在文件操作异常。");
            }
        }

        private async Task ExtractwemsFromFileAsync(string filePath, string extractedDir, CancellationToken cancellationToken)
        {
            byte[] fileContent = await File.ReadAllBytesAsync(filePath, cancellationToken);
            string baseFilename = Path.GetFileNameWithoutExtension(filePath);

            foreach (byte[] wemData in ExtractwemData(fileContent))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string extractedFilename = $"{baseFilename}_{ExtractedFileCount}.wem";
                string extractedPath = Path.Combine(extractedDir, extractedFilename);

                await File.WriteAllBytesAsync(extractedPath, wemData, cancellationToken);
                Console.WriteLine($"提取的文件: {extractedPath}");

                OnFileExtracted(extractedPath);
            }
        }

        private static IEnumerable<byte[]> ExtractwemData(byte[] fileContent)
        {
            int wemDataStart = 0;
            while ((wemDataStart = IndexOf(fileContent, RIFXHeader, wemDataStart)) != -1)
            {
                int nextRifxIndex = IndexOf(fileContent, RIFXHeader, wemDataStart + 1);
                int endIndex;
                if (nextRifxIndex != -1)
                {
                    endIndex = nextRifxIndex;
                }
                else
                {
                    endIndex = fileContent.Length;
                }

                int blockStart = wemDataStart + 8;
                bool haswemBlock = IndexOf(fileContent, wemBlock, blockStart) != -1;

                if (haswemBlock)
                {
                    int length = endIndex - wemDataStart;
                    byte[] wemData = new byte[length];
                    Array.Copy(fileContent, wemDataStart, wemData, 0, length);
                    yield return wemData;
                }

                wemDataStart = endIndex;
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