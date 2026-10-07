using WorkerControl.Core;

namespace WorkerControl.Core.Tests;

public class ConfigTests
{
    /// <summary>The sample file of 1.x, as it is.</summary>
    private const string Legacy = """
        {
            "ZapMQHost" : "localhost",
            "ZapMQPort" : 5679,
            "RateLoadConfig" : 180000,
            "WorkerGroups" : [
                {
                    "Enabled" : false,
                    "Name" : "Teste",
                    "ApplicationFullPath" : "C:\\Temp\\SimpleSample.exe",
                    "TotalWorkers" : 2,
                    "MonitoringRate": 30000,
                    "TimeoutKeepAlive" : 125000,
                    "Debug" : true,
                    "Boost" : {
                        "Enabled" : false,
                        "BoostWorkers" : 3,
                        "StartTime" : "12:00:00",
                        "EndTime" : "12:19:00"
                    }
                }
            ]
        }
        """;

    [Fact]
    public void Reads_a_1x_file_as_it_is()
    {
        var config = ConfigReader.Parse(Legacy);

        Assert.Equal("localhost", config.ZapMQHost);
        Assert.Equal(5679, config.ZapMQPort);
        Assert.Equal(TimeSpan.FromMinutes(3), config.RateLoadConfig);
        var group = Assert.Single(config.Groups);
        Assert.Equal("Teste", group.Name);
        Assert.False(group.Enabled);
        Assert.Equal("C:\\Temp\\SimpleSample.exe", group.ApplicationFullPath);
        Assert.Equal(2, group.TotalWorkers);
        Assert.Equal(TimeSpan.FromSeconds(30), group.MonitoringRate);
        Assert.Equal(TimeSpan.FromSeconds(125), group.TimeoutKeepAlive);
        Assert.Equal(new BoostConfig { Enabled = false, BoostWorkers = 3, StartTime = new TimeSpan(12, 0, 0), EndTime = new TimeSpan(12, 19, 0) }, group.Boost);

        // What 1.x did not have comes with its default.
        Assert.Equal(4, config.StartBatchSize);
        Assert.Equal(TimeSpan.FromSeconds(60), group.StartupGrace);
        Assert.Equal(TimeSpan.FromSeconds(30), group.SafeStopTimeout);
        Assert.Equal(3, group.CrashLimit);
        Assert.Null(group.WorkingDirectory);
        Assert.Equal("", group.Arguments);
    }

    [Fact]
    public void A_setting_given_at_the_root_is_for_every_group_unless_the_group_has_its_own()
    {
        var config = ConfigReader.Parse("""
            {
              "ZapMQHost": "broker", "ZapMQPort": 5679,
              "StartBatchSize": 2, "StartBatchIntervalMs": 500,
              "StartupGraceMs": 90000, "SafeStopTimeoutMs": 10000, "CrashLimit": 5,
              "WorkerGroups": [
                { "Name": "A", "ApplicationFullPath": "a.exe", "TotalWorkers": 1 },
                { "Name": "B", "ApplicationFullPath": "b.exe", "TotalWorkers": 1, "StartupGraceMs": 5000, "CrashWindowMs": 1000,
                  "Arguments": "--fast", "WorkingDirectory": "D:\\work" }
              ]
            }
            """);

        Assert.Equal(2, config.StartBatchSize);
        Assert.Equal(TimeSpan.FromMilliseconds(500), config.StartBatchInterval);
        Assert.Equal(TimeSpan.FromSeconds(90), config.Groups[0].StartupGrace);
        Assert.Equal(TimeSpan.FromSeconds(5), config.Groups[1].StartupGrace);
        Assert.All(config.Groups, group => Assert.Equal(TimeSpan.FromSeconds(10), group.SafeStopTimeout));
        Assert.All(config.Groups, group => Assert.Equal(5, group.CrashLimit));
        Assert.Equal(TimeSpan.FromSeconds(60), config.Groups[0].CrashWindow);
        Assert.Equal(TimeSpan.FromSeconds(1), config.Groups[1].CrashWindow);
        Assert.Equal("--fast", config.Groups[1].Arguments);
        Assert.Equal("D:\\work", config.Groups[1].WorkingDirectory);
        Assert.True(config.Groups[0].Enabled);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[]", "must hold a JSON object")]
    [InlineData("""{ "ZapMQPort": 5679, "WorkerGroups": [] }""", "\"ZapMQHost\" is required")]
    [InlineData("""{ "ZapMQHost": "h", "WorkerGroups": [] }""", "\"ZapMQPort\" is required")]
    [InlineData("""{ "ZapMQHost": "h", "ZapMQPort": 1 }""", "\"WorkerGroups\" must be a list")]
    [InlineData("""{ "ZapMQHost": "h", "ZapMQPort": 1, "WorkerGroups": [ { "ApplicationFullPath": "a", "TotalWorkers": 1 } ] }""", "needs a \"Name\"")]
    [InlineData("""{ "ZapMQHost": "h", "ZapMQPort": 1, "WorkerGroups": [ { "Name": "A", "TotalWorkers": 1 } ] }""", "\"ApplicationFullPath\" is required in group \"A\"")]
    [InlineData("""{ "ZapMQHost": "h", "ZapMQPort": 1, "WorkerGroups": [ { "Name": "A", "ApplicationFullPath": "a" } ] }""", "\"TotalWorkers\" is required in group \"A\"")]
    [InlineData("""{ "ZapMQHost": "h", "ZapMQPort": 1, "WorkerGroups": [ { "Name": "A", "ApplicationFullPath": "a", "TotalWorkers": -1 } ] }""", "cannot be less than 0")]
    [InlineData("""{ "ZapMQHost": "h", "ZapMQPort": 1, "WorkerGroups": [ { "Name": "A", "ApplicationFullPath": "a", "TotalWorkers": "2" } ] }""", "must be a whole number")]
    [InlineData("""{ "ZapMQHost": "h", "ZapMQPort": 1, "WorkerGroups": [ { "Name": "A", "ApplicationFullPath": "a", "TotalWorkers": 1 }, { "Name": "A", "ApplicationFullPath": "b", "TotalWorkers": 1 } ] }""", "two groups named \"A\"")]
    [InlineData("""{ "ZapMQHost": "h", "ZapMQPort": 1, "WorkerGroups": [ { "Name": "A", "ApplicationFullPath": "a", "TotalWorkers": 1, "Boost": { "StartTime": "25:00:00" } } ] }""", "must be a time of day")]
    [InlineData("""{ "ZapMQHost": "h", "ZapMQPort": 1, "WorkerGroups": [ { "Name": "A", "ApplicationFullPath": "a", "TotalWorkers": 1, "Enabled": "yes" } ] }""", "must be true or false")]
    public void A_file_with_anything_wrong_is_refused_saying_what(string json, string expected)
    {
        var error = Assert.Throws<ConfigException>(() => ConfigReader.Parse(json));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Comments_and_a_comma_too_many_are_tolerated()
    {
        var config = ConfigReader.Parse("""
            {
              // edited by hand
              "ZapMQHost": "h", "ZapMQPort": 1,
              "WorkerGroups": [ { "Name": "A", "ApplicationFullPath": "a", "TotalWorkers": 1, }, ],
            }
            """);

        Assert.Single(config.Groups);
    }
}
