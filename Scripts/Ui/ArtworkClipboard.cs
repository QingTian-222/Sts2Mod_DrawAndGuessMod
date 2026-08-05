using System;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;

namespace DrawAndGuessMod.Scripts.Ui;

internal static class ArtworkClipboard
{
    private const uint GmemMoveable = 0x0002;
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;
    private const int BitmapInfoHeaderSize = 40;
    private const int BitmapV5HeaderSize = 124;
    private const int BiBitfields = 3;

    public static bool TryCopyPng(byte[] pngBytes, out string error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "Image clipboard is only available on Windows.";
            return false;
        }

        Image image = new();
        if (image.LoadPngFromBuffer(pngBytes) != Error.Ok)
        {
            error = "The artwork is not a valid PNG image.";
            return false;
        }

        image.Convert(Image.Format.Rgba8);
        byte[] rgba = image.GetData().ToArray();
        int width = image.GetWidth();
        int height = image.GetHeight();
        int pixelBytes = checked(width * height * 4);
        if (rgba.Length < pixelBytes)
        {
            error = "The decoded artwork has incomplete pixel data.";
            return false;
        }

        byte[] dib = new byte[BitmapInfoHeaderSize + pixelBytes];
        WriteInt32(dib, 0, BitmapInfoHeaderSize);
        WriteInt32(dib, 4, width);
        WriteInt32(dib, 8, -height); // A top-down DIB matches Godot's row order.
        WriteInt16(dib, 12, 1);
        WriteInt16(dib, 14, 32);
        WriteInt32(dib, 20, pixelBytes);
        for (int source = 0, target = BitmapInfoHeaderSize;
             source < pixelBytes;
             source += 4, target += 4)
        {
            dib[target] = rgba[source + 2];
            dib[target + 1] = rgba[source + 1];
            dib[target + 2] = rgba[source];
            dib[target + 3] = rgba[source + 3];
        }

        byte[] dibV5 = BuildDibV5(width, height, rgba, pixelBytes);
        IntPtr dibMemory = AllocateClipboardMemory(dib);
        IntPtr dibV5Memory = AllocateClipboardMemory(dibV5);
        IntPtr pngMemory = AllocateClipboardMemory(pngBytes);
        if (dibMemory == IntPtr.Zero || dibV5Memory == IntPtr.Zero || pngMemory == IntPtr.Zero)
        {
            error = "Could not allocate memory for the clipboard image.";
            FreeClipboardMemory(ref dibMemory);
            FreeClipboardMemory(ref dibV5Memory);
            FreeClipboardMemory(ref pngMemory);
            return false;
        }

        bool clipboardOpened = false;
        try
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    clipboardOpened = true;
                    break;
                }

                Thread.Sleep(25);
            }
            if (!clipboardOpened)
            {
                error = $"Could not open the clipboard ({Marshal.GetLastWin32Error()}).";
                return false;
            }

            if (!EmptyClipboard())
            {
                error = $"Could not clear the clipboard ({Marshal.GetLastWin32Error()}).";
                return false;
            }

            if (SetClipboardData(CfDibV5, dibV5Memory) == IntPtr.Zero)
            {
                error = $"Could not write the image to the clipboard ({Marshal.GetLastWin32Error()}).";
                return false;
            }
            dibV5Memory = IntPtr.Zero; // The clipboard now owns the global memory handle.

            if (SetClipboardData(CfDib, dibMemory) != IntPtr.Zero)
            {
                dibMemory = IntPtr.Zero;
            }

            uint pngFormat = RegisterClipboardFormat("PNG");
            if (pngFormat != 0 && SetClipboardData(pngFormat, pngMemory) != IntPtr.Zero)
            {
                pngMemory = IntPtr.Zero;
            }

            error = string.Empty;
            return true;
        }
        finally
        {
            if (clipboardOpened)
            {
                CloseClipboard();
            }
            FreeClipboardMemory(ref dibMemory);
            FreeClipboardMemory(ref dibV5Memory);
            FreeClipboardMemory(ref pngMemory);
        }
    }

    private static byte[] BuildDibV5(int width, int height, byte[] rgba, int pixelBytes)
    {
        byte[] dib = new byte[BitmapV5HeaderSize + pixelBytes];
        WriteInt32(dib, 0, BitmapV5HeaderSize);
        WriteInt32(dib, 4, width);
        WriteInt32(dib, 8, -height);
        WriteInt16(dib, 12, 1);
        WriteInt16(dib, 14, 32);
        WriteInt32(dib, 16, BiBitfields);
        WriteInt32(dib, 20, pixelBytes);
        WriteInt32(dib, 40, unchecked((int)0x00FF0000));
        WriteInt32(dib, 44, 0x0000FF00);
        WriteInt32(dib, 48, 0x000000FF);
        WriteInt32(dib, 52, unchecked((int)0xFF000000));
        for (int source = 0, target = BitmapV5HeaderSize;
             source < pixelBytes;
             source += 4, target += 4)
        {
            dib[target] = rgba[source + 2];
            dib[target + 1] = rgba[source + 1];
            dib[target + 2] = rgba[source];
            dib[target + 3] = rgba[source + 3];
        }

        return dib;
    }

    private static IntPtr AllocateClipboardMemory(byte[] bytes)
    {
        IntPtr memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
        if (memory == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr target = GlobalLock(memory);
        if (target == IntPtr.Zero)
        {
            GlobalFree(memory);
            return IntPtr.Zero;
        }

        Marshal.Copy(bytes, 0, target, bytes.Length);
        GlobalUnlock(memory);
        return memory;
    }

    private static void FreeClipboardMemory(ref IntPtr memory)
    {
        if (memory != IntPtr.Zero)
        {
            GlobalFree(memory);
            memory = IntPtr.Zero;
        }
    }

    private static void WriteInt16(byte[] target, int offset, short value)
    {
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteInt32(byte[] target, int offset, int value)
    {
        for (int index = 0; index < 4; index++)
        {
            target[offset + index] = (byte)(value >> (index * 8));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
