using System.Text;

namespace Jornal.Workflow.Server.Windows;

internal sealed class OfficeLogWriter(string path) : TextWriter
{
    private const int Limit = 1024 * 1024;
    internal const int Keep = 3;
    private StreamWriter? _output;
    private long _length;
    public override Encoding Encoding { get; } = new UTF8Encoding(false);

    public override void Write(char value) => Write(value.ToString());
    public override void WriteLine(string? value) => Write(value + NewLine);
    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        try
        {
            if (value.Length > Limit / 4) value = value[..(Limit / 4)] + NewLine;
            var bytes = Encoding.GetByteCount(value);
            Open();
            if (_length + bytes > Limit)
            {
                CloseCurrent();
                for (var index = Keep - 1; index >= 0; index--)
                {
                    var source = PartPath(path, index);
                    if (File.Exists(source)) File.Move(source, PartPath(path, index + 1), overwrite: true);
                }
                Open();
            }
            _output!.Write(value);
            _length += bytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A disk or log viewer letting go of the file allows the next entry to reopen it.
            CloseCurrent();
        }
    }

    internal static string PartPath(string path, int index) => index == 0 ? path : Path.ChangeExtension(path, $"{index}.log");

    private void Open()
    {
        if (_output is not null) return;
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        _length = stream.Length;
        _output = new StreamWriter(stream, Encoding) { AutoFlush = true };
    }

    private void CloseCurrent()
    {
        var current = _output;
        _output = null;
        try { current?.Dispose(); }
        catch (IOException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CloseCurrent();
        base.Dispose(disposing);
    }
}
