using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LZ4;

namespace super_toolbox
{
    public class SctExtractor : BaseExtractor
    {
        private static readonly byte[] SCT_SIGNATURE = { 0x53, 0x43, 0x54, 0x01 };
        private static readonly int SIGNATURE_LENGTH = SCT_SIGNATURE.Length;

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            string extractedDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedDir);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var files = Directory.EnumerateFiles(directoryPath, "*.sct", SearchOption.AllDirectories)
               .Where(file => !file.StartsWith(extractedDir, StringComparison.OrdinalIgnoreCase))
               .ToList();

            TotalFilesToExtract = files.Count;

            var successfulExtractions = new ConcurrentBag<string>();
            var failedFiles = new ConcurrentBag<string>();

            try
            {
                await Task.Run(() =>
                {
                    Parallel.ForEach(files, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount * 2,
                        CancellationToken = cancellationToken
                    }, filePath =>
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string outputPath = Path.Combine(extractedDir, $"{Path.GetFileNameWithoutExtension(filePath)}.png");
                            ConvertSctToPng(filePath, outputPath);
                            successfulExtractions.Add(outputPath);
                            OnFileExtracted(outputPath);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            failedFiles.Add(filePath);
                            Console.WriteLine($"提取失败: {filePath} - {ex.Message}");
                        }
                    });
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("提取操作已取消。");
            }

            sw.Stop();

            Console.WriteLine($"处理完成，耗时 {sw.Elapsed.TotalSeconds:F2} 秒");
            Console.WriteLine($"共找到 {TotalFilesToExtract} 个SCT文件");
            Console.WriteLine($"成功提取 {ExtractedFileCount} 个PNG文件");
            Console.WriteLine($"失败 {failedFiles.Count} 个文件");

            if (failedFiles.Count > 0)
            {
                Console.WriteLine("\n失败文件列表:");
                foreach (var file in failedFiles)
                {
                    Console.WriteLine(file);
                }
            }

            if (ExtractedFileCount != TotalFilesToExtract - failedFiles.Count)
            {
                Console.WriteLine("警告: 统计数量与实际文件数量存在差异");
            }

            OnExtractionCompleted();
        }

        private void ConvertSctToPng(string inputPath, string outputPath)
        {
            try
            {
                using (FileStream fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read))
                {
                    byte[] signature = new byte[SIGNATURE_LENGTH];
                    fs.Read(signature, 0, SIGNATURE_LENGTH);

                    if (!signature.SequenceEqual(SCT_SIGNATURE))
                        throw new InvalidDataException("不是有效的SCT文件");

                    int texType = fs.ReadByte();
                    int width = BitConverter.ToInt16(ReadBytes(fs, 2), 0);
                    int height = BitConverter.ToInt16(ReadBytes(fs, 2), 0);
                    int decLen = BitConverter.ToInt32(ReadBytes(fs, 4), 0);
                    int comLen = BitConverter.ToInt32(ReadBytes(fs, 4), 0);

                    byte[] compressedData = ReadBytes(fs, comLen);
                    byte[] data = LZ4Codec.Decode(compressedData, 0, comLen, decLen);

                    Bitmap bitmap;
                    if (texType == 2)
                    {
                        bitmap = CreateBitmapFromRGBA(data, width, height);
                    }
                    else if (texType == 4)
                    {
                        bitmap = CreateBitmapFromRGBA5551(data, width, height);
                    }
                    else
                    {
                        throw new NotSupportedException($"不支持的纹理类型: {texType}");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    bitmap.Save(outputPath, ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"转换失败: {inputPath} - {ex.Message}");
                throw;
            }
        }

        private Bitmap CreateBitmapFromRGBA(byte[] data, int width, int height)
        {
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(data, 0, bmpData.Scan0, data.Length);
            bitmap.UnlockBits(bmpData);
            return bitmap;
        }

        private Bitmap CreateBitmapFromRGBA5551(byte[] data, int width, int height)
        {
            int start = width * height * 2;
            byte[] rgb555Data = new byte[start];
            byte[] alphaData = new byte[width * height];

            Array.Copy(data, 0, rgb555Data, 0, start);
            Array.Copy(data, start, alphaData, 0, width * height);

            byte[] rgbaData = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                ushort rgb555 = BitConverter.ToUInt16(rgb555Data, i * 2);
                byte r = (byte)(((((rgb555 >> 10) & 0x1F) * 527) + 23) >> 6);
                byte g = (byte)(((((rgb555 >> 5) & 0x1F) * 527) + 23) >> 6);
                byte b = (byte)(((rgb555 & 0x1F) * 527 + 23) >> 6);
                byte a = alphaData[i];

                rgbaData[i * 4] = r;
                rgbaData[i * 4 + 1] = g;
                rgbaData[i * 4 + 2] = b;
                rgbaData[i * 4 + 3] = a;
            }

            return CreateBitmapFromRGBA(rgbaData, width, height);
        }

        private byte[] ReadBytes(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int bytesRead = 0;
            while (bytesRead < count)
            {
                int read = stream.Read(buffer, bytesRead, count - bytesRead);
                if (read == 0)
                    throw new EndOfStreamException();
                bytesRead += read;
            }
            return buffer;
        }
    }
}