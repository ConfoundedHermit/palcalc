using System.IO.Compression;
using System.Text;

namespace PalCalc.SaveReader.Tests;

[TestClass]
public class CompressedSavHeaderTests
{
    [TestMethod]
    public void Read_RecognizesSingleDeflateHeader()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1234);
            writer.Write(567);
            writer.Write(Encoding.ASCII.GetBytes("PlZ1"));
        }

        stream.Position = 0;
        var header = CompressedSAVHeader.Read(stream);

        Assert.AreEqual(1234, header.UncompressedLength);
        Assert.AreEqual(567, header.CompressedLength);
        Assert.IsTrue(header.HasCompressionMarker);
        Assert.IsFalse(header.HasGamePassMarker);
        Assert.AreEqual(SaveCompressionType.SingleDeflate, header.CompressionType);
    }

    [TestMethod]
    public void Read_RecognizesGamePassDoubleDeflateHeader()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1234);
            writer.Write(567);
            writer.Write(Encoding.ASCII.GetBytes("CNK0"));
            writer.Write(new byte[8]);
            writer.Write(Encoding.ASCII.GetBytes("PlZ2"));
        }

        stream.Position = 0;
        var header = CompressedSAVHeader.Read(stream);

        Assert.IsTrue(header.HasGamePassMarker);
        Assert.IsTrue(header.HasCompressionMarker);
        Assert.AreEqual(SaveCompressionType.DoubleDeflate, header.CompressionType);
    }

    [TestMethod]
    public void WithDecompressedSave_InflatesGeneratedSingleDeflateData()
    {
        var expected = Encoding.UTF8.GetBytes("generated save payload");
        using var save = new MemoryStream(CreateSave(expected, "PlZ1"));
        byte[] actual = null!;
        CompressedSAV.WithDecompressedSave(save, stream =>
        {
            using var output = new MemoryStream();
            stream.CopyTo(output);
            actual = output.ToArray();
        });

        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void WithDecompressedSave_InflatesGeneratedDoubleDeflateData()
    {
        var expected = Encoding.UTF8.GetBytes("generated double-deflate save payload");
        using var save = new MemoryStream(CreateSave(Compress(Compress(expected)), "PlZ2", expected.Length));
        byte[] actual = null!;

        CompressedSAV.WithDecompressedSave(save, stream =>
        {
            using var output = new MemoryStream();
            stream.CopyTo(output);
            actual = output.ToArray();
        });

        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void WithDecompressedSave_CombinesGeneratedSplitFilesInSpecifiedOrder()
    {
        var expected = Encoding.UTF8.GetBytes(new string('x', 1_024));
        var compressed = Compress(expected);
        var splitIndex = compressed.Length / 2;
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"palcalc-save-reader-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var firstPath = Path.Combine(tempDirectory, "part-1.sav");
            var secondPath = Path.Combine(tempDirectory, "part-2.sav");
            File.WriteAllBytes(firstPath, CreateSave(compressed[..splitIndex], "PlZ1", expected.Length, compressed.Length));
            File.WriteAllBytes(secondPath, CreateSave(compressed[splitIndex..], "PlZ1", expected.Length));

            byte[] actual = null!;
            CompressedSAV.WithDecompressedSave([firstPath, secondPath], stream =>
            {
                using var output = new MemoryStream();
                stream.CopyTo(output);
                actual = output.ToArray();
            });

            CollectionAssert.AreEqual(expected, actual);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void Read_ThrowsForTruncatedHeader()
    {
        using var stream = new MemoryStream([0x01, 0x02, 0x03]);

        var exception = Assert.ThrowsException<InvalidDataException>(() => CompressedSAVHeader.Read(stream));

        StringAssert.Contains(exception.Message, "truncated");
    }

    [TestMethod]
    public void WithDecompressedSave_RejectsUnknownCompressionMarkerWithActionableMessage()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"palcalc-save-reader-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var path = Path.Combine(tempDirectory, "invalid-marker.sav");
            File.WriteAllBytes(path, CreateSave(Encoding.UTF8.GetBytes("invalid"), "BAD!"));

            var exception = Assert.ThrowsException<InvalidDataException>(() => CompressedSAV.WithDecompressedSave(path, _ => { }));

            StringAssert.Contains(exception.Message, "compression marker");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static byte[] CreateSave(byte[] uncompressed, string marker) => CreateSave(Compress(uncompressed), marker, uncompressed.Length);

    private static byte[] CreateSave(byte[] compressed, string marker, int uncompressedLength, int? compressedLength = null)
    {
        using var save = new MemoryStream();
        using (var writer = new BinaryWriter(save, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(uncompressedLength);
            writer.Write(compressedLength ?? compressed.Length);
            writer.Write(Encoding.ASCII.GetBytes(marker));
            writer.Write(compressed);
        }

        return save.ToArray();
    }

    private static byte[] Compress(byte[] input)
    {
        using var compressed = new MemoryStream();
        using (var compressor = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(input);

        return compressed.ToArray();
    }
}
