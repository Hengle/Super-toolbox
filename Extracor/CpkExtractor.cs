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
        public event EventHandler<string>? ExtractionProgress;

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
            ExtractionProgress?.Invoke(this, $"发现 {TotalFilesToExtract} 个CPK文件");

            try
            {
                // 使用Task.Run将CPU密集型工作放在后台线程
                await Task.Run(() =>
                {
                    Parallel.ForEach(cpkFiles, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount
                    }, cpkFilePath =>
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string cpkFileName = Path.GetFileNameWithoutExtension(cpkFilePath);
                            string cpkExtractDir = Path.Combine(extractedRootDir, cpkFileName);
                            Directory.CreateDirectory(cpkExtractDir);

                            ExtractionProgress?.Invoke(this, $"正在处理: {cpkFileName}");

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

                                        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                                            Directory.CreateDirectory(outputDir);

                                        extractor.QueueItem(new CpkFileExtractorItem(outputPath, file));
                                    }

                                    extractor.WaitForCompletion();
                                }

                                // 报告提取结果
                                foreach (var file in files)
                                {
                                    string relativePath = !string.IsNullOrEmpty(file.Directory)
                                        ? Path.Combine(file.Directory, file.FileName)
                                        : file.FileName;

                                    string outputPath = Path.Combine(cpkExtractDir, relativePath);

                                    if (File.Exists(outputPath))
                                    {
                                        var fileInfo = new FileInfo(outputPath);
                                        string sizeInfo = ByteSize.FromBytes(fileInfo.Length).ToString();

                                        Interlocked.Increment(ref _extractedFileCount);
                                        OnFileExtracted(outputPath);
                                        ExtractionProgress?.Invoke(this, $"已提取: {relativePath} ({sizeInfo})");
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
                            ExtractionProgress?.Invoke(this, $"处理 {Path.GetFileName(cpkFilePath)} 时出错: {ex.Message}");
                        }
                    });
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                OnExtractionFailed("操作已取消");
                throw;
            }
            catch (Exception ex)
            {
                OnExtractionFailed($"提取失败: {ex.Message}");
                throw;
            }

            sw.Stop();
            ExtractionProgress?.Invoke(this, $"完成! 耗时 {sw.Elapsed.TotalSeconds:F2} 秒");
            OnExtractionCompleted();
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