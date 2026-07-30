using System.Collections.Generic;
using DrawAndGuessMod.Scripts.Networking;
using Godot;

namespace DrawAndGuessMod.Scripts.Ui;

public partial class DrawingCanvas : Control
{
    public const int StandardCanvasWidth = 500;
    public const int StandardCanvasHeight = 380;
    public const int AncientCanvasWidth = 300;
    public const int AncientCanvasHeight = 422;
    private const int AncientCanvasDisplayWidth = 360;
    private const int AncientCanvasDisplayHeight = 506;
    public const int MinBrushSize = 4;
    public const int MaxBrushSize = 48;
    public const int DefaultBrushSize = 14;
    public const int MinStampSize = 40;
    public const int MaxStampSize = 192;
    public const int DefaultStampSize = 96;
    internal static readonly Color PaperColor = new("F4EEDC");
    private static readonly Color StampPreviewModulate = new(1f, 1f, 1f, 0.45f);

    private enum DrawingTool
    {
        Brush,
        Eraser,
        Fill,
        Stamp
    }

    private Image _image = null!;
    private ImageTexture _texture = null!;
    private Color _leftColor = new("1B1A18");
    private Color _rightColor = PaperColor;
    private Color _activeStrokeColor = new("1B1A18");
    private DrawingTool _tool = DrawingTool.Brush;
    private Image? _stampImage;
    private ImageTexture? _stampPreviewTexture;
    private byte _stampIndex;
    private byte _brushSize = DefaultBrushSize;
    private byte _stampSize = DefaultStampSize;
    private uint _nextOperationId = 1u;
    private uint _activeStrokeOperationId;
    private MouseButton? _activeStrokeButton;
    private readonly Dictionary<byte, Image> _stampImages = new();
    private readonly Dictionary<(byte StampIndex, byte StampSize), Image> _scaledStampImages = new();
    private bool _drawing;
    private bool _batchApplying;
    private bool _pointerInside;
    private Vector2 _lastPixel;
    private Vector2 _pointerPosition;
    private int _canvasWidth = StandardCanvasWidth;
    private int _canvasHeight = StandardCanvasHeight;

    internal DrawingCanvasMode CanvasMode { get; private set; } = DrawingCanvasMode.Standard;

    internal event Action<DrawingCommand>? LocalCommandGenerated;
    internal event Action<Color>? LeftColorSampled;

    public override void _Ready()
    {
        CustomMinimumSize = GetCanvasDisplaySize(CanvasMode);
        ClipContents = true;
        MouseDefaultCursorShape = CursorShape.Cross;
        MouseFilter = MouseFilterEnum.Stop;
        _image = Image.CreateEmpty(_canvasWidth, _canvasHeight, false, Image.Format.Rgba8);
        _image.Fill(PaperColor);
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
                _stampImage.GetWidth() * Size.X / _canvasWidth,
                _stampImage.GetHeight() * Size.Y / _canvasHeight);
            Rect2 previewRect = new(_pointerPosition - previewSize / 2f, previewSize);
            DrawTextureRect(_stampPreviewTexture, previewRect, false, StampPreviewModulate);
        }
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("6D624E"), false, 3f);
    }

    public override void _Process(double delta)
    {
        if (_drawing &&
            _activeStrokeButton is MouseButton activeStrokeButton &&
            !Input.IsMouseButtonPressed(activeStrokeButton))
        {
            FinishStroke();
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Middle } middleButton)
        {
            if (middleButton.Pressed)
            {
                Vector2 pixel = ToPixel(middleButton.Position);
                Color sampled = NormalizeColor(_image.GetPixel(
                    Mathf.RoundToInt(pixel.X),
                    Mathf.RoundToInt(pixel.Y)));
                _leftColor = sampled;
                LeftColorSampled?.Invoke(sampled);
            }
            AcceptEvent();
            return;
        }

        if (@event is InputEventMouseButton button && (button.ButtonIndex == MouseButton.Left || button.ButtonIndex == MouseButton.Right))
        {
            if (!button.Pressed)
            {
                if (_activeStrokeButton == button.ButtonIndex)
                {
                    FinishStroke();
                }
                AcceptEvent();
                return;
            }

            if (_drawing)
            {
                FinishStroke();
            }
            _lastPixel = ToPixel(button.Position);
            Color inputColor = button.ButtonIndex == MouseButton.Right ? _rightColor : _leftColor;
            if (_tool is DrawingTool.Brush or DrawingTool.Eraser)
            {
                _activeStrokeOperationId = NextOperationId();
                _activeStrokeButton = button.ButtonIndex;
                _activeStrokeColor = inputColor;
                _drawing = true;
                PaintLineLocal(_lastPixel, _lastPixel);
            }
            else if (_tool == DrawingTool.Fill)
            {
                FloodFillLocal(
                    Mathf.RoundToInt(_lastPixel.X),
                    Mathf.RoundToInt(_lastPixel.Y),
                    inputColor);
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

    internal void SetCanvasMode(DrawingCanvasMode mode)
    {
        CanvasMode = mode;
        (_canvasWidth, _canvasHeight) = mode == DrawingCanvasMode.Ancient
            ? (AncientCanvasWidth, AncientCanvasHeight)
            : (StandardCanvasWidth, StandardCanvasHeight);
        CustomMinimumSize = GetCanvasDisplaySize(mode);
        CancelActiveOperation();
        if (_image == null)
        {
            return;
        }

        _image = Image.CreateEmpty(_canvasWidth, _canvasHeight, false, Image.Format.Rgba8);
        _image.Fill(PaperColor);
        _texture.SetImage(_image);
        QueueRedraw();
    }

    private static Vector2 GetCanvasDisplaySize(DrawingCanvasMode mode)
    {
        return mode == DrawingCanvasMode.Ancient
            ? new Vector2(AncientCanvasDisplayWidth, AncientCanvasDisplayHeight)
            : new Vector2(StandardCanvasWidth, StandardCanvasHeight);
    }

    public void SetMouseColors(Color leftColor, Color rightColor)
    {
        _leftColor = NormalizeColor(leftColor);
        _rightColor = NormalizeColor(rightColor);
        if (_tool == DrawingTool.Fill && _pointerInside)
        {
            QueueRedraw();
        }
    }

    public void SetBrushTool()
    {
        FinishStroke();
        _tool = DrawingTool.Brush;
        UpdateMouseCursor();
        QueueRedraw();
    }

    public void SetEraserTool()
    {
        FinishStroke();
        _tool = DrawingTool.Eraser;
        UpdateMouseCursor();
        QueueRedraw();
    }

    public void SetFillTool()
    {
        FinishStroke();
        _tool = DrawingTool.Fill;
        UpdateMouseCursor();
        QueueRedraw();
    }

    public void SetBrushSize(int size)
    {
        _brushSize = (byte)Mathf.Clamp(size, MinBrushSize, MaxBrushSize);
    }

    public void SetStampSize(int size)
    {
        _stampSize = (byte)Mathf.Clamp(size, MinStampSize, MaxStampSize);
        if (_tool == DrawingTool.Stamp)
        {
            UpdateSelectedStampImage();
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
            foreach ((byte StampIndex, byte StampSize) key in _scaledStampImages.Keys.Where(key => key.StampIndex == stampIndex).ToList())
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

        FinishStroke();
        _stampIndex = stampIndex;
        _tool = DrawingTool.Stamp;
        UpdateMouseCursor();
        UpdateSelectedStampImage();
        Vector2 localMouse = GetLocalMousePosition();
        _pointerInside = new Rect2(Vector2.Zero, Size).HasPoint(localMouse);
        _pointerPosition = localMouse;
        QueueRedraw();
        return true;
    }

    public bool IsStampTool()
    {
        return _tool == DrawingTool.Stamp;
    }

    private void UpdateMouseCursor()
    {
        MouseDefaultCursorShape = _tool == DrawingTool.Fill
            ? CursorShape.PointingHand
            : CursorShape.Cross;
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
                Image? stamp = GetScaledStampImage(command.StampIndex, command.StampSize);
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

    internal bool ImportPng(byte[] pngBytes, bool cancelActiveOperation = true)
    {
        Image imported = new();
        if (imported.LoadPngFromBuffer(pngBytes) != Error.Ok)
        {
            return false;
        }

        imported.Convert(Image.Format.Rgba8);
        imported.Resize(_canvasWidth, _canvasHeight, Image.Interpolation.Lanczos);
        if (cancelActiveOperation)
        {
            CancelActiveOperation();
        }
        _image = imported;
        _texture.Update(_image);
        QueueRedraw();
        return true;
    }

    internal void CancelActiveOperation()
    {
        _drawing = false;
        _activeStrokeOperationId = 0u;
        _activeStrokeButton = null;
        if (HasPointerPreview())
        {
            QueueRedraw();
        }
    }

    public Image Snapshot()
    {
        return Image.CreateFromData(_image.GetWidth(), _image.GetHeight(), false, _image.GetFormat(), _image.GetData());
    }

    public byte[] ExportPng()
    {
        return _image.SavePngToBuffer();
    }

    internal bool IsBlank()
    {
        for (int y = 0; y < _canvasHeight; y++)
        {
            for (int x = 0; x < _canvasWidth; x++)
            {
                if (!ColorsAreClose(_image.GetPixel(x, y), PaperColor))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private Vector2 ToPixel(Vector2 position)
    {
        float x = Mathf.Clamp(position.X / Mathf.Max(Size.X, 1f) * _canvasWidth, 0f, _canvasWidth - 1f);
        float y = Mathf.Clamp(position.Y / Mathf.Max(Size.Y, 1f) * _canvasHeight, 0f, _canvasHeight - 1f);
        return new Vector2(x, y);
    }

    private void PaintLineLocal(Vector2 from, Vector2 to)
    {
        bool erasing = _tool == DrawingTool.Eraser;
        ApplyLine(from, to, _activeStrokeColor, erasing, _brushSize);
        LocalCommandGenerated?.Invoke(DrawingCommand.Line(
            ToUShort(from.X),
            ToUShort(from.Y),
            ToUShort(to.X),
            ToUShort(to.Y),
            _activeStrokeColor,
            erasing,
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
        Color color = erasing ? PaperColor : brushColor;
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
                if (px >= 0 && py >= 0 && px < _canvasWidth && py < _canvasHeight)
                {
                    _image.SetPixel(px, py, color);
                }
            }
        }
    }

    private void FloodFillLocal(int startX, int startY, Color fillColor)
    {
        if (ApplyFloodFill(startX, startY, fillColor))
        {
            LocalCommandGenerated?.Invoke(DrawingCommand.Fill(
                (ushort)startX,
                (ushort)startY,
                fillColor,
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

        bool[] visited = new bool[_canvasWidth * _canvasHeight];
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

    private void EnqueueFillPoint(Queue<Vector2I> pending, bool[] visited, int x, int y)
    {
        if (x < 0 || y < 0 || x >= _canvasWidth || y >= _canvasHeight)
        {
            return;
        }

        int index = y * _canvasWidth + x;
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
                _stampSize,
                NextOperationId()));
        }
    }

    private void UpdateSelectedStampImage()
    {
        _stampImage = GetScaledStampImage(_stampIndex, _stampSize);
        _stampPreviewTexture = _stampImage == null ? null : ImageTexture.CreateFromImage(_stampImage);
        QueueRedraw();
    }

    private Image? GetScaledStampImage(byte stampIndex, byte stampSize)
    {
        byte normalizedStampSize = (byte)Mathf.Clamp(stampSize, MinStampSize, MaxStampSize);
        (byte StampIndex, byte StampSize) key = (stampIndex, normalizedStampSize);
        if (_scaledStampImages.TryGetValue(key, out Image? cached))
        {
            return cached;
        }
        if (!_stampImages.TryGetValue(stampIndex, out Image? source))
        {
            return null;
        }

        Image scaled = Image.CreateFromData(source.GetWidth(), source.GetHeight(), false, source.GetFormat(), source.GetData());
        scaled.Resize(normalizedStampSize, normalizedStampSize, Image.Interpolation.Lanczos);
        _scaledStampImages[key] = scaled;
        return scaled;
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
        return _tool == DrawingTool.Stamp;
    }

    private bool ApplyStamp(int centerX, int centerY, Image stampImage)
    {

        int destinationX = centerX - stampImage.GetWidth() / 2;
        int destinationY = centerY - stampImage.GetHeight() / 2;
        int sourceX = Math.Max(0, -destinationX);
        int sourceY = Math.Max(0, -destinationY);
        destinationX = Math.Max(0, destinationX);
        destinationY = Math.Max(0, destinationY);
        int width = Math.Min(stampImage.GetWidth() - sourceX, _canvasWidth - destinationX);
        int height = Math.Min(stampImage.GetHeight() - sourceY, _canvasHeight - destinationY);
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
        _image.Fill(PaperColor);
        RefreshTexture();
    }

    internal void RebuildFromCommands(IEnumerable<DrawingCommand> commands)
    {
        _batchApplying = true;
        try
        {
            _image.Fill(PaperColor);
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

    internal void ApplyCommands(IEnumerable<DrawingCommand> commands)
    {
        _batchApplying = true;
        try
        {
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

    internal void RebuildFromPixelPatches(IEnumerable<DrawingPixelPatch> patches)
    {
        _batchApplying = true;
        try
        {
            _image.Fill(PaperColor);
            foreach (DrawingPixelPatch patch in patches)
            {
                patch.Apply(_image);
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
        _activeStrokeOperationId = 0u;
        _activeStrokeButton = null;
        if (HasPointerPreview())
        {
            QueueRedraw();
        }
    }

    private static ushort ToUShort(float value)
    {
        return (ushort)Mathf.Clamp(Mathf.RoundToInt(value), 0, ushort.MaxValue);
    }

    private static Color NormalizeColor(Color color)
    {
        return DrawingCommand.UnpackRgb(DrawingCommand.PackRgb(color));
    }
}
