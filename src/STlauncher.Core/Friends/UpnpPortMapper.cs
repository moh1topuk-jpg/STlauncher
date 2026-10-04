using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace STlauncher.Core.Friends;

public enum UpnpFailure
{
    None,

    /// <summary>No router answered the search: UPnP is switched off, or the router has none.</summary>
    NoGateway,

    /// <summary>The router answered and would not add the mapping.</summary>
    Refused,

    /// <summary>
    /// The router's own outside address is a private one: it sits behind the provider's
    /// NAT, and a port opened on it is still not open to the internet.
    /// </summary>
    BehindAnotherNat,

    /// <summary>The router stopped answering half way, or the network failed.</summary>
    Error,

    /// <summary>
    /// The router took the mapping, yet a connection from outside did not get through:
    /// the provider's NAT in front of the router, or a firewall on this machine. Set by
    /// <see cref="FriendsHostSession"/> after the check through the relay, never by the
    /// mapper itself.
    /// </summary>
    NotReachable
}

/// <param name="InternalAddress">This machine's address on the home network.</param>
/// <param name="ExternalAddress">The router's outside address as the router reports it. Null when it would not say.</param>
public sealed record UpnpMapping(int ExternalPort, int InternalPort, string InternalAddress, string? ExternalAddress);

public sealed record UpnpResult(UpnpFailure Failure, UpnpMapping? Mapping)
{
    public bool Ok => Failure == UpnpFailure.None && Mapping is not null;
}

/// <summary>A WAN connection service of a router: its type and where to send commands for it.</summary>
public sealed record UpnpService(string ServiceType, Uri ControlUrl);

/// <summary>
/// Asks the home router to forward a port to this machine, the way consoles and torrent
/// clients do: find the router with an SSDP search, read its description, and send three
/// SOAP commands to its WAN connection service. Only as much of UPnP as that needs.
///
/// The mapping is taken for an hour and renewed while it is in use, so a launcher that
/// dies without cleaning up leaves a hole that closes by itself. On stop it is removed.
/// Everything the router sends is treated as untrusted: its description may only point
/// at the router itself, and XML is read with DTDs off.
/// </summary>
public sealed class UpnpPortMapper : IAsyncDisposable
{
    /// <summary>The name the mapping carries in the router's own list of forwarded ports.</summary>
    public const string MappingDescription = "STlauncher";

    private const int LeaseSeconds = 3600;
    private const int ConflictInMappingEntry = 718;
    private const int MaxPortAttempts = 4;

    private static readonly IPEndPoint SearchTarget = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly TimeSpan SearchTime = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan SearchGrace = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(20);

    private static readonly string[] SearchTypes =
    {
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2"
    };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Active? _active;
    private CancellationTokenSource? _renewStop;
    private Task? _renewLoop;

    public UpnpPortMapper()
    {
        // The router is on the home network: a system proxy has no business in between,
        // and a redirect from it is not something to follow.
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = RequestTimeout
        })
        {
            Timeout = RequestTimeout,
            MaxResponseContentBufferSize = 512 * 1024
        };
    }

    /// <summary>The mapping in place right now, or null.</summary>
    public UpnpMapping? Current => _active?.Mapping;

    /// <summary>
    /// Opens <paramref name="externalPort"/> (the same number as the internal one unless
    /// given) on the router and points it at this machine. If that port is taken by
    /// another device, a few random high ports are tried instead; the result says which
    /// one it ended up with. Replaces a mapping made earlier by this object.
    /// </summary>
    public async Task<UpnpResult> MapAsync(int internalPort, int? externalPort = null, CancellationToken cancellationToken = default)
    {
        if (internalPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(internalPort));
        }

        if (externalPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(externalPort));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await RemoveAsync().ConfigureAwait(false);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(OverallTimeout);

            try
            {
                return await FindAndMapAsync(internalPort, externalPort ?? internalPort, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new UpnpResult(UpnpFailure.Error, null);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes the mapping from the router. Safe to call when there is none.</summary>
    public async Task UnmapAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            await RemoveAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await UnmapAsync().ConfigureAwait(false);
        _http.Dispose();
    }

    private async Task<UpnpResult> FindAndMapAsync(int internalPort, int externalPort, CancellationToken token)
    {
        var locations = await DiscoverAsync(token).ConfigureAwait(false);
        var failure = UpnpFailure.NoGateway;

        foreach (var location in locations)
        {
            string description;

            try
            {
                description = await _http.GetStringAsync(location, token).ConfigureAwait(false);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                continue;
            }

            foreach (var service in ParseDescription(description, location))
            {
                UpnpResult result;

                try
                {
                    result = await MapOnAsync(service, location, internalPort, externalPort, token).ConfigureAwait(false);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    result = new UpnpResult(UpnpFailure.Error, null);
                }

                if (result.Ok)
                {
                    return result;
                }

                failure = result.Failure;
            }
        }

        return new UpnpResult(failure, null);
    }

    private async Task<UpnpResult> MapOnAsync(UpnpService service, Uri location, int internalPort, int externalPort, CancellationToken token)
    {
        var local = LocalAddressTowards(location.Host);

        if (local is null)
        {
            return new UpnpResult(UpnpFailure.Error, null);
        }

        string? external = null;
        var (gotAddress, addressBody) = await CallAsync(service, "GetExternalIPAddress", Array.Empty<(string, string)>(), token).ConfigureAwait(false);

        if (gotAddress)
        {
            external = ParseExternalAddress(addressBody);
        }

        if (external is not null && IPAddress.TryParse(external, out var outside))
        {
            // A router with a WAN service that is not connected reports no address:
            // the other service, if there is one, is the live one.
            if (outside.Equals(IPAddress.Any))
            {
                return new UpnpResult(UpnpFailure.Error, null);
            }

            if (!IsPublicAddress(outside))
            {
                return new UpnpResult(UpnpFailure.BehindAnotherNat, null);
            }
        }

        var port = externalPort;

        for (var attempt = 0; attempt < MaxPortAttempts; attempt++)
        {
            var lease = LeaseSeconds;
            var (added, body) = await AddAsync(service, port, internalPort, local, lease, token).ConfigureAwait(false);

            if (!added && ParseFaultCode(body) != ConflictInMappingEntry)
            {
                // Some routers only take permanent mappings and say so with one of
                // several error codes. Such a mapping lives until it is removed, which
                // stopping does.
                lease = 0;
                (added, body) = await AddAsync(service, port, internalPort, local, lease, token).ConfigureAwait(false);
            }

            if (!added && ParseFaultCode(body) == ConflictInMappingEntry &&
                await IsOwnMappingAsync(service, port, internalPort, local, token).ConfigureAwait(false))
            {
                // The port is taken by this very machine and port: a mapping left by a
                // session that did not end cleanly. It is adopted, and removed on stop.
                added = true;
                lease = 0;
            }

            if (added)
            {
                var mapping = new UpnpMapping(port, internalPort, local.ToString(), external);
                _active = new Active(service, mapping);
                StartRenewing(lease);
                return new UpnpResult(UpnpFailure.None, mapping);
            }

            if (ParseFaultCode(body) != ConflictInMappingEntry)
            {
                return new UpnpResult(UpnpFailure.Refused, null);
            }

            // Another device holds this port. Taking it away would break whatever that
            // device is doing, so a different one is tried.
            port = RandomNumberGenerator.GetInt32(20000, 60000);
        }

        return new UpnpResult(UpnpFailure.Refused, null);
    }

    private Task<(bool Ok, string Body)> AddAsync(UpnpService service, int externalPort, int internalPort, IPAddress local, int lease, CancellationToken token)
        => CallAsync(service, "AddPortMapping", new[]
        {
            ("NewRemoteHost", string.Empty),
            ("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
            ("NewProtocol", "TCP"),
            ("NewInternalPort", internalPort.ToString(CultureInfo.InvariantCulture)),
            ("NewInternalClient", local.ToString()),
            ("NewEnabled", "1"),
            ("NewPortMappingDescription", MappingDescription),
            ("NewLeaseDuration", lease.ToString(CultureInfo.InvariantCulture))
        }, token);

    private async Task<bool> IsOwnMappingAsync(UpnpService service, int externalPort, int internalPort, IPAddress local, CancellationToken token)
    {
        var (ok, body) = await CallAsync(service, "GetSpecificPortMappingEntry", new[]
        {
            ("NewRemoteHost", string.Empty),
            ("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
            ("NewProtocol", "TCP")
        }, token).ConfigureAwait(false);

        return ok && ParsePortMappingEntry(body) is { } entry &&
               entry.InternalPort == internalPort &&
               string.Equals(entry.InternalClient, local.ToString(), StringComparison.Ordinal);
    }

    private void StartRenewing(int lease)
    {
        if (lease <= 0 || _active is null)
        {
            return;
        }

        var active = _active;
        var stop = new CancellationTokenSource();
        _renewStop = stop;

        _renewLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(lease / 2), stop.Token).ConfigureAwait(false);

                    try
                    {
                        var local = IPAddress.Parse(active.Mapping.InternalAddress);
                        await AddAsync(active.Service, active.Mapping.ExternalPort, active.Mapping.InternalPort, local, lease, stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception) when (!stop.IsCancellationRequested)
                    {
                        // A router that was restarting gets asked again at the next half lease.
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task RemoveAsync()
    {
        var active = _active;
        _active = null;

        if (_renewStop is not null)
        {
            _renewStop.Cancel();

            if (_renewLoop is not null)
            {
                await _renewLoop.ConfigureAwait(false);
            }

            _renewStop.Dispose();
            _renewStop = null;
            _renewLoop = null;
        }

        if (active is null)
        {
            return;
        }

        try
        {
            await CallAsync(active.Service, "DeletePortMapping", new[]
            {
                ("NewRemoteHost", string.Empty),
                ("NewExternalPort", active.Mapping.ExternalPort.ToString(CultureInfo.InvariantCulture)),
                ("NewProtocol", "TCP")
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The router is gone or silent. There is nothing more to do from here, and
            // a leased mapping runs out by itself.
        }
    }

    private async Task<(bool Ok, string Body)> CallAsync(UpnpService service, string action, (string Name, string Value)[] arguments, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, service.ControlUrl);

        // Routers' small HTTP servers are picky: the quotes around the action and the
        // charset are part of what they match, and a length is expected, not chunks.
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(BuildRequest(service.ServiceType, action, arguments)));
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service.ServiceType}#{action}\"");

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        return (response.IsSuccessStatusCode, body);
    }

    private static Task<IReadOnlyList<Uri>> DiscoverAsync(CancellationToken token)
        => SearchAsync(SearchTarget, LocalAddresses(), token);

    /// <summary>
    /// Sends the SSDP search out of each of the given adapters and collects the
    /// description addresses that come back. The target is the SSDP multicast group in
    /// real use; it is a parameter so the exchange can be exercised against a stand-in.
    /// </summary>
    public static async Task<IReadOnlyList<Uri>> SearchAsync(IPEndPoint target, IEnumerable<IPAddress> adapters, CancellationToken token)
    {
        var found = new List<Uri>();
        var sockets = new List<Socket>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var search = CancellationTokenSource.CreateLinkedTokenSource(token);

        try
        {
            // One socket per home-network address: a multicast search leaves through
            // the adapter it is told to, and the default one is not always the router's.
            // The sockets are deliberately not bound: the system picks a port when the
            // search is sent, and a socket that only hears answers to its own question
            // is a plain client in a firewall's eyes.
            foreach (var adapter in adapters)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, adapter.GetAddressBytes());
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);

                    foreach (var type in SearchTypes)
                    {
                        socket.SendTo(Encoding.ASCII.GetBytes(
                            "M-SEARCH * HTTP/1.1\r\n" +
                            "HOST: 239.255.255.250:1900\r\n" +
                            "MAN: \"ssdp:discover\"\r\n" +
                            "MX: 1\r\n" +
                            $"ST: {type}\r\n\r\n"), SocketFlags.None, target);
                    }

                    sockets.Add(socket);
                }
                catch (SocketException)
                {
                    socket.Dispose();
                }
            }

            if (sockets.Count == 0)
            {
                return found;
            }

            // Listening starts after the search has gone out: only then does the socket
            // have a port to listen on. Answers that came in between wait in its buffer.
            var listeners = sockets.Select(socket => ListenAsync(socket, found, first, search.Token)).ToArray();

            // The router answers within a second. Once it has, a moment more catches a
            // second answer for the other device type, and that is enough.
            if (await Task.WhenAny(first.Task, Task.Delay(SearchTime, token)).ConfigureAwait(false) == first.Task)
            {
                await Task.Delay(SearchGrace, token).ConfigureAwait(false);
            }

            search.Cancel();
            await Task.WhenAll(listeners).ConfigureAwait(false);
        }
        finally
        {
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
        }

        token.ThrowIfCancellationRequested();

        lock (found)
        {
            return found.ToList();
        }
    }

    private static async Task ListenAsync(Socket socket, List<Uri> found, TaskCompletionSource first, CancellationToken token)
    {
        var buffer = new byte[4096];

        try
        {
            while (true)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token).ConfigureAwait(false);
                var location = ParseSearchResponse(Encoding.ASCII.GetString(buffer, 0, received.ReceivedBytes));

                if (location is null)
                {
                    continue;
                }

                lock (found)
                {
                    if (!found.Contains(location))
                    {
                        found.Add(location);
                    }
                }

                first.TrySetResult();
            }
        }
        catch (Exception)
        {
            // Cancelled when the search is over, or the adapter went away under it.
        }
    }

    /// <summary>The IPv4 addresses of adapters that have a gateway: the ones a router can be behind.</summary>
    private static IEnumerable<IPAddress> LocalAddresses()
    {
        NetworkInterface[] adapters;

        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var adapter in adapters)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;

            try
            {
                properties = adapter.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            // An adapter without a gateway - a virtual switch, a host-only network - has
            // no router to ask.
            if (!properties.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                {
                    yield return unicast.Address;
                }
            }
        }
    }

    /// <summary>This machine's address on the network the router is on: what the router should forward to.</summary>
    private static IPAddress? LocalAddressTowards(string routerHost)
    {
        if (!IPAddress.TryParse(routerHost, out var router))
        {
            return null;
        }

        try
        {
            // Connecting a UDP socket sends nothing; it only makes the system pick the
            // route, and with it the local address.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(router, 1900);
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes the description's address out of an answer to the search. Only an http
    /// address on a private IP literal is accepted: the answer comes from whatever is on
    /// the local network, and it must not be able to send the launcher anywhere else.
    /// </summary>
    public static Uri? ParseSearchResponse(string response)
    {
        if (string.IsNullOrEmpty(response))
        {
            return null;
        }

        var lines = response.Split('\n');

        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) || !lines[0].Contains(" 200"))
        {
            return null;
        }

        foreach (var raw in lines.Skip(1))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');

            if (colon <= 0 || !line[..colon].Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();

            if (Uri.TryCreate(value, UriKind.Absolute, out var location) &&
                location.Scheme == Uri.UriSchemeHttp &&
                IPAddress.TryParse(location.Host, out var host) &&
                host.AddressFamily == AddressFamily.InterNetwork &&
                !IsPublicAddress(host) && !IPAddress.IsLoopback(host))
            {
                return location;
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// Finds the WAN connection services in a router's description, IP connections ahead
    /// of PPP ones. Control addresses are resolved against URLBase or the description's
    /// own address, and any that point at a different host are dropped.
    /// </summary>
    public static IReadOnlyList<UpnpService> ParseDescription(string xml, Uri location)
    {
        var services = new List<UpnpService>();
        var document = LoadXml(xml);

        if (document is null)
        {
            return services;
        }

        var baseUri = location;
        var urlBase = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value.Trim();

        if (Uri.TryCreate(urlBase, UriKind.Absolute, out var declared) &&
            declared.Scheme == Uri.UriSchemeHttp &&
            string.Equals(declared.Host, location.Host, StringComparison.OrdinalIgnoreCase))
        {
            baseUri = declared;
        }

        foreach (var service in document.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            var type = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value.Trim();
            var control = service.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value.Trim();

            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(control) || !IsWanConnection(type))
            {
                continue;
            }

            if (Uri.TryCreate(baseUri, control, out var controlUrl) &&
                controlUrl.Scheme == Uri.UriSchemeHttp &&
                string.Equals(controlUrl.Host, location.Host, StringComparison.OrdinalIgnoreCase))
            {
                services.Add(new UpnpService(type, controlUrl));
            }
        }

        return services
            .OrderBy(s => s.ServiceType.Contains(":WANIPConnection:", StringComparison.Ordinal) ? 0 : 1)
            .ToList();
    }

    /// <summary>The address out of an answer to GetExternalIPAddress, or null when there is none.</summary>
    public static string? ParseExternalAddress(string soap)
    {
        var value = LoadXml(soap)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value.Trim();

        return !string.IsNullOrEmpty(value) && IPAddress.TryParse(value, out _) ? value : null;
    }

    /// <summary>The UPnP error number out of a SOAP fault, or null when the text is not one.</summary>
    public static int? ParseFaultCode(string soap)
    {
        var value = LoadXml(soap)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value.Trim();

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    /// <summary>Who an existing mapping forwards to, out of an answer to GetSpecificPortMappingEntry.</summary>
    public static (string InternalClient, int InternalPort)? ParsePortMappingEntry(string soap)
    {
        var document = LoadXml(soap);
        var client = document?.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewInternalClient")?.Value.Trim();
        var port = document?.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewInternalPort")?.Value.Trim();

        return !string.IsNullOrEmpty(client) && int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? (client, number)
            : null;
    }

    /// <summary>The SOAP envelope for one action of a service.</summary>
    public static string BuildRequest(string serviceType, string action, IEnumerable<(string Name, string Value)> arguments)
    {
        var text = new StringBuilder();
        text.Append("<?xml version=\"1.0\"?>\r\n");
        text.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
        text.Append("<s:Body>");
        text.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(SecurityElement.Escape(serviceType)).Append("\">");

        foreach (var (name, value) in arguments)
        {
            text.Append('<').Append(name).Append('>').Append(SecurityElement.Escape(value)).Append("</").Append(name).Append('>');
        }

        text.Append("</u:").Append(action).Append('>');
        text.Append("</s:Body></s:Envelope>\r\n");
        return text.ToString();
    }

    /// <summary>
    /// False for the ranges that never reach the internet: home networks, the providers'
    /// shared range (100.64/10), link-local, loopback and the reserved ends.
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();

        return b[0] switch
        {
            0 or 10 or 127 => false,
            100 when b[1] >= 64 && b[1] <= 127 => false,
            169 when b[1] == 254 => false,
            172 when b[1] >= 16 && b[1] <= 31 => false,
            192 when b[1] == 168 => false,
            >= 224 => false,
            _ => true
        };
    }

    private static bool IsWanConnection(string serviceType)
        => serviceType.StartsWith("urn:schemas-upnp-org:service:WANIPConnection:", StringComparison.Ordinal) ||
           serviceType.StartsWith("urn:schemas-upnp-org:service:WANPPPConnection:", StringComparison.Ordinal);

    private static XDocument? LoadXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        // Some routers put a byte-order mark or a stray line before the declaration.
        var start = xml.IndexOf('<');

        if (start < 0)
        {
            return null;
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml[start..]), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 2_000_000
            });

            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private sealed record Active(UpnpService Service, UpnpMapping Mapping);
}
