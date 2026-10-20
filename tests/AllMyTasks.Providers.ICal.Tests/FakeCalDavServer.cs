using System.Net;
using System.Text;
using System.Xml.Linq;

namespace AllMyTasks.Providers.ICal.Tests;

/// <summary>A tiny in-memory CalDAV server (just enough of RFC 4791 / 6578) for exercising the client without a network.</summary>
internal sealed class FakeCalDavServer : HttpMessageHandler
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace Cs = "http://calendarserver.org/ns/";
    private static readonly XNamespace Apple = "http://apple.com/ns/ical/";

    public sealed class Calendar(string path, string name)
    {
        public string Path { get; } = path;

        public string Name { get; } = name;

        public string? Color { get; set; } = "#FF8800FF";

        public string Components { get; set; } = "VEVENT";

        public int Version { get; set; } = 1;

        public int MinValidVersion { get; set; } = 1;

        public Dictionary<string, (string ETag, string Ics, int Version)> Resources { get; } = [];

        public List<(int Version, string Href, bool Deleted)> Log { get; } = [];

        public string CTag => "ctag-" + Version;

        public void Put(string file, string ics)
        {
            Version++;
            Resources[Path + file] = ("\"" + Version + "\"", ics, Version);
            Log.Add((Version, Path + file, false));
        }

        public void Delete(string file)
        {
            Version++;
            Resources.Remove(Path + file);
            Log.Add((Version, Path + file, true));
        }
    }

    public string User { get; set; } = "me";

    public string Password { get; set; } = "pw";

    public bool SupportsSyncCollection { get; set; } = true;

    public bool SupportsCTag { get; set; } = true;

    public bool WellKnownOnly { get; set; }

    public bool AddressIsCalendar { get; set; }

    public Dictionary<string, Calendar> Calendars { get; } = [];

    public List<(string Method, string Path, string Kind)> Requests { get; } = [];

    public string? LastAuthorization { get; private set; }

    public Calendar Add(string file, string name)
    {
        var calendar = new Calendar("/dav/cal/" + file + "/", name);
        Calendars[calendar.Path] = calendar;
        return calendar;
    }

    public int Count(string kind) => Requests.Count(r => r.Kind == kind);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastAuthorization = request.Headers.Authorization?.ToString();
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
        var body = request.Content is null ? string.Empty : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        var path = request.RequestUri!.AbsolutePath;
        var kind = Classify(request.Method.Method, body);
        Requests.Add((request.Method.Method, path, kind));

        if (request.Headers.Authorization?.Parameter != expected)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        return Task.FromResult(Handle(request, path, body, kind));
    }

    private static string Classify(string method, string body)
    {
        if (method == "PROPFIND")
        {
            return "propfind";
        }

        var root = body.Length == 0 ? null : XDocument.Parse(body).Root?.Name.LocalName;
        return root ?? method.ToLowerInvariant();
    }

    private HttpResponseMessage Handle(HttpRequestMessage request, string path, string body, string kind)
    {
        if (path == "/.well-known/caldav")
        {
            var r = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            r.Headers.Location = new Uri("/dav/", UriKind.Relative);
            return r;
        }

        if (request.Method.Method == "PROPFIND")
        {
            var depth = request.Headers.TryGetValues("Depth", out var d) ? d.Single() : "0";
            return Propfind(path, depth, XDocument.Parse(body));
        }

        if (!Calendars.TryGetValue(path, out var calendar))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return kind switch
        {
            "sync-collection" => SyncCollection(calendar, XDocument.Parse(body)),
            "calendar-multiget" => Multiget(calendar, XDocument.Parse(body)),
            "calendar-query" => Query(calendar),
            _ => new HttpResponseMessage(HttpStatusCode.NotImplemented),
        };
    }

    private HttpResponseMessage Propfind(string path, string depth, XDocument request)
    {
        var wanted = request.Root!.Element(D + "prop")!.Elements().Select(e => e.Name).ToHashSet();
        var responses = new List<XElement>();

        if (path == "/" && !AddressIsCalendar)
        {
            if (WellKnownOnly)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            responses.Add(Response("/", Prop(wanted, resourceType: [D + "collection"], principal: "/principals/me/")));
        }
        else if (path == "/dav/" && !AddressIsCalendar)
        {
            responses.Add(Response(path, Prop(wanted, resourceType: [D + "collection"], principal: "/principals/me/")));
        }
        else if (path == "/principals/me/")
        {
            responses.Add(Response(path, Prop(wanted, home: "/dav/cal/")));
        }
        else if (path == "/dav/cal/")
        {
            responses.Add(Response(path, Prop(wanted, resourceType: [D + "collection"])));
            if (depth == "1")
            {
                responses.AddRange(Calendars.Values.Select(c => Response(c.Path, CalendarProp(wanted, c))));
            }
        }
        else if (Calendars.TryGetValue(path, out var calendar))
        {
            responses.Add(Response(path, CalendarProp(wanted, calendar)));
            if (depth == "1")
            {
                foreach (var (href, resource) in calendar.Resources)
                {
                    responses.Add(Response(href, new XElement(D + "prop", new XElement(D + "getetag", resource.ETag), new XElement(D + "resourcetype"))));
                }
            }
        }
        else
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return Multistatus(responses);
    }

    private XElement CalendarProp(HashSet<XName> wanted, Calendar c)
    {
        var prop = Prop(wanted, resourceType: [D + "collection", C + "calendar"]);
        if (wanted.Contains(D + "displayname"))
        {
            prop.Add(new XElement(D + "displayname", c.Name));
        }

        if (wanted.Contains(Apple + "calendar-color") && c.Color is not null)
        {
            prop.Add(new XElement(Apple + "calendar-color", c.Color));
        }

        if (wanted.Contains(C + "supported-calendar-component-set"))
        {
            prop.Add(new XElement(C + "supported-calendar-component-set", new XElement(C + "comp", new XAttribute("name", c.Components))));
        }

        if (wanted.Contains(Cs + "getctag") && SupportsCTag)
        {
            prop.Add(new XElement(Cs + "getctag", c.CTag));
        }

        if (wanted.Contains(D + "sync-token") && SupportsSyncCollection)
        {
            prop.Add(new XElement(D + "sync-token", "tok-" + c.Version));
        }

        if (wanted.Contains(D + "supported-report-set") && SupportsSyncCollection)
        {
            prop.Add(new XElement(D + "supported-report-set", new XElement(D + "supported-report", new XElement(D + "report", new XElement(D + "sync-collection")))));
        }

        return prop;
    }

    private static XElement Prop(HashSet<XName> wanted, XName[]? resourceType = null, string? principal = null, string? home = null)
    {
        var prop = new XElement(D + "prop");
        if (wanted.Contains(D + "resourcetype") && resourceType is not null)
        {
            prop.Add(new XElement(D + "resourcetype", resourceType.Select(n => new XElement(n))));
        }

        if (wanted.Contains(D + "current-user-principal") && principal is not null)
        {
            prop.Add(new XElement(D + "current-user-principal", new XElement(D + "href", principal)));
        }

        if (wanted.Contains(C + "calendar-home-set") && home is not null)
        {
            prop.Add(new XElement(C + "calendar-home-set", new XElement(D + "href", home)));
        }

        return prop;
    }

    private HttpResponseMessage SyncCollection(Calendar calendar, XDocument request)
    {
        if (!SupportsSyncCollection)
        {
            return new HttpResponseMessage(HttpStatusCode.NotImplemented);
        }

        var token = request.Root!.Element(D + "sync-token")!.Value;
        var since = 0;
        if (token.Length > 0)
        {
            since = int.Parse(token[4..], System.Globalization.CultureInfo.InvariantCulture);
            if (since < calendar.MinValidVersion - 1 || !token.StartsWith("tok-", StringComparison.Ordinal))
            {
                return Error(HttpStatusCode.Forbidden, new XElement(D + "error", new XElement(D + "valid-sync-token")));
            }
        }

        var latest = new Dictionary<string, bool>();
        foreach (var (version, href, deleted) in calendar.Log.Where(l => l.Version > since))
        {
            latest[href] = deleted;
        }

        var responses = latest.Select(kv => kv.Value
            ? new XElement(D + "response", new XElement(D + "href", kv.Key), new XElement(D + "status", "HTTP/1.1 404 Not Found"))
            : Response(kv.Key, new XElement(D + "prop", new XElement(D + "getetag", calendar.Resources[kv.Key].ETag)))).ToList();
        var ms = new XElement(D + "multistatus", responses, new XElement(D + "sync-token", "tok-" + calendar.Version));
        return Xml(ms);
    }

    private static HttpResponseMessage Multiget(Calendar calendar, XDocument request)
    {
        var responses = new List<XElement>();
        foreach (var href in request.Root!.Elements(D + "href").Select(h => h.Value))
        {
            responses.Add(calendar.Resources.TryGetValue(href, out var r)
                ? Response(href, new XElement(D + "prop", new XElement(D + "getetag", r.ETag), new XElement(C + "calendar-data", r.Ics)))
                : new XElement(D + "response", new XElement(D + "href", href), new XElement(D + "status", "HTTP/1.1 404 Not Found")));
        }

        return Multistatus(responses);
    }

    private static HttpResponseMessage Query(Calendar calendar) =>
        Multistatus(calendar.Resources.Select(kv =>
            Response(kv.Key, new XElement(D + "prop", new XElement(D + "getetag", kv.Value.ETag), new XElement(C + "calendar-data", kv.Value.Ics)))));

    private static XElement Response(string href, XElement prop) =>
        new(D + "response", new XElement(D + "href", href), new XElement(D + "propstat", prop, new XElement(D + "status", "HTTP/1.1 200 OK")));

    private static HttpResponseMessage Multistatus(IEnumerable<XElement> responses) => Xml(new XElement(D + "multistatus", responses));

    private static HttpResponseMessage Xml(XElement root) =>
        new((HttpStatusCode)207) { Content = new StringContent(new XDocument(root).ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml") };

    private static HttpResponseMessage Error(HttpStatusCode status, XElement root) =>
        new(status) { Content = new StringContent(new XDocument(root).ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml") };
}
