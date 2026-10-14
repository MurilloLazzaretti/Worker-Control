using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using WorkerControl.Service.Database;
using WorkerControl.Service.Traffic;
using WorkerControl.Service.Transport;

namespace WorkerControl.Service.Tests;

/// <summary>
/// What a drawing of the environment is given: the applications of the web server with how
/// their sites stand, and who is connected to the database, process by process.
/// </summary>
public class LandscapeTests
{
    [Fact]
    public async Task The_applications_with_their_sites_and_the_processes_connected_to_the_database_are_told()
    {
        var instance = new PretendInstance();
        instance.Fast = instance.Fast with
        {
            Clients =
            [
                new ClientProcess("APP01", 7001, "Orders.Api", "Sales", 3, 1, 2),
                new ClientProcess("app01", 7001, "Orders.Api", "Sales", 2, 0, 40),
                new ClientProcess("DESK9", 512, "Management Studio", "Sales", 1, 0, 900),
                // Another database of the instance is nobody's business here.
                new ClientProcess("APP01", 7001, "Orders.Api", "master", 9, 9, 0)
            ]
        };
        await using var rig = new Rig { DatabaseJson = """{ "Name": "Test", "Server": "HOST\\ONE", "User": "app", "Password": "typed", "Databases": ["Sales"], "SampleSeconds": 5 }""" };
        var machine = new PretendMachine();
        var folder = Path.Combine(rig.Directory, "www", "Orders");
        Directory.CreateDirectory(folder);
        machine.WebSites.Add(new WebSite("Shop Api Orders 1", 9001, folder));
        machine.WebSites.Add(new WebSite("Shop Api Orders 2", 9002, folder));
        machine.StoppedSites.Add("Shop Api Orders 2");
        machine.Serving["Shop Api Orders 1"] = [7001];
        machine.Running.Add(new MachineProcess(7100, "Orders.Api", Path.Combine(folder, "Orders.Api.exe")));
        machine.Running.Add(new MachineProcess(7200, "Other", Path.Combine(rig.Directory, "www", "OrdersOld", "Other.exe")));
        rig.Services = machine;
        rig.Register = services =>
        {
            services.AddSingleton<IWebServer>(machine);
            services.AddSingleton<IMachineNetwork>(machine);
            services.AddSingleton<IDatabaseSource>(instance);
            services.AddSingleton<ISecretProtector>(new PretendProtector());
        };
        rig.WriteConfig();
        await rig.StartBrokerAsync();
        await rig.StartServiceAsync();

        JObject? seen = null;
        Assert.True(await Rig.Eventually(async () => (seen = await rig.CommandAsync("Landscape"))?["Database"]?["Clients"] is JArray { Count: > 0 }), seen?.ToString());

        var api = Assert.Single(seen!["Apis"]!);
        Assert.Equal(("Orders", folder), ((string?)api["Name"], (string?)api["Folder"]));
        // What runs from the folder of the application is its own, whoever started it.
        Assert.Equal([7100], api["ProcessIds"]!.Select(id => (int)id));
        Assert.Equal([("Shop Api Orders 1", 9001, true, 1), ("Shop Api Orders 2", 9002, false, 0)],
            api["Sites"]!.Select(site => ((string?)site["Name"], (int)site["Port"]!, (bool)site["Started"]!, site["ProcessIds"]!.Count())));
        Assert.Equal(Environment.MachineName, (string?)seen["Machine"]);

        var database = seen["Database"]!;
        Assert.Equal(("Test", true), ((string?)database["Name"], (bool)database["Online"]!));
        // One line for each process of each machine, however the machine wrote its own name.
        var clients = database["Clients"]!.OrderBy(client => (int)client["ProcessId"]!).ToList();
        Assert.Equal([(512, 1, 0, 900), (7001, 5, 1, 2)], clients.Select(client => ((int)client["ProcessId"]!, (int)client["Sessions"]!, (int)client["Running"]!, (int)client["IdleSeconds"]!)));
    }
}
