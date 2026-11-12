using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

/// <summary>A tiny local control channel (one line in, one line out): used by the second launch, the --status / --quit commands and the tests.</summary>
public sealed class ControlPipe : IDisposable
{
    readonly string name;
    readonly Func<string, string> handler;
    readonly Thread thread;
    volatile bool stop;

    public ControlPipe(string pipeName, Func<string, string> onCommand)
    {
        name = pipeName;
        handler = onCommand;
        thread = new Thread(Loop);
        thread.IsBackground = true;
        thread.Name = "ControlPipe";
        thread.Start();
    }

    void Loop()
    {
        while (!stop)
        {
            try
            {
                using (NamedPipeServerStream s = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte))
                {
                    s.WaitForConnection();
                    if (stop) return;
                    StreamReader r = new StreamReader(s);
                    StreamWriter w = new StreamWriter(s);
                    w.AutoFlush = true;
                    string cmd = r.ReadLine();
                    string reply;
                    try { reply = handler(cmd == null ? "" : cmd.Trim()); }
                    catch (Exception ex) { reply = "error " + ex.Message; }
                    w.WriteLine(reply);
                    s.WaitForPipeDrain();
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    public static string Send(string pipeName, string command, int timeoutMs)
    {
        using (NamedPipeClientStream c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
        {
            c.Connect(timeoutMs);
            StreamWriter w = new StreamWriter(c);
            w.AutoFlush = true;
            StreamReader r = new StreamReader(c);
            w.WriteLine(command);
            return r.ReadLine();
        }
    }

    public void Dispose()
    {
        if (stop) return;
        stop = true;
        try { Send(name, "ping", 500); } catch { }       // wakes the blocked WaitForConnection
        thread.Join(1000);
    }
}
