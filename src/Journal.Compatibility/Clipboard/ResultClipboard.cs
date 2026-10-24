using System.Globalization;
using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Jornal.Core.Results;

namespace Jornal.Desktop.Clipboard;

public interface IResultClipboard
{
    Task WriteAsync(ResultClipboardContent content);
}

public sealed class ResultClipboard(Func<IClipboard?> getClipboard) : IResultClipboard
{
    public async Task WriteAsync(ResultClipboardContent content)
    {
        var clipboard = getClipboard() ?? throw new NotSupportedException("Системный буфер обмена недоступен.");
        var data = ResultClipboardPayload.Create(content);
        await clipboard.SetDataAsync(data);
        // После успешной записи объект принадлежит Avalonia. Не очищаем прежний буфер отдельным вызовом.
        await clipboard.FlushAsync();
    }
}

public static class ResultClipboardPayload
{
    public static DataFormat<byte[]> HtmlFormat { get; } = DataFormat.CreateBytesPlatformFormat(
        OperatingSystem.IsWindows() ? "HTML Format" : OperatingSystem.IsMacOS() ? "public.html" : "text/html");
    public static DataFormat<byte[]> RtfFormat { get; } = DataFormat.CreateBytesPlatformFormat(
        OperatingSystem.IsWindows() ? "Rich Text Format" : OperatingSystem.IsMacOS() ? "public.rtf" : "text/rtf");

    public static DataTransfer Create(ResultClipboardContent content)
    {
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, content.Text);
        var html = OperatingSystem.IsWindows() ? WindowsHtml(content.Html) : Encoding.UTF8.GetBytes(HtmlDocument(content.Html));
        // Win32 сериализует string как UTF-16; RTF-потребителям нужны ASCII-байты с Unicode-экранированием.
        var rtf = Encoding.ASCII.GetBytes(content.Rtf);
        if (OperatingSystem.IsWindows())
        {
            // Нативные CF_HTML и RTF завершаются NUL; смещения CF_HTML его не включают.
            Array.Resize(ref html, html.Length + 1);
            Array.Resize(ref rtf, rtf.Length + 1);
        }
        item.Set(HtmlFormat, html);
        item.Set(RtfFormat, rtf);
        var data = new DataTransfer();
        data.Add(item);
        return data;
    }

    private const string Prefix = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><!--StartFragment-->";
    private const string Suffix = "<!--EndFragment--></body></html>";
    public static string HtmlDocument(string fragment) => Prefix + fragment + Suffix;

    public static byte[] WindowsHtml(string fragment)
    {
        // CF_HTML задаёт смещения в байтах UTF-8, а не в символах .NET.
        const string headerTemplate = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var startHtml = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, headerTemplate, 0, 0, 0, 0));
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(Prefix);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(Suffix);
        var header = string.Format(CultureInfo.InvariantCulture, headerTemplate, startHtml, endHtml, startFragment, endFragment);
        return Encoding.UTF8.GetBytes(header + HtmlDocument(fragment));
    }
}
