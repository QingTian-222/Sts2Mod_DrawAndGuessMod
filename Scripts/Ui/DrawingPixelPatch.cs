using Godot;

namespace DrawAndGuessMod.Scripts.Ui;

internal sealed class DrawingPixelPatch
{
    private readonly int[] _pixelIndices;
    private readonly uint[] _rgbaValues;

    private DrawingPixelPatch(int[] pixelIndices, uint[] rgbaValues)
    {
        _pixelIndices = pixelIndices;
        _rgbaValues = rgbaValues;
    }

    public static DrawingPixelPatch Between(Image before, Image after)
    {
        if (before.GetWidth() != after.GetWidth() ||
            before.GetHeight() != after.GetHeight() ||
            before.GetFormat() != Image.Format.Rgba8 ||
            after.GetFormat() != Image.Format.Rgba8)
        {
            throw new ArgumentException("Drawing pixel patches require equally sized RGBA8 images.");
        }

        byte[] beforeData = before.GetData();
        byte[] afterData = after.GetData();
        List<int> pixelIndices = new();
        List<uint> rgbaValues = new();
        int pixelCount = before.GetWidth() * before.GetHeight();
        for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            int byteIndex = pixelIndex * 4;
            if (beforeData[byteIndex] == afterData[byteIndex] &&
                beforeData[byteIndex + 1] == afterData[byteIndex + 1] &&
                beforeData[byteIndex + 2] == afterData[byteIndex + 2] &&
                beforeData[byteIndex + 3] == afterData[byteIndex + 3])
            {
                continue;
            }

            pixelIndices.Add(pixelIndex);
            rgbaValues.Add(
                (uint)(afterData[byteIndex] << 24 |
                       afterData[byteIndex + 1] << 16 |
                       afterData[byteIndex + 2] << 8 |
                       afterData[byteIndex + 3]));
        }

        return new DrawingPixelPatch(pixelIndices.ToArray(), rgbaValues.ToArray());
    }

    public void Apply(Image image)
    {
        if (image.GetFormat() != Image.Format.Rgba8)
        {
            throw new ArgumentException("Drawing pixel patches require an RGBA8 image.");
        }

        byte[] data = image.GetData();
        for (int index = 0; index < _pixelIndices.Length; index++)
        {
            int byteIndex = _pixelIndices[index] * 4;
            uint rgba = _rgbaValues[index];
            data[byteIndex] = (byte)(rgba >> 24);
            data[byteIndex + 1] = (byte)(rgba >> 16);
            data[byteIndex + 2] = (byte)(rgba >> 8);
            data[byteIndex + 3] = (byte)rgba;
        }
        image.SetData(image.GetWidth(), image.GetHeight(), false, Image.Format.Rgba8, data);
    }
}
