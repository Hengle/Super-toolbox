using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class HcaExtractor : BaseExtractor
    {
        private static readonly byte[] START_SEQ_1 = { 0x48, 0x43, 0x41, 0x00 };
        private static readonly byte[] START_SEQ_2 = { 0xC8, 0xC3, 0xC1, 0x00, 0x03, 0x00, 0x00, 0x60 };
        private static readonly byte[] HCA_BLOCK_MARKER = { 0x66, 0x6D, 0x74 };

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            string extractedDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedDir);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var files = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
               .Where(file => !file.StartsWith(extractedDir, StringComparison.OrdinalIgnoreCase))
               .ToList();

            TotalFilesToExtract = files.Count;

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
                            byte[] content = File.ReadAllBytes(filePath);
                            int count = ExtractHcaType(content, filePath, extractedDir, START_SEQ_1, HCA_BLOCK_MARKER, extractedFiles);
                            count += ExtractHcaType(content, filePath, extractedDir, START_SEQ_2, null, extractedFiles);
                        }
                        catch (Exception ex)
                        {
                            OnExtractionFailed($"提取文件 {filePath} 时发生错误: {ex.Message}");
                        }
                    });
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("提取操作已取消。");
            }

            sw.Stop();

            int actualExtractedCount = Directory.EnumerateFiles(extractedDir, "*.hca", SearchOption.AllDirectories).Count();
            Console.WriteLine($"处理完成，耗时 {sw.Elapsed.TotalSeconds:F2} 秒");
            Console.WriteLine($"共提取出 {actualExtractedCount} 个HCA文件，统计提取文件数量: {ExtractedFileCount}");
            if (ExtractedFileCount != actualExtractedCount)
            {
                Console.WriteLine("警告: 统计数量与实际数量不符，可能存在文件操作异常。");
            }
        }

        private int ExtractHcaType(byte[] content, string filePath, string extractedDir,
            byte[] startSequence, byte[]? blockMarker, ConcurrentBag<string> extractedFiles)
        {
            int count = 0;
            int index = 0;

            while (index < content.Length)
            {
                int headerStartIndex = IndexOf(content, startSequence, index);
                if (headerStartIndex == -1)
                {
                    break;
                }

                int nextHeaderIndex = IndexOf(content, startSequence, headerStartIndex + 1);
                int endIndex = nextHeaderIndex == -1 ? content.Length : nextHeaderIndex;

                byte[] extractedData = new byte[endIndex - headerStartIndex];
                Array.Copy(content, headerStartIndex, extractedData, 0, extractedData.Length);

                if (blockMarker == null || ContainsBytes(extractedData, blockMarker))
                {
                    string outputFileName = startSequence.SequenceEqual(START_SEQ_2)
                        ? $"{Path.GetFileNameWithoutExtension(filePath)}_{++count}_enc.hca"
                        : $"{Path.GetFileNameWithoutExtension(filePath)}_{++count}.hca";

                    string outputFilePath = Path.Combine(extractedDir, outputFileName);

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);
                        File.WriteAllBytes(outputFilePath, extractedData);
                        extractedFiles.Add(outputFilePath);
                        OnFileExtracted(outputFilePath);
                    }
                    catch (Exception ex)
                    {
                        OnExtractionFailed($"写入文件 {outputFilePath} 时发生错误: {ex.Message}");
                    }
                }

                index = headerStartIndex + 1;
            }

            return count;
        }

        private static int IndexOf(byte[] data, byte[] pattern, int startIndex)
        {
            for (int i = startIndex; i <= data.Length - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
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

        private static bool ContainsBytes(byte[] data, byte[] pattern)
        {
            return IndexOf(data, pattern, 0) != -1;
        }
    }
}