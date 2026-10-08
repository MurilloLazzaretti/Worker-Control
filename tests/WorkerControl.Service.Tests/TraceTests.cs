using Newtonsoft.Json.Linq;
using ZapMQ;

namespace WorkerControl.Service.Tests;

/// <summary>
/// Trace for workers that only know the way of 1.x: the service opens the port they connect
/// to and passes what they write on to a queue.
/// </summary>
[Collection("processes")]
public class TraceTests
{
    /// <summary>
    /// Collects the text of every line published in a queue.
    /// </summary>
    private static (ZapMQWrapper Consumer, List<string> Lines) Listen(Rig rig, string queue)
    {
        var lines = new List<string>();
        var consumer = new ZapMQWrapper("localhost", rig.Port);
        consumer.Bind(queue, (ZapJSONMessage message, out bool processing) =>
        {
            processing = false;
            var batch = (JObject)message.Body;
            lock (lines)
                lines.AddRange(batch["Lines"]!.Select(line => (string)line["Text"]!));
            return null!;
        });
        return (consumer, lines);
    }

    private static int Count(List<string> lines)
    {
        lock (lines)
            return lines.Count;
    }

    [Fact]
    public async Task What_a_worker_writes_to_the_port_goes_to_the_queue()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 1));
        await rig.StartServiceAsync();
        var pid = (await rig.WaitForWorkersAsync("Orders", 1)).Single();
        var queue = "trace-test-" + Guid.NewGuid().ToString("N");
        var (consumer, lines) = Listen(rig, queue);

        var answer = await rig.CommandAsync("StartTrace", request =>
        {
            request["ProcessId"] = pid;
            request["Queue"] = queue;
        });

        Assert.True((bool)answer!["Ok"]!, answer.ToString());
        Assert.True(await Rig.Eventually(() => Count(lines) >= 5));
        lock (lines)
        {
            Assert.Contains(lines, line => line.Contains($"trace 2 of {pid}: ação"));
            // Written in the ANSI code page, as a Delphi worker does.
            Assert.Contains(lines, line => line.Contains("ação") && !line.Contains("trace"));
        }

        Assert.True((bool)(await rig.CommandAsync("StopTrace", request => request["ProcessId"] = pid))!["Ok"]!);
        await Task.Delay(1000);
        var after = Count(lines);
        await Task.Delay(1000);
        Assert.Equal(after, Count(lines));
        consumer.StopThreads();
    }

    [Fact]
    public async Task Without_being_asked_again_the_trace_turns_itself_off()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 1));
        await rig.StartServiceAsync();
        var pid = (await rig.WaitForWorkersAsync("Orders", 1)).Single();
        var queue = "trace-test-" + Guid.NewGuid().ToString("N");
        var (consumer, lines) = Listen(rig, queue);

        Assert.True((bool)(await rig.CommandAsync("StartTrace", request =>
        {
            request["ProcessId"] = pid;
            request["Queue"] = queue;
            request["LeaseSeconds"] = 2;
        }))!["Ok"]!);
        Assert.True(await Rig.Eventually(() => Count(lines) >= 3));

        // Asked again in time, it goes on past the first two seconds.
        await Task.Delay(1200);
        Assert.True((bool)(await rig.CommandAsync("StartTrace", request =>
        {
            request["ProcessId"] = pid;
            request["Queue"] = queue;
            request["LeaseSeconds"] = 2;
        }))!["Ok"]!);
        await Task.Delay(1500);
        var during = Count(lines);
        await Task.Delay(400);
        Assert.True(Count(lines) > during);

        Assert.True(await Rig.Eventually(() => rig.LogText().Contains($"Trace of worker {pid} turned off")));
        await Task.Delay(500);
        var after = Count(lines);
        await Task.Delay(800);
        Assert.Equal(after, Count(lines));
        consumer.StopThreads();
    }

    [Fact]
    public async Task Only_a_worker_of_the_service_can_be_traced()
    {
        await using var rig = new Rig();
        await rig.StartBrokerAsync();
        rig.WriteConfig(rig.Group("Orders", 1));
        await rig.StartServiceAsync();
        await rig.WaitForWorkersAsync("Orders", 1);

        var unknown = await rig.CommandAsync("StartTrace", request =>
        {
            request["ProcessId"] = 1;
            request["Queue"] = "anything";
        });
        var incomplete = await rig.CommandAsync("StartTrace", request => request["ProcessId"] = 1);

        Assert.Equal("not-found", (string)unknown!["Error"]!["Code"]!);
        Assert.Equal("invalid-request", (string)incomplete!["Error"]!["Code"]!);
    }
}
