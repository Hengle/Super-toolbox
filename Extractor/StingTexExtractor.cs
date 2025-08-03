using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using DALLib.File;
using DALLib.IO;
using DALLib.Imaging;
using DALLib.Compression;

namespace super_toolbox
{
    public class StingTexExtractor : BaseExtractor
    {
        private readonly object _lockObject = new object();
        private int _processedFiles = 0;
        private static byte[] TargetHeader = { 0x5A, 0x4C, 0x49, 0x42 };

        public new event EventHandler<string>? FileExtracted;
        public event EventHandler<string>? ExtractionProgress;

        public override void Extract(string directoryPath) =>
            ExtractAsync(directoryPath).Wait();

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnError($"目录不存在: {directoryPath}");
                return;
            }

            var files = Directory.GetFiles(directoryPath, "*.tex", SearchOption.AllDirectories);
            TotalFilesToExtract = files.Length;
            OnProgress($"开始处理 {files.Length} 个TEX文件");

            string outputDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(outputDir);

            await Task.Run(async () =>
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        Interlocked.Increment(ref _processedFiles);
                        OnProgress($"正在处理 {_processedFiles}/{files.Length}: {Path.GetFileName(file)}");

                        await ProcessSingleTexFileAsync(file, outputDir, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        OnError($"处理失败: {Path.GetFileName(file)} - {ex.Message}");
                    }
                }
            }, cancellationToken);

            OnExtractionCompleted();
        }

        private async Task ProcessSingleTexFileAsync(string filePath, string outputDir, CancellationToken ct)
        {
            await Task.Run(() =>
            {
                byte[] fileData;
                using (var fs = File.OpenRead(filePath))
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    fileData = ms.ToArray();
                }

                bool isTargetHeader = fileData.Length >= 12 && fileData.Take(4).SequenceEqual(TargetHeader);
                if (isTargetHeader)
                {
                    fileData = fileData.Skip(12).ToArray();

                    if (fileData.Length >= 2 && fileData[0] == 0x78 && fileData[1] == 0xDA)
                    {
                        try
                        {
                            fileData = DecompressZlib(fileData);
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidDataException($"ZLIB解压失败: {ex.Message}");
                        }
                    }
                }

                string texName = Path.GetFileNameWithoutExtension(filePath);
                string texOutputDir = Path.Combine(outputDir, texName);
                Directory.CreateDirectory(texOutputDir);

                using (var ms = new MemoryStream(fileData))
                using (var reader = new ExtendedBinaryReader(ms))
                {
                    var texFile = new TEXFile();
                    texFile.Load(reader);

                    string outputPath = Path.Combine(texOutputDir, $"{texName}.png");

                    if (texFile.SheetData != null)
                    {
                        ImageTools.SaveImage(outputPath,
                            texFile.SheetWidth,
                            texFile.SheetHeight,
                            texFile.SheetData);

                        base.OnFileExtracted(Path.Combine(texName, Path.GetFileName(outputPath)));
                        FileExtracted?.Invoke(this, $"已提取: {Path.Combine(texName, Path.GetFileName(outputPath))}");
                    }
                }
            }, ct);
        }

        private byte[] DecompressZlib(byte[] compressedData)
        {
            using (var inputMs = new MemoryStream(compressedData, 2, compressedData.Length - 2))
            using (var zlibStream = new DeflateStream(inputMs, CompressionMode.Decompress))
            using (var outputMs = new MemoryStream())
            {
                zlibStream.CopyTo(outputMs);
                return outputMs.ToArray();
            }
        }

        private void OnProgress(string message) => ExtractionProgress?.Invoke(this, message);
        private void OnError(string error) => ExtractionProgress?.Invoke(this, $"错误: {error}");
    }
}