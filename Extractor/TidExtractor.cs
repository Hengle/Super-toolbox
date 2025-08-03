using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class TidExtractor : BaseExtractor
    {
        private readonly object _lockObject = new object();

        public new event EventHandler<string>? FileExtracted;
        public event EventHandler<string>? ExtractionProgress;

        public TidExtractor() { }

        public override void Extract(string directoryPath)
        {
            ExtractAsync(directoryPath).Wait();
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                ExtractionProgress?.Invoke(this, $"错误: {directoryPath} 不是有效的目录");
                OnExtractionFailed($"错误: {directoryPath} 不是有效的目录");
                return;
            }

            string extractedRootDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedRootDir);

            var tidFiles = Directory.GetFiles(directoryPath, "*.tid", SearchOption.AllDirectories)
                .Where(file => !file.StartsWith(extractedRootDir, StringComparison.OrdinalIgnoreCase))
                .ToList();

            TotalFilesToExtract = tidFiles.Count;
            ExtractionProgress?.Invoke(this, $"发现 {TotalFilesToExtract} 个TID文件");
            OnFileExtracted($"发现 {TotalFilesToExtract} 个TID文件");

            try
            {
                await Task.Run(() =>
                {
                    Parallel.ForEach(tidFiles, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount
                    }, tidFilePath =>
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string tidFileName = Path.GetFileNameWithoutExtension(tidFilePath);
                            ExtractionProgress?.Invoke(this, $"正在处理: {tidFileName}");

                            using (var fileStream = new FileStream(tidFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (var binaryReader = new EndianBinaryReader(fileStream, Endianness.LittleEndian))
                            {
                                binaryReader.BaseStream.Seek(0, SeekOrigin.Begin);

                                string idString = new string(binaryReader.ReadChars(3));
                                if (idString != "TID")
                                {
                                    ExtractionProgress?.Invoke(this, $"错误: {tidFileName} 不是有效的TID文件");
                                    return;
                                }

                                byte flags = binaryReader.ReadByte();

                                if ((flags & 0x80) != 0)
                                {
                                    binaryReader.Endianness = ((flags & 1) != 0) ? Endianness.BigEndian : Endianness.LittleEndian;
                                }
                                else
                                {
                                    ExtractionProgress?.Invoke(this, $"{Path.GetFileName(tidFilePath)}: Unknown FLAGS. ({flags:X})");
                                    return;
                                }

                                binaryReader.BaseStream.Seek(0x44, SeekOrigin.Begin);
                                int width = binaryReader.ReadInt32();
                                int height = binaryReader.ReadInt32();
                                binaryReader.BaseStream.Seek(0x58, SeekOrigin.Begin);
                                int imgSize = binaryReader.ReadInt32();
                                binaryReader.BaseStream.Seek(0x64, SeekOrigin.Begin);
                                int dxt = binaryReader.ReadInt32();
                                binaryReader.BaseStream.Seek(0x80, SeekOrigin.Begin);

                                int pitch;
                                if ((flags & 0x80) != 0)
                                {
                                    if (dxt == 0)
                                    {
                                        pitch = (width * 32 + 7) / 8;
                                    }
                                    else if (dxt == 0x31545844)
                                    {
                                        pitch = ((width + 3) / 4) * 8;
                                    }
                                    else if (dxt == 0x33545844 || dxt == 0x35545844 || dxt == 0x20374342)
                                    {
                                        pitch = ((width + 3) / 4) * 16;
                                    }
                                    else
                                    {
                                        ExtractionProgress?.Invoke(this, $"{Path.GetFileName(tidFilePath)}: Unknown FLAGS ({flags:X}) and DXT ({dxt:X}) pair.");
                                        return;
                                    }
                                }
                                else
                                {
                                    ExtractionProgress?.Invoke(this, $"{Path.GetFileName(tidFilePath)}: Unknown FLAGS ({flags:X}) and DXT ({dxt:X}) pair.");
                                    return;
                                }

                                byte[] memoryFile;
                                if (dxt == 0x20374342)
                                {
                                    memoryFile = new byte[148 + imgSize];
                                    CreateBC7DDSHeader(memoryFile, width, height, pitch, imgSize);
                                }
                                else
                                {
                                    memoryFile = new byte[128 + imgSize];
                                    CreateStandardDDSHeader(memoryFile, width, height, pitch, imgSize, dxt, flags);
                                }

                                binaryReader.Read(memoryFile, memoryFile.Length - imgSize, imgSize);

                                string outputName = Path.GetFileNameWithoutExtension(tidFilePath);
                                if ((flags & 0x8) != 0)
                                {
                                    outputName += "_FLIP_VERTICALLY";
                                }
                                outputName += ".dds";

                                string outputPath = Path.Combine(extractedRootDir, outputName);
                                File.WriteAllBytes(outputPath, memoryFile);

                                base.OnFileExtracted(outputName);
                                FileExtracted?.Invoke(this, $"已提取: {outputName}");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            ExtractionProgress?.Invoke(this, $"处理 {Path.GetFileName(tidFilePath)} 时出错: {ex.Message}");
                        }
                    });
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ExtractionProgress?.Invoke(this, "操作已取消");
                OnExtractionFailed("操作已取消");
                throw;
            }
            catch (Exception ex)
            {
                ExtractionProgress?.Invoke(this, $"提取失败: {ex.Message}");
                OnExtractionFailed($"提取失败: {ex.Message}");
                throw;
            }

            ExtractionProgress?.Invoke(this, $"完成! 共提取 {ExtractedFileCount} 个文件");
        }

        private void CreateBC7DDSHeader(byte[] buffer, int width, int height, int pitch, int imgSize)
        {
            // BC7 DX10 DDS header template
            byte[] bc7HeaderTemplate = {
        0x44, 0x44, 0x53, 0x20, 0x7C, 0x00, 0x00, 0x00, 0x07, 0x10, 0x0A, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
        0x44, 0x58, 0x31, 0x30, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x62, 0x00, 0x00, 0x00,
        0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    };

            Array.Copy(bc7HeaderTemplate, buffer, bc7HeaderTemplate.Length);

            // Set width and height
            BitConverter.GetBytes(height).CopyTo(buffer, 12);
            BitConverter.GetBytes(width).CopyTo(buffer, 16);

            // Set pitch/linear size
            BitConverter.GetBytes(imgSize).CopyTo(buffer, 20);

            // For BC7, we need to set the DX10 header values correctly
            // DX10 format (BC7_UNORM)
            BitConverter.GetBytes(98).CopyTo(buffer, 0x80); // DXGI_FORMAT_BC7_UNORM = 98
        }

        private void CreateStandardDDSHeader(byte[] buffer, int width, int height, int pitch, int imgSize, int dxt, byte flags)
        {
            BitConverter.GetBytes(0x20534444).CopyTo(buffer, 0); // "DDS "
            BitConverter.GetBytes(124).CopyTo(buffer, 4);        // header size
            BitConverter.GetBytes(0x81007).CopyTo(buffer, 8);    // flags
            BitConverter.GetBytes(height).CopyTo(buffer, 12);
            BitConverter.GetBytes(width).CopyTo(buffer, 16);
            BitConverter.GetBytes(pitch > 0 ? pitch : imgSize).CopyTo(buffer, 20); // pitch or linear size
            BitConverter.GetBytes(0).CopyTo(buffer, 24);         // depth
            BitConverter.GetBytes(1).CopyTo(buffer, 28);         // mipmap count
            new byte[44].CopyTo(buffer, 32);                     // reserved
            BitConverter.GetBytes(32).CopyTo(buffer, 76);        // pixel format size

            if (dxt == 0) // Uncompressed
            {
                BitConverter.GetBytes(0x41).CopyTo(buffer, 80);   // flags
                BitConverter.GetBytes(0).CopyTo(buffer, 84);      // fourCC
                BitConverter.GetBytes(32).CopyTo(buffer, 88);     // RGB bit count
                BitConverter.GetBytes(0x000000FF).CopyTo(buffer, 92);  // R mask
                BitConverter.GetBytes(0x0000FF00).CopyTo(buffer, 96);  // G mask
                BitConverter.GetBytes(0x00FF0000).CopyTo(buffer, 100); // B mask
                BitConverter.GetBytes(0xFF000000).CopyTo(buffer, 104); // A mask
            }
            else // Compressed (DXT1/DXT3/DXT5)
            {
                BitConverter.GetBytes(0x04).CopyTo(buffer, 80);   // flags (DDPF_FOURCC)
                BitConverter.GetBytes(dxt).CopyTo(buffer, 84);    // fourCC
                BitConverter.GetBytes(0).CopyTo(buffer, 88);      // RGB bit count
                BitConverter.GetBytes(0).CopyTo(buffer, 92);       // R mask
                BitConverter.GetBytes(0).CopyTo(buffer, 96);       // G mask
                BitConverter.GetBytes(0).CopyTo(buffer, 100);      // B mask
                BitConverter.GetBytes(0).CopyTo(buffer, 104);      // A mask
            }

            BitConverter.GetBytes(0x1000).CopyTo(buffer, 108);    // caps
            BitConverter.GetBytes(0).CopyTo(buffer, 112);         // caps2
            BitConverter.GetBytes(0).CopyTo(buffer, 116);         // caps3
            BitConverter.GetBytes(0).CopyTo(buffer, 120);         // caps4
            BitConverter.GetBytes(0).CopyTo(buffer, 124);         // reserved
        }

        public enum Endianness
        {
            LittleEndian,
            BigEndian
        }

        public class EndianBinaryReader : BinaryReader
        {
            public Endianness Endianness { get; set; }

            public EndianBinaryReader(Stream input, Endianness endianness) : base(input)
            {
                Endianness = endianness;
            }

            public override short ReadInt16()
            {
                if (Endianness == Endianness.LittleEndian)
                    return base.ReadInt16();
                else
                    return Swap(base.ReadInt16());
            }

            public override int ReadInt32()
            {
                if (Endianness == Endianness.LittleEndian)
                    return base.ReadInt32();
                else
                    return Swap(base.ReadInt32());
            }

            public override long ReadInt64()
            {
                if (Endianness == Endianness.LittleEndian)
                    return base.ReadInt64();
                else
                    return Swap(base.ReadInt64());
            }

            private short Swap(short value)
            {
                return (short)((value >> 8) | (value << 8));
            }

            private int Swap(int value)
            {
                return (int)((Swap((short)value) << 16) | (Swap((short)(value >> 16)) & 0xFFFF));
            }

            private long Swap(long value)
            {
                return ((long)Swap((int)value) << 32) | (Swap((int)(value >> 32)) & 0xFFFFFFFFL);
            }
        }

        public class EndianBinaryWriter : BinaryWriter
        {
            public Endianness Endianness { get; set; }

            public EndianBinaryWriter(Stream input, Endianness endianness) : base(input)
            {
                Endianness = endianness;
            }

            public override void Write(short value)
            {
                if (Endianness == Endianness.LittleEndian)
                    base.Write(value);
                else
                    base.Write(Swap(value));
            }

            public override void Write(int value)
            {
                if (Endianness == Endianness.LittleEndian)
                    base.Write(value);
                else
                    base.Write(Swap(value));
            }

            public override void Write(long value)
            {
                if (Endianness == Endianness.LittleEndian)
                    base.Write(value);
                else
                    base.Write(Swap(value));
            }

            private short Swap(short value)
            {
                return (short)((value >> 8) | (value << 8));
            }

            private int Swap(int value)
            {
                return (int)((Swap((short)value) << 16) | (Swap((short)(value >> 16)) & 0xFFFF));
            }

            private long Swap(long value)
            {
                return ((long)Swap((int)value) << 32) | (Swap((int)(value >> 32)) & 0xFFFFFFFFL);
            }
        }
    }
}