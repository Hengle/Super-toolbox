using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GXTConvert.Exceptions;
using GXTConvert.FileFormat;
using GXTConvert.FileFormat.BUV;

namespace super_toolbox
{
    public class SonyGxtConverter : BaseExtractor
    {
        private readonly object _lockObject = new object();
        private int _extractedFileCount = 0;

        public new event EventHandler<string>? FileExtracted;
        public event EventHandler<string>? ExtractionProgress;
        public new event EventHandler<int>? ExtractionCompleted;

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                ExtractionProgress?.Invoke(this, $"错误: {directoryPath} 不是有效的目录");
                OnExtractionFailed($"错误: {directoryPath} 不是有效的目录");
                return;
            }

            string extractedDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedDir);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            var files = Directory.EnumerateFiles(directoryPath, "*.gxt", SearchOption.AllDirectories)
               .Where(file => !file.StartsWith(extractedDir, StringComparison.OrdinalIgnoreCase))
               .ToList();

            TotalFilesToExtract = files.Count;
            ExtractionProgress?.Invoke(this, $"开始处理 {files.Count} 个GXT文件...");
            OnFileExtracted($"开始处理 {files.Count} 个GXT文件...");

            var extractedFiles = new ConcurrentBag<string>();

            try
            {
                await Task.Run(() =>
                {
                    Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 }, filePath =>
                    {
                        try
                        {
                            string gxtFileName = Path.GetFileNameWithoutExtension(filePath);
                            string gxtExtractedDir = Path.Combine(extractedDir, gxtFileName);
                            Directory.CreateDirectory(gxtExtractedDir);

                            ExtractionProgress?.Invoke(this, $"处理文件: {gxtFileName}.gxt");

                            using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                GxtBinary gxtInstance = new GxtBinary(fileStream);

                                for (int i = 0; i < gxtInstance.TextureInfos.Length; i++)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();

                                    string outputFilename = string.Format("{0} (Texture {1}).png", gxtFileName, i);
                                    string outputFilePath = GetUniqueFilePath(gxtExtractedDir, outputFilename);

                                    lock (_lockObject)
                                    {
                                        gxtInstance.Textures[i].Save(outputFilePath, System.Drawing.Imaging.ImageFormat.Png);
                                    }

                                    extractedFiles.Add(outputFilePath);

                                    int count = Interlocked.Increment(ref _extractedFileCount);
                                    string relativePath = Path.GetRelativePath(directoryPath, outputFilePath);

                                    FileExtracted?.Invoke(this, $"已提取: {relativePath}");
                                    OnFileExtracted(outputFilePath);
                                }

                                if (gxtInstance.BUVChunk != null)
                                {
                                    for (int i = 0; i < gxtInstance.BUVTextures.Length; i++)
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();

                                        string outputFilename = string.Format("{0} (Block {1}).png", gxtFileName, i);
                                        string outputFilePath = GetUniqueFilePath(gxtExtractedDir, outputFilename);

                                        lock (_lockObject)
                                        {
                                            gxtInstance.BUVTextures[i].Save(outputFilePath, System.Drawing.Imaging.ImageFormat.Png);
                                        }

                                        extractedFiles.Add(outputFilePath);

                                        int count = Interlocked.Increment(ref _extractedFileCount);
                                        string relativePath = Path.GetRelativePath(directoryPath, outputFilePath);

                                        FileExtracted?.Invoke(this, $"已提取: {relativePath}");
                                        OnFileExtracted(outputFilePath);
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
                            ExtractionProgress?.Invoke(this, $"处理文件时出错: {ex.Message}");
                        }
                    });
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ExtractionProgress?.Invoke(this, "提取操作已取消");
                OnExtractionFailed("提取操作已取消");
                throw;
            }
            catch (Exception ex)
            {
                ExtractionProgress?.Invoke(this, $"提取过程中出错: {ex.Message}");
                OnExtractionFailed($"提取过程中出错: {ex.Message}");
            }

            sw.Stop();

            int actualExtractedCount = Directory.EnumerateFiles(extractedDir, "*", SearchOption.AllDirectories).Count();

            ExtractionProgress?.Invoke(this, $"处理完成，耗时 {sw.Elapsed.TotalSeconds:F2} 秒");
            ExtractionProgress?.Invoke(this, $"共提取出 {actualExtractedCount} 个文件，统计提取文件数量: {_extractedFileCount}");

            if (_extractedFileCount != actualExtractedCount)
            {
                ExtractionProgress?.Invoke(this, "警告: 统计数量与实际数量不符，可能存在文件操作异常。");
            }

            ExtractionCompleted?.Invoke(this, _extractedFileCount);
            OnExtractionCompleted();
        }

        private string GetUniqueFilePath(string directory, string originalFileName)
        {
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(originalFileName);
            string fileExtension = Path.GetExtension(originalFileName);
            string baseFilePath = Path.Combine(directory, originalFileName);

            if (!File.Exists(baseFilePath))
            {
                return baseFilePath;
            }

            int counter = 1;
            string newFilePath;
            do
            {
                string newFileName = $"{fileNameWithoutExtension}_{counter}{fileExtension}";
                newFilePath = Path.Combine(directory, newFileName);
                counter++;
            } while (File.Exists(newFilePath));

            return newFilePath;
        }
    }
}