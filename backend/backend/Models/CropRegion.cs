namespace backend.Models;

/// <summary>
/// 0~1 범위의 상대 좌표로 표현된 크롭 영역. WpfApp1의 CropRegion과 동일한 의미.
/// </summary>
public struct CropRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public static CropRegion Full => new() { X = 0, Y = 0, Width = 1, Height = 1 };

    public bool IsFull =>
        Math.Abs(X) < 0.001 &&
        Math.Abs(Y) < 0.001 &&
        Math.Abs(Width - 1) < 0.001 &&
        Math.Abs(Height - 1) < 0.001;
}
