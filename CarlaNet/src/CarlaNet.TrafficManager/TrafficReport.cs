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
                    // The host owns this file and keeps its own handle open on it for the whole run,
                    // so the share mode has to permit another writer: StreamWriter's own file open
                    // asks for FileShare.Read, which denies write access that the host already holds
                    // and fails with a sharing violation every time. That failure is only reportable
                    // to the console — the file it would have gone to is the one that could not be
                    // opened — so it reads as the traffic manager having nothing to say.
                    //
                    // Append mode on both sides is what keeps the two writers from overwriting each
                    // other: every write goes to the end of the file as it stands, rather than to a
                    // position each handle tracks independently.
                    var stream = new System.IO.FileStream(
                        path,
                        System.IO.FileMode.Append,
                        System.IO.FileAccess.Write,
                        System.IO.FileShare.ReadWrite);
                    _file = new System.IO.StreamWriter(stream) { AutoFlush = true };
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
