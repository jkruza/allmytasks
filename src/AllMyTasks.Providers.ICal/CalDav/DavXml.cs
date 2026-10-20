using System.Xml;
using System.Xml.Linq;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Providers.ICal.CalDav;

/// <summary>XML names and the few request shapes CalDAV needs, plus safe parsing of multistatus responses.</summary>
internal static class DavXml
{
    public static readonly XNamespace D = "DAV:";
    public static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    public static readonly XNamespace Apple = "http://apple.com/ns/ical/";
    public static readonly XNamespace Cs = "http://calendarserver.org/ns/";

    /// <summary>Parses XML with DTDs and external entities switched off (untrusted input).</summary>
    public static XDocument Parse(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = 64L * 1024 * 1024,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            throw new SourceDataException("The server's response was not valid XML.");
        }
    }

    public static string PropfindBody(params XName[] props)
    {
        var doc = new XDocument(
            new XElement(
                D + "propfind",
                new XAttribute(XNamespace.Xmlns + "d", D.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "c", C.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", Apple.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "cs", Cs.NamespaceName),
                new XElement(D + "prop", props.Select(p => new XElement(p)))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    public static string SyncCollectionBody(string token)
    {
        var doc = new XDocument(
            new XElement(
                D + "sync-collection",
                new XAttribute(XNamespace.Xmlns + "d", D.NamespaceName),
                new XElement(D + "sync-token", token),
                new XElement(D + "sync-level", "1"),
                new XElement(D + "prop", new XElement(D + "getetag"))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    public static string MultigetBody(IEnumerable<string> hrefs)
    {
        var doc = new XDocument(
            new XElement(
                C + "calendar-multiget",
                new XAttribute(XNamespace.Xmlns + "d", D.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "c", C.NamespaceName),
                new XElement(D + "prop", new XElement(D + "getetag"), new XElement(C + "calendar-data")),
                hrefs.Select(h => new XElement(D + "href", h))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    public static string CalendarQueryBody()
    {
        var doc = new XDocument(
            new XElement(
                C + "calendar-query",
                new XAttribute(XNamespace.Xmlns + "d", D.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "c", C.NamespaceName),
                new XElement(D + "prop", new XElement(D + "getetag"), new XElement(C + "calendar-data")),
                new XElement(
                    C + "filter",
                    new XElement(C + "comp-filter", new XAttribute("name", "VCALENDAR"), new XElement(C + "comp-filter", new XAttribute("name", "VEVENT"))))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    public static Multistatus ParseMultistatus(string xml)
    {
        var root = Parse(xml).Root;
        if (root is null || root.Name != D + "multistatus")
        {
            throw new SourceDataException("The server's response was not a WebDAV multistatus.");
        }

        var responses = root.Elements(D + "response").Select(ParseResponse).ToList();
        return new Multistatus(responses, root.Element(D + "sync-token")?.Value);
    }

    private static DavResponse ParseResponse(XElement response)
    {
        var hrefs = response.Elements(D + "href").Select(h => h.Value.Trim()).ToList();
        var propStats = response.Elements(D + "propstat")
            .Select(p => new DavPropStat(StatusCode(p.Element(D + "status")?.Value), p.Element(D + "prop") ?? new XElement(D + "prop")))
            .ToList();
        return new DavResponse(hrefs.FirstOrDefault() ?? string.Empty, propStats, StatusCode(response.Element(D + "status")?.Value));
    }

    /// <summary>Reads the code out of <c>HTTP/1.1 404 Not Found</c>; 0 when absent.</summary>
    internal static int StatusCode(string? statusLine)
    {
        var parts = statusLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: >= 2 } && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var code) ? code : 0;
    }
}

internal sealed record DavPropStat(int Status, XElement Prop);

/// <summary>One <c>response</c> of a multistatus.</summary>
/// <param name="Href">The resource, as the server wrote it.</param>
/// <param name="PropStats">Property groups with their statuses.</param>
/// <param name="Status">A response-level status (used for removed resources in <c>sync-collection</c>).</param>
internal sealed record DavResponse(string Href, IReadOnlyList<DavPropStat> PropStats, int Status)
{
    /// <summary>True when the server says the resource is gone (404 at response or property level).</summary>
    public bool IsGone => Status == 404 || (PropStats.Count > 0 && PropStats.All(p => p.Status == 404));

    /// <summary>The property from a successful (2xx or unstated) property group.</summary>
    public XElement? Prop(XName name) =>
        PropStats.Where(p => p.Status is 0 or (>= 200 and < 300)).Select(p => p.Prop.Element(name)).FirstOrDefault(e => e is not null);

    public string? Text(XName name) => Prop(name)?.Value;
}

internal sealed record Multistatus(IReadOnlyList<DavResponse> Responses, string? SyncToken)
{
    public DavResponse? First => Responses.Count > 0 ? Responses[0] : null;
}
