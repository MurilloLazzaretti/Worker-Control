using Newtonsoft.Json.Linq;
using WorkerControl.Core;

namespace WorkerControl.Service.Tests;

/// <summary>
/// The configuration in two files: the supervisor's own, and the one of the environment.
/// </summary>
public sealed class EnvironmentFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("environment-file-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void What_is_of_the_environment_leaves_the_file_of_the_supervisor_and_both_are_read_as_one()
    {
        var file = new ConfigFile(_directory);
        File.WriteAllText(file.Path, """
            { "ZapMQHost": "localhost", "ZapMQPort": 5679, "WorkerGroups": [],
              "Transport": { "StopSeconds": 45 }, "Frontends": [ { "Name": "App", "Root": "D:\\www\\app", "Mode": "monolith" } ] }
            """);

        Assert.True(file.Separate());

        var own = JObject.Parse(file.Read());
        Assert.Null(own["Transport"]);
        Assert.Null(own["Frontends"]);
        var environment = JObject.Parse(file.ReadEnvironment());
        Assert.Equal(45, (int)environment["Transport"]!["StopSeconds"]!);
        var config = ConfigReader.Parse(file.ReadMerged());
        Assert.Equal((45, "monolith"), (config.Transport.StopSeconds, config.Frontends.Single().Mode));
        Assert.False(file.Separate());

        // What is written in the file of the supervisor goes to the other all the same.
        file.Write("""{ "ZapMQHost": "localhost", "ZapMQPort": 5679, "WorkerGroups": [], "Proxy": { "Service": "nginx" } }""");
        Assert.Null(JObject.Parse(file.Read())["Proxy"]);
        Assert.Equal("nginx", (string?)JObject.Parse(file.ReadEnvironment())["Proxy"]!["Service"]);

        // The file of the environment takes only what is of the environment, and only what would be accepted.
        Assert.Throws<ConfigException>(() => file.WriteEnvironment("""{ "WorkerGroups": [] }"""));
        Assert.Throws<ConfigException>(() => file.WriteEnvironment("""{ "Transport": { "StopSeconds": 1 } }"""));
        Assert.Throws<ConfigException>(() => file.WriteEnvironment("{ not json"));
        file.WriteEnvironment("""{ "Transport": { "StopSeconds": 30 } }""");
        Assert.Equal(30, ConfigReader.Parse(file.ReadMerged()).Transport.StopSeconds);

        // A section changed by the service goes where it belongs.
        file.ChangeSection("Transport", transport => transport["KeepVersions"] = 5);
        Assert.Equal((30, 5), (ConfigReader.Parse(file.ReadMerged()).Transport.StopSeconds, ConfigReader.Parse(file.ReadMerged()).Transport.KeepVersions));
        Assert.Null(JObject.Parse(file.Read())["Transport"]);
    }
}
