using QRCoder;

namespace ShinyCommander.Share;

/// <summary>A QR code as dark/light modules, the four-module quiet zone included - drawn by the app.</summary>
public sealed class QrMatrix
{
    readonly bool[,] modules;

    QrMatrix(bool[,] modules) => this.modules = modules;

    public int Size => this.modules.GetLength(0);
    public bool this[int row, int column] => this.modules[row, column];

    public static QrMatrix Create(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix;
        var size = rows.Count;
        var modules = new bool[size, size];
        for (var r = 0; r < size; r++)
        {
            for (var c = 0; c < size; c++)
                modules[r, c] = rows[r][c];
        }
        return new QrMatrix(modules);
    }
}
