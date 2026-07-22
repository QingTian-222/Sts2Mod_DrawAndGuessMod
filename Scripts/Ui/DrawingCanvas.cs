using System.Collections.Generic;
using DrawAndGuessMod.Scripts.Networking;
using Godot;

namespace DrawAndGuessMod.Scripts.Ui;

public partial class DrawingCanvas : Control
{
    public const int CanvasWidth = 500;
    public const int CanvasHeight = 380;
    public const int MinBrushSize = 4;
    public const int MaxBrushSize = 48;
    public const int DefaultBrushSize = 14;
    private const int MinStampSize = 40;
    private const int MaxStampSize = 192;
    private static readonly Color StampPreviewModulate = new(1f, 1f, 1f, 0.45f);
    private static readonly Color EraserPreviewFill = new(1f, 1f, 1f, 0.18f);
    private static readonly Color EraserPreviewOutline = new(0.12f, 0.12f, 0.12f, 0.92f);

    private enum DrawingTool
    {
        Brush,
        Eraser,
        Fill,
        Stamp
    }

    private readonly Color _paperColor = new("F4EEDC");
    private Image _image = null!;
    private ImageTexture _texture = null!;
    private Color _brushColor = new("1B1A18");
    private DrawingTool _tool = DrawingTool.Brush;
    private Image? _stampImage;
    private ImageTexture? _stampPreviewTexture;
    private byte _stampIndex;
    private byte _brushSize = DefaultBrushSize;
    private uint _nextOperationId = 1u;
    private uint _activeStrokeOperationId;
    private readonly Dictionary<byte, Image> _stampImages = new();
    private readonly Dictionary<(byte StampIndex, byte BrushSize), Image> _scaledStampImages = new();
    private bool _drawing;
    private bool _erasing;
    private bool _batchApplying;
    private bool _pointerInside;
    private Vector2 _lastPixel;
    private Vector2 _pointerPosition;

    internal event Action<DrawingCommand>? LocalCommandGenerated;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(CanvasWidth, CanvasHeight);
        ClipContents = true;
        MouseDefaultCursorShape = CursorShape.Cross;
        MouseFilter = MouseFilterEnum.Stop;
        _image = Image.CreateEmpty(CanvasWidth, CanvasHeight, false, Image.Format.Rgba8);
        _image.Fill(_paperColor);
        _texture = ImageTexture.CreateFromImage(_image);
        MouseEntered += OnMouseEnteredCanvas;
        MouseExited += OnMouseExitedCanvas;
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawTextureRect(_texture, new Rect2(Vector2.Zero, Size), false);
        if (_tool == DrawingTool.Stamp && _pointerInside && !_drawing && _stampImage != null && _stampPreviewTexture != null)
        {
            Vector2 previewSize = new(
                _stampImage.GetWidth() * Size.X / CanvasWidth,
                _stampImage.GetHeight() * Size.Y / CanvasHeight);
            Rect2 previewRect = new(_pointerPosition - previewSize / 2f, previewSize);
            DrawTextureRect(_stampPreviewTexture, previewRect, false, StampPreviewModulate);
        }
        else if (_tool == DrawingTool.Eraser && _pointerInside)
        {
            float displayScale = Mathf.Min(Size.X / CanvasWidth, Size.Y / CanvasHeight);
            float radius = Mathf.Max(2f, _brushSize * displayScale / 2f);
            DrawCircle(_pointerPosition, radius, EraserPreviewFill, true, -1f, true);
            DrawCircle(_pointerPosition, radius, EraserPreviewOutline, false, 2f, true);
        }
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("6D624E"), false, 3f);
    }

    public override void _Process(double delta)
    {
        if (_drawing && !Input.IsMouseButtonPressed(MouseButton.Left) && !Input.IsMouseButtonPressed(MouseButton.Right))
        {
            FinishStroke();
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton button && (button.ButtonIndex == MouseButton.Left || button.ButtonIndex == MouseButton.Right))
        {
            if (!button.Pressed)
            {
                FinishStroke();
                AcceptEvent();
                return;
            }

            _lastPixel = ToPixel(button.Position);
            if (button.ButtonIndex == MouseButton.Right)
            {
                _activeStrokeOperationId = NextOperationId();
                _drawing = true;
                _erasing = true;
                PaintLineLocal(_lastPixel, _lastPixel);
            }
            else if (_tool == DrawingTool.Brush || _tool == DrawingTool.Eraser)
            {
                _activeStrokeOperationId = NextOperationId();
                _drawing = true;
                _erasing = _tool == DrawingTool.Eraser;
                PaintLineLocal(_lastPixel, _lastPixel);
            }
            else if (_tool == DrawingTool.Fill)
            {
                FloodFillLocal(Mathf.RoundToInt(_lastPixel.X), Mathf.RoundToInt(_lastPixel.Y));
            }
            else if (_stampImage != null)
            {
                PaintStampLocal(Mathf.RoundToInt(_lastPixel.X), Mathf.RoundToInt(_lastPixel.Y));
            }

            AcceptEvent();
            return;
        }

        if (@event is InputEventMouseMotion motion)
        {
            _pointerInside = true;
            _pointerPosition = motion.Position;
            if (HasPointerPreview())
            {
                QueueRedraw();
            }

            if (!_drawing)
            {
                return;
            }

            Vector2 current = ToPixel(motion.Position);
            PaintLineLocal(_lastPixel, current);
            _lastPixel = current;
            AcceptEvent();
        }
    }

    public void ClearCanvas()
    {
        ApplyClear();
        LocalCommandGenerated?.Invoke(DrawingCommand.Clear(NextOperationId()));
    }

    public void SetBrushColor(Color color)
    {
        _brushColor = DrawingCommand.UnpackRgb(DrawingCommand.PackRgb(color));
    }

    public void SetBrushTool()
    {
        _tool = DrawingTool.Brush;
        QueueRedraw();
    }

    public void SetEraserTool()
    {
        _tool = DrawingTool.Eraser;
        QueueRedraw();
    }

    public void SetFillTool()
    {
        _tool = DrawingTool.Fill;
        QueueRedraw();
    }

    public void SetBrushSize(int size)
    {
        _brushSize = (byte)Mathf.Clamp(size, MinBrushSize, MaxBrushSize);
        if (HasPointerPreview())
        {
            if (_tool == DrawingTool.Stamp)
            {
                UpdateSelectedStampImage();
            }
            else
            {
                QueueRedraw();
            }
        }
    }

    public bool RegisterStamp(byte stampIndex, Texture2D texture)
    {
        try
        {
            Image image = texture.GetImage();
            if (image.IsEmpty() || image.IsCompressed() && image.Decompress() != Error.Ok)
            {
                return false;
            }

            image.Convert(Image.Format.Rgba8);
            _stampImages[stampIndex] = image;
            foreach ((byte StampIndex, byte BrushSize) key in _scaledStampImages.Keys.Where(key => key.StampIndex == stampIndex).ToList())
            {
                _scaledStampImages.Remove(key);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SetStampTool(byte stampIndex)
    {
        if (!_stampImages.TryGetValue(stampIndex, out Image? image))
        {
            return false;
        }

        _stampIndex = stampIndex;
        _tool = DrawingTool.Stamp;
        UpdateSelectedStampImage();
        Vector2 localMouse = GetLocalMousePosition();
        _pointerInside = new Rect2(Vector2.Zero, Size).HasPoint(localMouse);
        _pointerPosition = localMouse;
        QueueRedraw();
        return true;
    }

    internal void ApplyRemote(DrawingCommand command)
    {
        switch (command.Kind)
        {
            case DrawingCommandKind.Line:
                ApplyLine(
                    new Vector2(command.X1, command.Y1),
                    new Vector2(command.X2, command.Y2),
                    DrawingCommand.UnpackRgb(command.ColorRgb),
                    command.Erasing,
                    command.BrushSize);
                break;
            case DrawingCommandKind.StrokeEnd:
                break;
            case DrawingCommandKind.Fill:
                ApplyFloodFill(command.X1, command.Y1, DrawingCommand.UnpackRgb(command.ColorRgb));
                break;
            case DrawingCommandKind.Stamp:
                Image? stamp = GetScaledStampImage(command.StampIndex, command.BrushSize);
                if (stamp != null)
                {
                    ApplyStamp(command.X1, command.Y1, stamp);
                }
                break;
            case DrawingCommandKind.Clear:
                ApplyClear();
                break;
        }
    }

    internal bool ImportPng(byte[] pngBytes)
    {
        Image imported = new();
        if (imported.LoadPngFromBuffer(pngBytes) != Error.Ok)
        {
            return false;
        }

        imported.Convert(Image.Format.Rgba8);
        imported.Resize(CanvasWidth, CanvasHeight, Image.Interpolation.Lanczos);
        _drawing = false;
        _erasing = false;
        _activeStrokeOperationId = 0u;
        _image = imported;
        _texture.Update(_image);
        QueueRedraw();
        return true;
    }

    public Image Snapshot()
    {
        return Image.CreateFromData(_image.GetWidth(), _image.GetHeight(), false, _image.GetFormat(), _image.GetData());
    }

    public byte[] ExportPng()
    {
        return _image.SavePngToBuffer();
    }

    private Vector2 ToPixel(Vector2 position)
    {
        float x = Mathf.Clamp(position.X / Mathf.Max(Size.X, 1f) * CanvasWidth, 0f, CanvasWidth - 1f);
        float y = Mathf.Clamp(position.Y / Mathf.Max(Size.Y, 1f) * CanvasHeight, 0f, CanvasHeight - 1f);
        return new Vector2(x, y);
    }

    private void PaintLineLocal(Vector2 from, Vector2 to)
    {
        ApplyLine(from, to, _brushColor, _erasing, _brushSize);
        LocalCommandGenerated?.Invoke(DrawingCommand.Line(
            ToUShort(from.X),
            ToUShort(from.Y),
            ToUShort(to.X),
            ToUShort(to.Y),
            _brushColor,
            _erasing,
            _brushSize,
            _activeStrokeOperationId));
    }

    private void ApplyLine(Vector2 from, Vector2 to, Color color, bool erasing, byte brushSize)
    {
        float distance = from.DistanceTo(to);
        int steps = Mathf.Max(1, Mathf.CeilToInt(distance / 2f));
        for (int i = 0; i <= steps; i++)
        {
            Vector2 point = from.Lerp(to, i / (float)steps);
            PaintBrush(Mathf.RoundToInt(point.X), Mathf.RoundToInt(point.Y), color, erasing, brushSize);
        }
        RefreshTexture();
    }

    private void PaintBrush(int centerX, int centerY, Color brushColor, bool erasing, byte brushSize)
    {
        int radius = Mathf.Clamp(brushSize, MinBrushSize, MaxBrushSize) / 2;
        Color color = erasing ? _paperColor : brushColor;
        int radiusSquared = radius * radius;
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (x * x + y * y > radiusSquared)
                {
                    continue;
                }
                int px = centerX + x;
                int py = centerY + y;
                if (px >= 0 && py >= 0 && px < CanvasWidth && py < CanvasHeight)
                {
                    _image.SetPixel(px, py, color);
                }
            }
        }
    }

    private void FloodFillLocal(int startX, int startY)
    {
        if (ApplyFloodFill(startX, startY, _brushColor))
        {
            LocalCommandGenerated?.Invoke(DrawingCommand.Fill(
                (ushort)startX,
                (ushort)startY,
                _brushColor,
                NextOperationId()));
        }
    }

    private bool ApplyFloodFill(int startX, int startY, Color fillColor)
    {
        Color target = _image.GetPixel(startX, startY);
        if (ColorsAreClose(target, fillColor))
        {
            return false;
        }

        bool[] visited = new bool[CanvasWidth * CanvasHeight];
        Queue<Vector2I> pending = new();
        EnqueueFillPoint(pending, visited, startX, startY);
        while (pending.Count > 0)
        {
            Vector2I point = pending.Dequeue();
            if (!ColorsAreClose(_image.GetPixel(point.X, point.Y), target))
            {
                continue;
            }

            _image.SetPixel(point.X, point.Y, fillColor);
            EnqueueFillPoint(pending, visited, point.X - 1, point.Y);
            EnqueueFillPoint(pending, visited, point.X + 1, point.Y);
            EnqueueFillPoint(pending, visited, point.X, point.Y - 1);
            EnqueueFillPoint(pending, visited, point.X, point.Y + 1);
        }

        RefreshTexture();
        return true;
    }

    private static void EnqueueFillPoint(Queue<Vector2I> pending, bool[] visited, int x, int y)
    {
        if (x < 0 || y < 0 || x >= CanvasWidth || y >= CanvasHeight)
        {
            return;
        }

        int index = y * CanvasWidth + x;
        if (visited[index])
        {
            return;
        }

        visited[index] = true;
        pending.Enqueue(new Vector2I(x, y));
    }

    private static bool ColorsAreClose(Color left, Color right)
    {
        float red = left.R - right.R;
        float green = left.G - right.G;
        float blue = left.B - right.B;
        float alpha = left.A - right.A;
        return red * red + green * green + blue * blue + alpha * alpha <= 0.0025f;
    }

    private void PaintStampLocal(int centerX, int centerY)
    {
        if (_stampImage == null)
        {
            return;
        }

        if (ApplyStamp(centerX, centerY, _stampImage))
        {
            LocalCommandGenerated?.Invoke(DrawingCommand.Stamp(
                (ushort)centerX,
                (ushort)centerY,
                _stampIndex,
                _brushSize,
                NextOperationId()));
        }
    }

    private void UpdateSelectedStampImage()
    {
        _stampImage = GetScaledStampImage(_stampIndex, _brushSize);
        _stampPreviewTexture = _stampImage == null ? null : ImageTexture.CreateFromImage(_stampImage);
        QueueRedraw();
    }

    private Image? GetScaledStampImage(byte stampIndex, byte brushSize)
    {
        byte normalizedBrushSize = (byte)Mathf.Clamp(brushSize, MinBrushSize, MaxBrushSize);
        (byte StampIndex, byte BrushSize) key = (stampIndex, normalizedBrushSize);
        if (_scaledStampImages.TryGetValue(key, out Image? cached))
        {
            return cached;
        }
        if (!_stampImages.TryGetValue(stampIndex, out Image? source))
        {
            return null;
        }

        int stampSize = GetStampPixelSize(normalizedBrushSize);
        Image scaled = Image.CreateFromData(source.GetWidth(), source.GetHeight(), false, source.GetFormat(), source.GetData());
        scaled.Resize(stampSize, stampSize, Image.Interpolation.Lanczos);
        _scaledStampImages[key] = scaled;
        return scaled;
    }

    private static int GetStampPixelSize(byte brushSize)
    {
        float ratio = (Mathf.Clamp(brushSize, MinBrushSize, MaxBrushSize) - MinBrushSize) /
                      (float)(MaxBrushSize - MinBrushSize);
        return Mathf.RoundToInt(Mathf.Lerp(MinStampSize, MaxStampSize, ratio));
    }

    private void OnMouseEnteredCanvas()
    {
        _pointerInside = true;
        _pointerPosition = GetLocalMousePosition();
        if (HasPointerPreview())
        {
            QueueRedraw();
        }
    }

    private void OnMouseExitedCanvas()
    {
        _pointerInside = false;
        if (HasPointerPreview())
        {
            QueueRedraw();
        }
    }

    private bool HasPointerPreview()
    {
        return _tool is DrawingTool.Eraser or DrawingTool.Stamp;
    }

    private bool ApplyStamp(int centerX, int centerY, Image stampImage)
    {

        int destinationX = centerX - stampImage.GetWidth() / 2;
        int destinationY = centerY - stampImage.GetHeight() / 2;
        int sourceX = Math.Max(0, -destinationX);
        int sourceY = Math.Max(0, -destinationY);
        destinationX = Math.Max(0, destinationX);
        destinationY = Math.Max(0, destinationY);
        int width = Math.Min(stampImage.GetWidth() - sourceX, CanvasWidth - destinationX);
        int height = Math.Min(stampImage.GetHeight() - sourceY, CanvasHeight - destinationY);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        _image.BlendRect(stampImage, new Rect2I(sourceX, sourceY, width, height), new Vector2I(destinationX, destinationY));
        RefreshTexture();
        return true;
    }

    private void ApplyClear()
    {
        _image.Fill(_paperColor);
        RefreshTexture();
    }

    internal void RebuildFromCommands(IEnumerable<DrawingCommand> commands)
    {
        _batchApplying = true;
        try
        {
            _image.Fill(_paperColor);
            foreach (DrawingCommand command in commands)
            {
                ApplyRemote(command);
            }
        }
        finally
        {
            _batchApplying = false;
        }
        RefreshTexture();
    }

    private void RefreshTexture()
    {
        if (_batchApplying)
        {
            return;
        }
        _texture.Update(_image);
        QueueRedraw();
    }

    private uint NextOperationId()
    {
        uint operationId = _nextOperationId++;
        if (_nextOperationId == 0u)
        {
            _nextOperationId = 1u;
        }
        return operationId;
    }

    private void FinishStroke()
    {
        if (_drawing && _activeStrokeOperationId != 0u)
        {
            LocalCommandGenerated?.Invoke(DrawingCommand.StrokeEnd(_activeStrokeOperationId));
        }
        _drawing = false;
        _erasing = false;
        _activeStrokeOperationId = 0u;
        if (HasPointerPreview())
        {
            QueueRedraw();
        }
    }

    private static ushort ToUShort(float value)
    {
        return (ushort)Mathf.Clamp(Mathf.RoundToInt(value), 0, ushort.MaxValue);
    }
}
