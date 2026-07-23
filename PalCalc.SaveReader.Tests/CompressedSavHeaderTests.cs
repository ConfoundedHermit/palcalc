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
}
