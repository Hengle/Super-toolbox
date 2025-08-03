using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class MP4_Extractor : BaseExtractor
    {
        private static readonly byte[] MP4_SIGNATURE = { 0x66, 0x74, 0x79, 0x70 }; // "ftyp"
        private static readonly HashSet<string> VALID_BRANDS = new HashSet<string>
        {
            "mp41", "mp42", "isom", "avc1", "M4V ", "M4A ", "M4P ", "M4B ", "qt  ", "iso2", "3g2a", "drc1", "F4V", "F4P", "F4A", "F4B", "mmp4"// 常见合法的MP4品牌标识
        };

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
                            ExtractValidMP4Files(content, filePath, extractedDir, extractedFiles);
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

            int actualExtractedCount = Directory.EnumerateFiles(extractedDir, "*.mp4", SearchOption.AllDirectories).Count();
            Console.WriteLine($"处理完成，耗时 {sw.Elapsed.TotalSeconds:F2} 秒");
            Console.WriteLine($"共提取出 {actualExtractedCount} 个MP4文件，统计提取文件数量: {ExtractedFileCount}");
            if (ExtractedFileCount != actualExtractedCount)
            {
                Console.WriteLine("警告: 统计数量与实际数量不符，可能存在文件操作异常。");
            }
        }

        private void ExtractValidMP4Files(byte[] content, string filePath, string extractedDir, ConcurrentBag<string> extractedFiles)
        {
            int index = 0;
            int count = 0;

            while (index < content.Length - 8) 
            {
                int signatureIndex = IndexOf(content, MP4_SIGNATURE, index);
                if (signatureIndex == -1)
                {
                    break;
                }

                if (signatureIndex + 8 >= content.Length)
                {
                    index = signatureIndex + 1;
                    continue;
                }

                string brand = System.Text.Encoding.ASCII.GetString(
                    content, signatureIndex + 4, 4);

                if (!VALID_BRANDS.Contains(brand))
                {
                    index = signatureIndex + 1;
                    continue;
                }

                int headerStartIndex = signatureIndex - 4;
                if (headerStartIndex < 0)
                {
                    index = signatureIndex + 1;
                    continue;
                }

                int nextSignatureIndex = IndexOf(content, MP4_SIGNATURE, signatureIndex + 1);
                int endIndex = nextSignatureIndex == -1 ? content.Length : nextSignatureIndex - 4;

                if (endIndex > content.Length)
                {
                    endIndex = content.Length;
                }

                byte[] extractedData = new byte[endIndex - headerStartIndex];
                Array.Copy(content, headerStartIndex, extractedData, 0, extractedData.Length);

                string outputFileName = $"{Path.GetFileNameWithoutExtension(filePath)}_{++count}.mp4";
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

                index = endIndex;
            }
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
    }
}
