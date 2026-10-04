using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Friends;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// What a router says, as fixtures: the answer to the search, the description with its
/// nested devices, and the SOAP answers. Talking to a real router is not something a
/// test may do.
/// </summary>
public class UpnpPortMapperTests
{
    private static readonly Uri Location = new("http://192.168.1.1:5000/rootDesc.xml");

    private const string SearchAnswer =
        "HTTP/1.1 200 OK\r\n" +
        "CACHE-CONTROL: max-age=120\r\n" +
        "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
        "USN: uuid:3ddcd1d3-2380-45f5-b069-2c4d54008cf2::urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
        "EXT:\r\n" +
        "SERVER: OpenWRT/23.05 UPnP/1.1 MiniUPnPd/2.3.3\r\n" +
        "Location: http://192.168.1.1:5000/rootDesc.xml\r\n" +
        "\r\n";

    // The shape every home router uses: the gateway device holds a WAN device, which
    // holds a WAN connection device, which holds the service that maps ports.
    private const string Description = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <device>
            <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
            <friendlyName>Home router</friendlyName>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>
                <controlURL>/ctl/L3F</controlURL>
              </service>
            </serviceList>
            <deviceList>
              <device>
                <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType>
                    <controlURL>/ctl/CmnIfCfg</controlURL>
                  </service>
                </serviceList>
                <deviceList>
                  <device>
                    <deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
                    <serviceList>
                      <service>
                        <serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType>
                        <controlURL>/ctl/PPPConn</controlURL>
                      </service>
                      <service>
                        <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                        <serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>
                        <controlURL>/ctl/IPConn</controlURL>
                        <eventSubURL>/evt/IPConn</eventSubURL>
                        <SCPDURL>/WANIPCn.xml</SCPDURL>
                      </service>
                    </serviceList>
                  </device>
                </deviceList>
              </device>
            </deviceList>
          </device>
        </root>
        """;

    [Fact]
    public void The_search_answer_gives_the_description_address()
    {
        Assert.Equal(Location, UpnpPortMapper.ParseSearchResponse(SearchAnswer));
    }

    [Fact]
    public async Task The_search_hears_the_answer_to_its_own_question()
    {
        // A stand-in for the router on the loopback address: it takes the search as a
        // plain datagram and answers the port it came from, as a router does.
        using var router = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)router.Client.LocalEndPoint!).Port;

        var answering = Task.Run(async () =>
        {
            var question = await router.ReceiveAsync();
            var answer = Encoding.ASCII.GetBytes(SearchAnswer);
            await router.SendAsync(answer, answer.Length, question.RemoteEndPoint);
            return Encoding.ASCII.GetString(question.Buffer);
        });

        var found = await UpnpPortMapper.SearchAsync(
            new IPEndPoint(IPAddress.Loopback, port),
            new[] { IPAddress.Loopback },
            CancellationToken.None);

        Assert.Equal(new[] { Location }, found);

        var asked = await answering;
        Assert.StartsWith("M-SEARCH * HTTP/1.1", asked);
        Assert.Contains("\"ssdp:discover\"", asked);
        Assert.Contains("InternetGatewayDevice", asked);
    }

    [Theory]
    [InlineData("HTTP/1.1 404 Not Found\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nLOCATION: http://8.8.8.8/rootDesc.xml\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nLOCATION: http://router.example.org/rootDesc.xml\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nLOCATION: https://192.168.1.1/rootDesc.xml\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nLOCATION: file:///etc/passwd\r\n\r\n")]
    [InlineData("NOTIFY * HTTP/1.1\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n")]
    [InlineData("")]
    public void An_answer_that_points_anywhere_but_a_home_router_is_ignored(string answer)
    {
        Assert.Null(UpnpPortMapper.ParseSearchResponse(answer));
    }

    [Fact]
    public void The_description_gives_the_wan_services_with_ip_ahead_of_ppp()
    {
        var services = UpnpPortMapper.ParseDescription(Description, Location);

        Assert.Equal(2, services.Count);
        Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:1", services[0].ServiceType);
        Assert.Equal(new Uri("http://192.168.1.1:5000/ctl/IPConn"), services[0].ControlUrl);
        Assert.Equal("urn:schemas-upnp-org:service:WANPPPConnection:1", services[1].ServiceType);
    }

    [Fact]
    public void Control_addresses_resolve_against_the_base_and_never_leave_the_router()
    {
        var withBase = Description.Replace(
            "<specVersion>",
            "<URLBase>http://192.168.1.1:49152/upnp/</URLBase><specVersion>")
            .Replace("/ctl/IPConn", "control/WANIPConn1");

        var services = UpnpPortMapper.ParseDescription(withBase, Location);
        Assert.Equal(new Uri("http://192.168.1.1:49152/upnp/control/WANIPConn1"), services[0].ControlUrl);

        // A base on another host is not believed, and neither is a control address there.
        var foreignBase = Description.Replace("<specVersion>", "<URLBase>http://203.0.113.9/</URLBase><specVersion>");
        Assert.Equal(new Uri("http://192.168.1.1:5000/ctl/IPConn"), UpnpPortMapper.ParseDescription(foreignBase, Location)[0].ControlUrl);

        var foreignControl = Description
            .Replace("/ctl/IPConn", "http://203.0.113.9/ctl/IPConn")
            .Replace("/ctl/PPPConn", "http://203.0.113.9/ctl/PPPConn");
        Assert.Empty(UpnpPortMapper.ParseDescription(foreignControl, Location));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<root><device><serviceList><service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>")]
    [InlineData("<!DOCTYPE root [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><root>&x;</root>")]
    public void A_broken_or_hostile_description_gives_no_services(string xml)
    {
        Assert.Empty(UpnpPortMapper.ParseDescription(xml, Location));
    }

    [Fact]
    public void The_external_address_is_read_from_the_answer()
    {
        const string answer = """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:GetExternalIPAddressResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">
                  <NewExternalIPAddress>198.51.100.24</NewExternalIPAddress>
                </u:GetExternalIPAddressResponse>
              </s:Body>
            </s:Envelope>
            """;

        Assert.Equal("198.51.100.24", UpnpPortMapper.ParseExternalAddress(answer));
        Assert.Null(UpnpPortMapper.ParseExternalAddress(answer.Replace("198.51.100.24", "")));
        Assert.Null(UpnpPortMapper.ParseExternalAddress(answer.Replace("198.51.100.24", "<b>router</b>")));
        Assert.Null(UpnpPortMapper.ParseExternalAddress("garbage"));
    }

    [Fact]
    public void A_fault_gives_its_error_number()
    {
        const string fault = """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <s:Fault>
                  <faultcode>s:Client</faultcode>
                  <faultstring>UPnPError</faultstring>
                  <detail>
                    <UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                      <errorCode>718</errorCode>
                      <errorDescription>ConflictInMappingEntry</errorDescription>
                    </UPnPError>
                  </detail>
                </s:Fault>
              </s:Body>
            </s:Envelope>
            """;

        Assert.Equal(718, UpnpPortMapper.ParseFaultCode(fault));
        Assert.Null(UpnpPortMapper.ParseFaultCode("<html><body>500</body></html>"));
        Assert.Null(UpnpPortMapper.ParseFaultCode(""));
    }

    [Fact]
    public void An_existing_mapping_says_who_it_forwards_to()
    {
        const string entry = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <u:GetSpecificPortMappingEntryResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">
                  <NewInternalPort>25565</NewInternalPort>
                  <NewInternalClient>192.168.1.42</NewInternalClient>
                  <NewEnabled>1</NewEnabled>
                  <NewPortMappingDescription>STlauncher</NewPortMappingDescription>
                  <NewLeaseDuration>3600</NewLeaseDuration>
                </u:GetSpecificPortMappingEntryResponse>
              </s:Body>
            </s:Envelope>
            """;

        Assert.Equal(("192.168.1.42", 25565), UpnpPortMapper.ParsePortMappingEntry(entry));
        Assert.Null(UpnpPortMapper.ParsePortMappingEntry("<s:Envelope xmlns:s=\"x\"><s:Body/></s:Envelope>"));
    }

    [Fact]
    public void A_request_names_its_action_and_escapes_its_values()
    {
        var request = UpnpPortMapper.BuildRequest(
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "AddPortMapping",
            new[] { ("NewExternalPort", "25565"), ("NewPortMappingDescription", "a<b>&c") });

        Assert.Contains("<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">", request);
        Assert.Contains("<NewExternalPort>25565</NewExternalPort>", request);
        Assert.Contains("<NewPortMappingDescription>a&lt;b&gt;&amp;c</NewPortMappingDescription>", request);
        Assert.Contains("</u:AddPortMapping>", request);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("198.51.100.24", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.16.4.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.254", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("169.254.10.10", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    public void Private_and_shared_ranges_are_not_public(string address, bool isPublic)
    {
        Assert.Equal(isPublic, UpnpPortMapper.IsPublicAddress(IPAddress.Parse(address)));
    }
}
