using System;
using System.IO;
using System.IO.Compression;

namespace PlaywrightMcpHarness.Tests.Common;

public class SimplePngImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] RgbaPixels { get; } // 4 bytes per pixel: R, G, B, A

    public SimplePngImage(int width, int height)
    {
        Width = width;
        Height = height;
        RgbaPixels = new byte[width * height * 4];
    }

    public SimplePngImage(int width, int height, byte[] rgbaPixels)
    {
        Width = width;
        Height = height;
        RgbaPixels = rgbaPixels;
    }

    public static SimplePngImage CreateBlank(int width, int height, byte r = 0, byte g = 0, byte b = 0, byte a = 255)
    {
        var img = new SimplePngImage(width, height);
        for (int i = 0; i < img.RgbaPixels.Length; i += 4)
        {
            img.RgbaPixels[i] = r;
            img.RgbaPixels[i + 1] = g;
            img.RgbaPixels[i + 2] = b;
            img.RgbaPixels[i + 3] = a;
        }
        return img;
    }

    public (byte R, byte G, byte B, byte A) GetPixel(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException($"Pixel coordinates ({x},{y}) out of bounds ({Width}x{Height})");
        }
        int offset = (y * Width + x) * 4;
        return (RgbaPixels[offset], RgbaPixels[offset + 1], RgbaPixels[offset + 2], RgbaPixels[offset + 3]);
    }

    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException($"Pixel coordinates ({x},{y}) out of bounds ({Width}x{Height})");
        }
        int offset = (y * Width + x) * 4;
        RgbaPixels[offset] = r;
        RgbaPixels[offset + 1] = g;
        RgbaPixels[offset + 2] = b;
        RgbaPixels[offset + 3] = a;
    }

    public void Save(Stream stream)
    {
        var bytes = ToPngBytes();
        stream.Write(bytes, 0, bytes.Length);
    }

    public static SimplePngImage FromStream(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return FromBytes(ms.ToArray());
    }

    public static SimplePngImage FromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return FromBytes(bytes);
    }

    public void SaveToFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(path, ToPngBytes());
    }

    public static SimplePngImage FromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);

        // Verify PNG Signature
        var sig = reader.ReadBytes(8);
        if (sig.Length < 8 || sig[0] != 0x89 || sig[1] != 0x50 || sig[2] != 0x4E || sig[3] != 0x47 ||
            sig[4] != 0x0D || sig[5] != 0x0A || sig[6] != 0x1A || sig[7] != 0x0A)
        {
            throw new FormatException("Invalid PNG signature.");
        }

        int width = 0;
        int height = 0;
        int bitDepth = 0;
        int colorType = 0;
        int interlace = 0;
        byte[]? palette = null;
        byte[]? paletteAlpha = null;
        using var idatStream = new MemoryStream();

        while (ms.Position < ms.Length)
        {
            if (ms.Length - ms.Position < 8) break;
            int length = ReadBigEndianInt32(reader);
            if (length < 0 || ms.Position + 4 + length > ms.Length)
            {
                throw new FormatException("Corrupted PNG chunk length.");
            }
            var chunkType = new string(reader.ReadChars(4));

            if (chunkType == "IHDR")
            {
                width = ReadBigEndianInt32(reader);
                height = ReadBigEndianInt32(reader);
                bitDepth = reader.ReadByte();
                colorType = reader.ReadByte();
                reader.ReadBytes(2); // compression, filter
                interlace = reader.ReadByte();
                reader.ReadInt32(); // CRC
            }
            else if (chunkType == "PLTE")
            {
                palette = reader.ReadBytes(length);
                reader.ReadInt32(); // CRC
            }
            else if (chunkType == "tRNS")
            {
                paletteAlpha = reader.ReadBytes(length);
                reader.ReadInt32(); // CRC
            }
            else if (chunkType == "IDAT")
            {
                var chunkData = reader.ReadBytes(length);
                idatStream.Write(chunkData, 0, chunkData.Length);
                reader.ReadInt32(); // CRC
            }
            else if (chunkType == "IEND")
            {
                reader.ReadInt32(); // CRC
                break;
            }
            else
            {
                reader.ReadBytes(length);
                reader.ReadInt32(); // CRC
            }
        }

        if (width <= 0 || height <= 0)
        {
            throw new FormatException("Missing or invalid IHDR chunk in PNG.");
        }

        if (interlace != 0)
        {
            throw new NotSupportedException("Interlaced PNG images are not supported.");
        }

        int bytesPerPixel = colorType switch
        {
            6 => 4, // RGBA
            4 => 2, // Grayscale + Alpha
            3 => 1, // Indexed Palette
            2 => 3, // RGB
            0 => 1, // Grayscale
            _ => throw new NotSupportedException($"PNG color type {colorType} is not supported.")
        };

        if (bitDepth != 8)
        {
            throw new NotSupportedException($"Only 8-bit PNG bit depth is supported (found {bitDepth}).");
        }

        idatStream.Position = 0;
        byte[] decompressed;
        using (var zlib = new ZLibStream(idatStream, CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            zlib.CopyTo(outMs);
            decompressed = outMs.ToArray();
        }

        var result = new SimplePngImage(width, height);
        int stride = width * bytesPerPixel;
        int srcOffset = 0;

        byte[] prevRow = new byte[stride];
        byte[] currRow = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            if (srcOffset >= decompressed.Length) break;
            byte filterType = decompressed[srcOffset++];

            for (int i = 0; i < stride; i++)
            {
                byte x = decompressed[srcOffset++];
                byte a = i >= bytesPerPixel ? currRow[i - bytesPerPixel] : (byte)0;
                byte b = prevRow[i];
                byte c = i >= bytesPerPixel ? prevRow[i - bytesPerPixel] : (byte)0;

                byte raw = filterType switch
                {
                    0 => x,                                // None
                    1 => (byte)(x + a),                    // Sub
                    2 => (byte)(x + b),                    // Up
                    3 => (byte)(x + ((a + b) >> 1)),       // Average
                    4 => (byte)(x + PaethPredictor(a, b, c)), // Paeth
                    _ => x
                };

                currRow[i] = raw;
            }

            int destRowOffset = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int destPixel = destRowOffset + x * 4;
                int srcPixel = x * bytesPerPixel;

                if (colorType == 6) // RGBA
                {
                    result.RgbaPixels[destPixel] = currRow[srcPixel];         // R
                    result.RgbaPixels[destPixel + 1] = currRow[srcPixel + 1]; // G
                    result.RgbaPixels[destPixel + 2] = currRow[srcPixel + 2]; // B
                    result.RgbaPixels[destPixel + 3] = currRow[srcPixel + 3]; // A
                }
                else if (colorType == 2) // RGB
                {
                    result.RgbaPixels[destPixel] = currRow[srcPixel];         // R
                    result.RgbaPixels[destPixel + 1] = currRow[srcPixel + 1]; // G
                    result.RgbaPixels[destPixel + 2] = currRow[srcPixel + 2]; // B
                    result.RgbaPixels[destPixel + 3] = 255;                   // A
                }
                else if (colorType == 4) // Grayscale + Alpha
                {
                    byte g = currRow[srcPixel];
                    result.RgbaPixels[destPixel] = g;
                    result.RgbaPixels[destPixel + 1] = g;
                    result.RgbaPixels[destPixel + 2] = g;
                    result.RgbaPixels[destPixel + 3] = currRow[srcPixel + 1];
                }
                else if (colorType == 3) // Indexed Palette
                {
                    byte idx = currRow[srcPixel];
                    int palOffset = idx * 3;
                    if (palette != null && palOffset + 2 < palette.Length)
                    {
                        result.RgbaPixels[destPixel] = palette[palOffset];
                        result.RgbaPixels[destPixel + 1] = palette[palOffset + 1];
                        result.RgbaPixels[destPixel + 2] = palette[palOffset + 2];
                    }
                    result.RgbaPixels[destPixel + 3] = (paletteAlpha != null && idx < paletteAlpha.Length)
                        ? paletteAlpha[idx]
                        : (byte)255;
                }
                else // Grayscale
                {
                    byte g = currRow[srcPixel];
                    result.RgbaPixels[destPixel] = g;
                    result.RgbaPixels[destPixel + 1] = g;
                    result.RgbaPixels[destPixel + 2] = g;
                    result.RgbaPixels[destPixel + 3] = 255;
                }
            }

            Array.Copy(currRow, prevRow, stride);
        }

        return result;
    }

    public byte[] ToPngBytes()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Signature
        writer.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR Chunk
        var ihdrData = new byte[13];
        WriteBigEndianInt32(ihdrData, 0, Width);
        WriteBigEndianInt32(ihdrData, 4, Height);
        ihdrData[8] = 8; // 8-bit depth
        ihdrData[9] = 6; // RGBA
        ihdrData[10] = 0; // Deflate
        ihdrData[11] = 0; // Filter
        ihdrData[12] = 0; // Non-interlaced
        WriteChunk(writer, "IHDR", ihdrData);

        // Prepare raw scanlines with Filter 0 (None)
        int stride = Width * 4;
        var uncompressedData = new byte[Height * (stride + 1)];
        int destIndex = 0;
        int srcIndex = 0;

        for (int y = 0; y < Height; y++)
        {
            uncompressedData[destIndex++] = 0; // Filter 0 (None)
            Array.Copy(RgbaPixels, srcIndex, uncompressedData, destIndex, stride);
            destIndex += stride;
            srcIndex += stride;
        }

        // Compress scanlines with ZLib
        byte[] compressedData;
        using (var compressMs = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressMs, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(uncompressedData, 0, uncompressedData.Length);
            }
            compressedData = compressMs.ToArray();
        }

        // IDAT Chunk
        WriteChunk(writer, "IDAT", compressedData);

        // IEND Chunk
        WriteChunk(writer, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static int ReadBigEndianInt32(BinaryReader reader)
    {
        var b = reader.ReadBytes(4);
        return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    }

    private static void WriteBigEndianInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WriteChunk(BinaryWriter writer, string type, byte[] data)
    {
        writer.Write((byte)((data.Length >> 24) & 0xFF));
        writer.Write((byte)((data.Length >> 16) & 0xFF));
        writer.Write((byte)((data.Length >> 8) & 0xFF));
        writer.Write((byte)(data.Length & 0xFF));

        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        writer.Write(typeBytes);
        writer.Write(data);

        uint crc = CalculateCrc(typeBytes, data);
        writer.Write((byte)((crc >> 24) & 0xFF));
        writer.Write((byte)((crc >> 16) & 0xFF));
        writer.Write((byte)((crc >> 8) & 0xFF));
        writer.Write((byte)(crc & 0xFF));
    }

    private static readonly uint[] CrcTable = InitializeCrcTable();

    private static uint[] InitializeCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                if ((c & 1) != 0)
                    c = 0xEDB88320u ^ (c >> 1);
                else
                    c >>= 1;
            }
            table[i] = c;
        }
        return table;
    }

    private static uint CalculateCrc(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = 0; i < type.Length; i++)
        {
            c = CrcTable[(c ^ type[i]) & 0xFF] ^ (c >> 8);
        }
        for (int i = 0; i < data.Length; i++)
        {
            c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }
}
