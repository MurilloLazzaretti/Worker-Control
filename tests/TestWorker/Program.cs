// A worker for the tests. It speaks to the Worker Control the way a real one does (keep-alive
// on the queue <pid>, safe stop on <pid>SS) and misbehaves on request:
//
//   TestWorker <host> <port> [mode]
//
//   normal        answers and leaves when asked (default)
//   hang          answers one keep-alive, then never again
//   deaf          never answers a keep-alive
//   stubborn      answers, but ignores the safe stop
//   crash:<ms>    ends by itself, with exit code 7, after that long
//   slow:<ms>     takes that long to start answering
//
// In every mode it also traces the way of 1.x when asked to.
using ZapMQ;

var host = args[0];
var port = int.Parse(args[1]);
var mode = args.Length > 2 ? args[2] : "normal";
var pid = Environment.ProcessId.ToString();
var leave = new ManualResetEventSlim(false);
var answered = 0;

if (mode.StartsWith("slow:"))
    Thread.Sleep(int.Parse(mode[5..]));

if (mode.StartsWith("crash:"))
{
    var after = int.Parse(mode[6..]);
    new Thread(() =>
    {
        Thread.Sleep(after);
        Environment.Exit(7);
    }) { IsBackground = true }.Start();
}

var wrapper = new ZapMQWrapper(host, port);

wrapper.Bind(pid, (ZapJSONMessage _, out bool processing) =>
{
    processing = false;
    if (mode == "deaf" || (mode == "hang" && Interlocked.Increment(ref answered) > 1))
        Thread.Sleep(Timeout.Infinite);
    return new { ProcessId = pid };
});

// Trace the way of 1.x: told a port of this machine, the worker connects to it and writes.
System.Net.Sockets.TcpClient? traceSocket = null;
wrapper.Bind(pid + "TR", (ZapJSONMessage message, out bool processing) =>
{
    processing = false;
    var body = (Newtonsoft.Json.Linq.JObject)message.Body;
    if ((string?)body["message"] != "start trace")
    {
        traceSocket?.Close();
        traceSocket = null;
        return new { message = "off" };
    }
    var socket = new System.Net.Sockets.TcpClient();
    socket.Connect("127.0.0.1", (int)body["port"]!);
    traceSocket = socket;
    new Thread(() =>
    {
        try
        {
            // Bytes that are not UTF-8, as a Delphi worker would send an accented text.
            socket.GetStream().Write([0x61, 0xE7, 0xE3, 0x6F]);
            for (var line = 1; ; line++)
            {
                Thread.Sleep(60);
                socket.GetStream().Write(System.Text.Encoding.UTF8.GetBytes($"trace {line} of {pid}: ação"));
            }
        }
        catch (Exception)
        {
            // Turned off.
        }
    }) { IsBackground = true }.Start();
    return new { message = "on" };
});

wrapper.Bind(pid + "SS", (ZapJSONMessage _, out bool processing) =>
{
    processing = false;
    if (mode != "stubborn")
        leave.Set();
    return null!;
});

leave.Wait();
wrapper.StopThreads();
return 0;
