using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CriFsV2Lib;
using CriFsV2Lib.Definitions;
using CriFsV2Lib.Definitions.Interfaces;
using CriFsV2Lib.Definitions.Structs;
using ByteSizeLib;

namespace super_toolbox
{
    public class CpkExtractor : BaseExtractor
    {
        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed($"错误: {directoryPath} 不是有效的目录");
                return;
            }

            string extractedRootDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedRootDir);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            var cpkFiles = Directory.GetFiles(directoryPath, "*.cpk", SearchOption.AllDirectories)
                .Where(file => !file.StartsWith(extractedRootDir, StringComparison.OrdinalIgnoreCase))
                .ToList();

            TotalFilesToExtract = cpkFiles.Count;

            try
            {
                await Task.Run(() =>
                {
                    Parallel.ForEach(cpkFiles, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                        CancellationToken = cancellationToken
                    }, cpkFilePath =>
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string cpkFileName = Path.GetFileNameWithoutExtension(cpkFilePath);
                            string cpkExtractDir = Path.Combine(extractedRootDir, cpkFileName);
                            Directory.CreateDirectory(cpkExtractDir);

                            using (var fileStream = new FileStream(cpkFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                var cpk = CriFsLib.Instance.CreateCpkReader(fileStream, true);
                                var files = cpk.GetFiles();

                                using (var extractor = CriFsLib.Instance.CreateBatchExtractor<CpkFileExtractorItem>(cpkFilePath))
                                {
                                    foreach (var file in files)
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();

                                        string relativePath = !string.IsNullOrEmpty(file.Directory)
                                            ? Path.Combine(file.Directory, file.FileName)
                                            : file.FileName;

                                        string outputPath = Path.Combine(cpkExtractDir, relativePath);
                                        string? outputDir = Path.GetDirectoryName(outputPath);
                                        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                                            Directory.CreateDirectory(outputDir);

                                        extractor.QueueItem(new CpkFileExtractorItem(outputPath, file));
                                    }

                                    extractor.WaitForCompletion();
                                }

                                foreach (var file in files)
                                {
                                    string relativePath = !string.IsNullOrEmpty(file.Directory)
                                        ? Path.Combine(file.Directory, file.FileName)
                                        : file.FileName;

                                    string outputPath = Path.Combine(cpkExtractDir, relativePath);

                                    if (File.Exists(outputPath))
                                    {
                                        OnFileExtracted(outputPath);
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            OnExtractionFailed($"处理 {Path.GetFileName(cpkFilePath)} 时出错: {ex.Message}");
                        }
                    });
                }, cancellationToken);

                OnExtractionCompleted();
            }
            catch (OperationCanceledException)
            {
                OnExtractionFailed("操作已取消");
            }
            catch (Exception ex)
            {
                OnExtractionFailed($"提取失败: {ex.Message}");
            }
            finally
            {
                sw.Stop();
            }
        }

        public override void Extract(string directoryPath)
        {
            ExtractAsync(directoryPath).Wait();
        }

        private class CpkFileExtractorItem : IBatchFileExtractorItem
        {
            public string FullPath { get; }
            public CpkFile File { get; }

            public CpkFileExtractorItem(string fullPath, CpkFile file)
            {
                FullPath = fullPath;
                File = file;
            }
        }
    }
}