// Where the traffic manager says what it did to a vehicle.
//
// These are the lines that explain a vehicle stopping, being removed, leaving its route, or
// deciding it no longer has to obey a signal — the events somebody will need accounted for when
// traffic misbehaves. They are written unconditionally rather than behind a diagnostic flag, for
// the reason every defect in this area has demonstrated: a subsystem that acts on a vehicle without
// saying so cannot be told apart from one that is not acting at all.
//
// They go to standard error, which is where the rest of the traffic manager's output goes. A host
// that is also writing its own console to a file can name that file here, and these lines will be
// written to it as well — the host cannot capture them by wrapping its own streams, because this
// process writes to its own handle on the same descriptor and never passes through them.
#nullable enable

namespace CarlaNet.TrafficManager;

/// <summary>
/// The traffic manager's per-event reporting sink: standard error, plus an optional file.
/// </summary>
internal static class TrafficReport
{
    private static readonly TeeWriter _writer = new();

    /// <summary>Write traffic-manager events here. Never null.</summary>
    public static System.IO.TextWriter Writer => _writer;

    /// <summary>
    /// Also append events to <paramref name="path"/>, or stop doing so when it is null. Opening a
    /// new file closes the previous one. A path that cannot be opened is reported and otherwise
    /// ignored, because losing the console output as well would be a worse outcome than losing the
    /// file.
    /// </summary>
    public static void SetLogFile(string? path) => _writer.SetFile(path);

    private sealed class TeeWriter : System.IO.TextWriter
    {
        private readonly object _gate = new();
        private System.IO.StreamWriter? _file;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public void SetFile(string? path)
        {
            lock (_gate)
            {
                _file?.Dispose();
                _file = null;
                if (string.IsNullOrEmpty(path)) return;
                try
                {
                    string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    // Appending: the host normally owns this file and has already written its own
                    // startup banner to it by the time the traffic manager exists.
                    _file = new System.IO.StreamWriter(path, append: true) { AutoFlush = true };
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[traffic] could not open '{path}' for event logging: {ex.Message}");
                }
            }
        }

        public override void WriteLine(string? value)
        {
            Console.Error.WriteLine(value);
            lock (_gate)
            {
                // A run that ends badly must still leave behind what led up to it, hence AutoFlush.
                try { _file?.WriteLine(value); }
                catch (Exception) { /* the console copy is the one that matters */ }
            }
        }

        public override void Write(char value)
        {
            Console.Error.Write(value);
            lock (_gate)
            {
                try { _file?.Write(value); }
                catch (Exception) { }
            }
        }
    }
}
