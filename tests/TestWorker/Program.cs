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
