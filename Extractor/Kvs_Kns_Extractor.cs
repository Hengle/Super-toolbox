using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class Kvs_Kns_Extractor : BaseExtractor
    {
        private static readonly byte[] KVS_SIG_BYTES = { 0x4B, 0x4F, 0x56, 0x53 }; // Steam平台
        private static readonly byte[] KNS_SIG_BYTES = { 0x4B, 0x54, 0x53, 0x53 }; // Switch平台
        private static readonly byte[] AT3_SIG_BYTES = { 0x52, 0x49, 0x46, 0x46 }; // PS4平台(AT3)RIFF（4字节）
        private static readonly byte[] KTAC_SIG_BYTES = { 0x4B, 0x54, 0x41, 0x43 }; // PS4平台(KTAC)
        private static readonly byte[] WAVE_FMT_SIG = { 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74 }; // WAVEfmt（7字节）
        private const int AT3_HEADER_TOTAL_LENGTH = 15; // 4(RIFF) + 4 + 7(WAVEfmt) = 15字节

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
                if (found) return i;
            }
            return -1;
        }

        public override void Extract(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed($"源文件夹 {directoryPath} 不存在");
                return;
            }

            string extractedDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedDir);

            var filePaths = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).ToList();
            TotalFilesToExtract = filePaths.Count;

            var extractedFiles = ProcessFiles(filePaths, extractedDir);
            OnExtractionCompleted();
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed($"源文件夹 {directoryPath} 不存在");
                return;
            }

            string extractedDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedDir);

            var filePaths = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).ToList();
            TotalFilesToExtract = filePaths.Count;

            var extractedFiles = await ProcessFilesAsync(filePaths, extractedDir, cancellationToken);
            OnExtractionCompleted();
        }

        private List<string> ProcessFiles(List<string> filePaths, string extractedDir)
        {
            var extractedFiles = new List<string>();

            foreach (var filePath in filePaths)
            {
                try
                {
                    byte[] content = File.ReadAllBytes(filePath);
                    var currentExtracted = ProcessFileContent(content, filePath, extractedDir).ToList();

                    if (currentExtracted.Any())
                    {
                        extractedFiles.AddRange(currentExtracted);
                    }
                }
                catch (Exception ex)
                {
                    OnExtractionFailed($"处理 {Path.GetFileName(filePath)} 失败: {ex.Message}");
                }
            }

            return extractedFiles;
        }

        private async Task<List<string>> ProcessFilesAsync(List<string> filePaths, string extractedDir, CancellationToken cancellationToken)
        {
            var extractedFiles = new List<string>();

            foreach (var filePath in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    byte[] content = await File.ReadAllBytesAsync(filePath, cancellationToken);
                    var currentExtracted = ProcessFileContent(content, filePath, extractedDir).ToList();

                    if (currentExtracted.Any())
                    {
                        extractedFiles.AddRange(currentExtracted);
                    }
                }
                catch (OperationCanceledException)
                {
                    OnExtractionFailed("提取操作已取消");
                    throw;
                }
                catch (Exception ex)
                {
                    OnExtractionFailed($"处理 {Path.GetFileName(filePath)} 失败: {ex.Message}");
                }
            }

            return extractedFiles;
        }

        private IEnumerable<string> ProcessFileContent(byte[] content, string sourcePath, string outputDir)
        {
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            var allExtracted = new List<string>();

            allExtracted.AddRange(ExtractFormat(content, baseName, outputDir, KVS_SIG_BYTES, ".kvs"));
            allExtracted.AddRange(ExtractFormat(content, baseName, outputDir, KNS_SIG_BYTES, ".kns"));
            allExtracted.AddRange(ExtractAt3Format(content, baseName, outputDir));
            allExtracted.AddRange(ExtractFormat(content, baseName, outputDir, KTAC_SIG_BYTES, ".ktac"));

            return allExtracted.Distinct();
        }

        private IEnumerable<string> ExtractFormat(byte[] content, string baseName, string outputDir, byte[] sigBytes, string ext)
        {
            var extracted = new List<string>();
            int index = 0;
            int count = 0;

            while (index < content.Length)
            {
                int start = IndexOf(content, sigBytes, index);
                if (start == -1) break;

                int end = IndexOf(content, sigBytes, start + 1);
                if (end == -1) end = content.Length;

                count++;
                string fileName = count == 1 ? $"{baseName}{ext}" : $"{baseName}_{count}{ext}";
                string outputPath = Path.Combine(outputDir, fileName);

                try
                {
                    File.WriteAllBytes(outputPath, content[start..end]);
                    extracted.Add(outputPath);
                    OnFileExtracted(outputPath);
                }
                catch (Exception ex)
                {
                    OnExtractionFailed($"保存 {fileName} 失败: {ex.Message}");
                }

                index = end;
            }

            return extracted;
        }

        private IEnumerable<string> ExtractAt3Format(byte[] content, string baseName, string outputDir)
        {
            var extracted = new List<string>();
            int index = 0;
            int count = 0;

            while (index < content.Length - AT3_HEADER_TOTAL_LENGTH)
            {
                int riffStart = IndexOf(content, AT3_SIG_BYTES, index);
                if (riffStart == -1) break;

                if (riffStart + AT3_HEADER_TOTAL_LENGTH > content.Length)
                {
                    OnExtractionFailed($"位置 {riffStart} 头部不完整（不足15字节），跳过");
                    index = riffStart + 4;
                    continue;
                }

                bool isWaveFmtValid = true;
                for (int i = 0; i < WAVE_FMT_SIG.Length; i++)
                {
                    if (content[riffStart + 8 + i] != WAVE_FMT_SIG[i])
                    {
                        isWaveFmtValid = false;
                        break;
                    }
                }

                if (!isWaveFmtValid)
                {
                    OnExtractionFailed($"位置 {riffStart} WAVEfmt匹配失败，跳过");
                    index = riffStart + 4;
                    continue;
                }

                int end = IndexOf(content, AT3_SIG_BYTES, riffStart + 1);
                if (end == -1) end = content.Length;

                count++;
                string fileName = count == 1 ? $"{baseName}.at3" : $"{baseName}_{count}.at3";
                string outputPath = Path.Combine(outputDir, fileName);

                try
                {
                    byte[] at3Data = new byte[end - riffStart];
                    Array.Copy(content, riffStart, at3Data, 0, end - riffStart);
                    File.WriteAllBytes(outputPath, at3Data);
                    extracted.Add(outputPath);
                    OnFileExtracted(outputPath);
                }
                catch (Exception ex)
                {
                    OnExtractionFailed($"保存 {fileName} 失败: {ex.Message}");
                }

                index = end;
            }

            return extracted;
        }
    }
}