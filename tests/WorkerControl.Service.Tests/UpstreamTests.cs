using WorkerControl.Service.Traffic;

namespace WorkerControl.Service.Tests;

/// <summary>
/// Who is behind the addresses the reverse proxy forwards to.
/// </summary>
public class UpstreamTests
{
    private sealed class Machine : IMachineNetwork
    {
        public Dictionary<int, int> Ports { get; } = [];
        public List<MachineProcess> Running { get; } = [];
        public List<WebSite> Hosted { get; } = [];
        public int Listings { get; private set; }

        public IReadOnlyDictionary<int, int> Listeners() => Ports;

        public MachineProcess? Process(int processId) => Running.FirstOrDefault(process => process.ProcessId == processId);

        public IReadOnlyList<MachineProcess> Processes()
        {
            Listings++;
            return Running;
        }

        public IReadOnlyList<WebSite> Sites() => Hosted;
    }

    [Fact]
    public void A_port_the_application_listens_on_is_the_application()
    {
        var machine = new Machine { Ports = { [3000] = 812 }, Running = { new MachineProcess(812, "node", @"C:\node\node.exe") } };

        var owner = Assert.Single(UpstreamResolver.Resolve(["127.0.0.1:3000"], machine));

        Assert.Equal(812, Assert.Single(owner.Processes).ProcessId);
        Assert.Null(owner.Site);
        Assert.Equal(0, machine.Listings);
    }

    [Fact]
    public void A_port_the_web_server_listens_on_is_what_runs_from_the_folder_of_the_site()
    {
        var machine = new Machine
        {
            // The system listens for every site.
            Ports = { [9014] = 4, [9024] = 4 },
            Hosted = { new WebSite("Producao", 9014, @"D:\www\API\Producao"), new WebSite("Cadastro", 9024, "D:/www/API/Cadastro/") },
            Running =
            {
                new MachineProcess(4, "System", null),
                new MachineProcess(500, "Api.Producao", @"D:\www\API\Producao\Api.Producao.exe"),
                new MachineProcess(501, "Api.Cadastro", @"d:\WWW\api\cadastro\Api.Cadastro.exe"),
                new MachineProcess(502, "Api.ProducaoOutra", @"D:\www\API\ProducaoOutra\x.exe"),
                new MachineProcess(503, "w3wp", @"C:\Windows\System32\inetsrv\w3wp.exe")
            }
        };

        var owners = UpstreamResolver.Resolve(["127.0.0.1:9014", "127.0.0.1:9024", "127.0.0.1:9014"], machine);

        Assert.Equal(2, owners.Count);
        Assert.Equal(("Producao", 500), (owners[0].Site, Assert.Single(owners[0].Processes).ProcessId));
        Assert.Equal(("Cadastro", 501), (owners[1].Site, Assert.Single(owners[1].Processes).ProcessId));
        // The processes of the machine are listed once, however many addresses there are.
        Assert.Equal(1, machine.Listings);
    }

    [Theory]
    [InlineData("10.0.0.7:9014")]
    [InlineData("127.0.0.1:9999")]
    [InlineData("unix:/tmp/x.sock")]
    [InlineData("nonsense")]
    public void What_is_elsewhere_or_listened_on_by_nobody_known_has_no_owner(string upstream)
    {
        var machine = new Machine { Ports = { [9014] = 812 }, Running = { new MachineProcess(812, "app", null) } };

        var owner = Assert.Single(UpstreamResolver.Resolve([upstream], machine));

        Assert.Empty(owner.Processes);
        Assert.Null(owner.Site);
    }

    [Fact]
    public void The_sites_are_read_from_the_configuration_of_the_web_server()
    {
        var sites = UpstreamResolver.ReadSites("""
            <configuration>
              <system.applicationHost>
                <sites>
                  <site name="Producao 1" id="2">
                    <application path="/" applicationPool="Producao1">
                      <virtualDirectory path="/" physicalPath="D:\www\API\Producao" />
                      <virtualDirectory path="/files" physicalPath="D:\files" />
                    </application>
                    <application path="/other"><virtualDirectory path="/" physicalPath="D:\other" /></application>
                    <bindings>
                      <binding protocol="http" bindingInformation="*:9014:" />
                      <binding protocol="http" bindingInformation="127.0.0.1:9015:producao.local" />
                    </bindings>
                  </site>
                  <site name="Empty" id="3"><bindings><binding protocol="http" bindingInformation="*:81:" /></bindings></site>
                  <siteDefaults><logFile logFormat="W3C" /></siteDefaults>
                </sites>
              </system.applicationHost>
            </configuration>
            """);

        Assert.Equal([("Producao 1", 9014, @"D:\www\API\Producao"), ("Producao 1", 9015, @"D:\www\API\Producao")], sites.Select(site => (site.Name, site.Port, site.PhysicalPath)));
        Assert.Empty(UpstreamResolver.ReadSites("not xml"));
    }
}
