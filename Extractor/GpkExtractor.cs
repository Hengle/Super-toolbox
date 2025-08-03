using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class GpkExtractor : BaseExtractor
    {
        public event EventHandler<string>? ExtractionProgress;
        public new event EventHandler<string>? FileExtracted;
        public event EventHandler<string>? ExtractionError;
        public new event EventHandler? ExtractionCompleted;

        private int _totalGpkFiles = 0;
        private int _processedGpkFiles = 0;
        private long _totalExtractedSizeOverall = 0;
        private DateTime _startTime;

        protected virtual void OnExtractionProgress(string message)
        {
            ExtractionProgress?.Invoke(this, message);
        }

        protected new virtual void OnFileExtracted(string fileName)
        {
            base.OnFileExtracted(fileName);
            FileExtracted?.Invoke(this, fileName);
        }

        protected virtual void OnExtractionError(string errorMessage)
        {
            ExtractionError?.Invoke(this, errorMessage);
        }

        protected new virtual void OnExtractionCompleted()
        {
            ExtractionCompleted?.Invoke(this, EventArgs.Empty);
        }

        public override async Task ExtractAsync(string inputPath, CancellationToken cancellationToken = default)
        {
            try
            {
                _totalExtractedSizeOverall = 0;
                _processedGpkFiles = 0;

                if (string.IsNullOrEmpty(inputPath))
                {
                    OnExtractionError("输入路径不能为空");
                    return;
                }

                _startTime = DateTime.Now;

                if (File.Exists(inputPath))
                {
                    if (Path.GetExtension(inputPath).Equals(".gpk", StringComparison.OrdinalIgnoreCase))
                    {
                        await ExtractFileAsync(inputPath, cancellationToken);
                    }
                    else
                    {
                        OnExtractionError($"输入的不是GPK文件: {inputPath}");
                    }
                }
                else if (Directory.Exists(inputPath))
                {
                    var gpkFiles = Directory.GetFiles(inputPath, "*.gpk", SearchOption.AllDirectories);
                    _totalGpkFiles = gpkFiles.Length;

                    if (_totalGpkFiles == 0)
                    {
                        OnExtractionError($"在目录中未找到GPK文件: {inputPath}");
                        return;
                    }

                    Console.WriteLine($"找到 {_totalGpkFiles} 个GPK文件");
                    OnExtractionProgress($"找到 {_totalGpkFiles} 个GPK文件");

                    foreach (var gpkFile in gpkFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            Console.WriteLine($"\n处理文件 {_processedGpkFiles + 1}/{_totalGpkFiles}: {Path.GetFileName(gpkFile)}");
                            OnExtractionProgress($"处理文件 {_processedGpkFiles + 1}/{_totalGpkFiles}: {Path.GetFileName(gpkFile)}");

                            await ExtractFileAsync(gpkFile, cancellationToken);
                            _processedGpkFiles++;
                        }
                        catch (Exception ex)
                        {
                            OnExtractionError($"处理文件时出错: {gpkFile} - {ex.Message}");
                            Console.WriteLine($"处理文件时出错: {gpkFile}");
                            Console.WriteLine($"错误详情: {ex.Message}");
                        }
                    }

                    TimeSpan elapsed = DateTime.Now - _startTime;
                    Console.WriteLine("\n=== 批量处理完成 ===");
                    Console.WriteLine($"总共处理: {_processedGpkFiles}/{_totalGpkFiles} 个GPK文件");
                    Console.WriteLine($"提取的文件总数: {ExtractedFileCount}");
                    Console.WriteLine($"提取的总大小: {FormatSize(_totalExtractedSizeOverall)}");
                    Console.WriteLine($"耗时: {elapsed:mm\\:ss\\.fff}");

                    OnExtractionProgress($"批量处理完成: {_processedGpkFiles}/{_totalGpkFiles} 个GPK文件");
                }
                else
                {
                    OnExtractionError($"路径不存在: {inputPath}");
                }
            }
            catch (OperationCanceledException)
            {
                OnExtractionError("提取操作已取消");
            }
            catch (Exception ex)
            {
                OnExtractionError($"提取过程中发生错误: {ex.Message}");
                Console.WriteLine($"详细错误: {ex}");
            }
            finally
            {
                OnExtractionCompleted();
            }
        }

        private async Task ExtractFileAsync(string gpkFilePath, CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine($"加载: {gpkFilePath}...");
                OnExtractionProgress($"加载: {gpkFilePath}...");

                string outputDir = GetOutputDirectory(gpkFilePath);
                Directory.CreateDirectory(outputDir);

                var gpk = new GPK(gpkFilePath);
                TotalFilesToExtract += gpk.Files.Count;

                Console.WriteLine($"在GPK文件中找到 {gpk.Files.Count} 个文件");
                OnExtractionProgress($"在GPK文件中找到 {gpk.Files.Count} 个文件");

                for (int i = 0; i < gpk.Files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var declaration = gpk.Declarations[i];
                    var fileData = gpk.Files[i];
                    string outputPath = Path.Combine(outputDir, declaration.FileName);

                    Console.WriteLine($"正在提取 {i + 1}/{gpk.Files.Count}: {declaration.FileName} " +
                                      $"({declaration.Size} 字节，偏移量 {declaration.Offset})");
                    OnExtractionProgress($"正在提取 {i + 1}/{gpk.Files.Count}: {declaration.FileName}");

                    string? fileDir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(fileDir) && !Directory.Exists(fileDir))
                    {
                        Directory.CreateDirectory(fileDir);
                        Console.WriteLine($"创建目录: {fileDir}");
                        OnExtractionProgress($"创建目录: {fileDir}");
                    }

                    await File.WriteAllBytesAsync(outputPath, fileData, cancellationToken);

                    _totalExtractedSizeOverall += fileData.Length;

                    OnExtractionProgress($"已提取 {ExtractedFileCount + 1}/{TotalFilesToExtract}: {declaration.FileName}");
                    OnFileExtracted(declaration.FileName);
                }

                Console.WriteLine($"成功从GPK文件中提取 {gpk.Files.Count} 个文件到: {outputDir}");
                OnExtractionProgress($"成功从GPK文件中提取 {gpk.Files.Count} 个文件到: {outputDir}");
            }
            catch (Exception ex)
            {
                OnExtractionError($"提取GPK文件时出错: {ex.Message}");
                throw;
            }
        }

        private string GetOutputDirectory(string gpkFilePath)
        {
            string fileName = Path.GetFileNameWithoutExtension(gpkFilePath);
            string directory = Path.GetDirectoryName(gpkFilePath) ?? "";

            string safeFileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(directory, $"{safeFileName}_extracted");
        }
        private string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;

            while (size >= 1024 && order < suffixes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {suffixes[order]}";
        }
    }

    public class GPK
    {
        public List<GPKDeclaration> Declarations { get; set; }
        public List<byte[]> Files { get; set; }

        public GPK()
        {
            Declarations = new List<GPKDeclaration>();
            Files = new List<byte[]>();
        }

        public GPK(string path)
        {
            using var reader = new BinaryReader(File.Open(path, FileMode.Open));
            uint fileCount = reader.ReadUInt32();
            Declarations = new List<GPKDeclaration>((int)fileCount);
            Files = new List<byte[]>((int)fileCount);

            for (int i = 0; i < fileCount; i++)
            {
                Declarations.Add(new GPKDeclaration(reader));
            }

            foreach (var declaration in Declarations)
            {
                reader.BaseStream.Seek(declaration.Offset, SeekOrigin.Begin);
                Files.Add(reader.ReadBytes(declaration.Size));
            }
        }

        public void Save(string path)
        {
            using var writer = new BinaryWriter(File.Open(path, FileMode.Create));
            writer.Write(Declarations.Count);

            foreach (var declaration in Declarations)
            {
                declaration.Save(writer);
            }

            foreach (var file in Files)
            {
                writer.Write(file);
            }
        }
    }

    public class GPKDeclaration
    {
        public string FileName { get; set; }
        public uint Offset { get; set; }
        public int Size { get; set; }

        public GPKDeclaration()
        {
            FileName = "";
        }

        public GPKDeclaration(BinaryReader reader)
        {
            FileName = new string(reader.ReadChars(260)).Replace("\0", "");
            Size = reader.ReadInt32();
            Offset = reader.ReadUInt32();
        }

        public void Save(BinaryWriter writer)
        {
            for (int i = 0; i < FileName.Length; i++)
                writer.Write(FileName[i]);

            for (int i = 0; i < 260 - FileName.Length; i++)
                writer.Write((byte)0);

            writer.Write(Size);
            writer.Write(Offset);
        }
    }
}